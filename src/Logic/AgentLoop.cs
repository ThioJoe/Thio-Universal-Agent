using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Thio_Universal_Agent.AI_API;

namespace Thio_Universal_Agent.Logic;

/// <summary>
/// The core observe-think-act loop that drives an autonomous agent session.
/// Takes a screenshot, sends it to the AI, parses the response, executes the action,
/// and repeats until the goal is achieved, the agent fails, or the session is cancelled.
/// When <see cref="Globals.ENABLE_TESTING"/> is true, captures verbose debug entries at every stage.
/// </summary>
public sealed class AgentLoop(
    IAiProvider aiProvider,
    IScreenProvider screenProvider,
    AgentActionExecutor executor,
    CoordinatePrompter coordinatePrompter,
    AppConfig appConfig,
    ILogger<AgentLoop> logger)
{
    private const int MaxSteps = 50;
    private const int MaxParseRetries = 2;
    private const int DefaultSettleDelayMs = 1500;
    private const int ContextResetInterval = 8;
    private const string ScreenMimeType = "image/jpeg";

    private readonly int _settleDelayMs = appConfig.Agent.SettleDelayMs;

    private readonly bool _enableContextReset = appConfig.Agent.EnableContextReset;

    /// <summary>
    /// Runs the agent loop to completion for the given session.
    /// This method is intended to be called on a background task.
    /// </summary>
    public async Task RunAsync(AgentSession session)
    {
        ArgumentNullException.ThrowIfNull(session);

        CancellationToken ct = session.Cts.Token;

        try
        {
            byte[] screenshot = screenProvider.CaptureScreen();
            //TODO: Add a config for whether to add grid overlay to regular conversation screenshots. For now default to true
            screenshot = coordinatePrompter.CreateFullGridOverlayImage(screenshot);

            string systemPrompt = AgentPromptBuilder.BuildSystemPrompt(session.Goal);
            var conversation = new AiConversation();

            logger.LogInformation("Agent session {SessionId} started. Goal: \"{Goal}\".", session.SessionId, session.Goal);

            var initialAiSw = Stopwatch.StartNew();
            AiResponse response = await aiProvider.ContinueConversationAsync(conversation, systemPrompt, screenshot, ScreenMimeType, ct)
                .ConfigureAwait(false);
            initialAiSw.Stop();

            if (!response.Success)
            {
                session.Status = AgentSessionStatus.Error;
                session.FinalResult = $"AI request failed on initial prompt: {response.ErrorMessage}";
                logger.LogError("Initial AI call failed for session {SessionId}: {Error}", session.SessionId, response.ErrorMessage);
                return;
            }

            // Debug tracking: the prompt/response that produced the AI output we're about to parse
            string lastPromptSent = systemPrompt;
            string lastRawResponse = response.Text;

            // Debug entries from context resets carry forward to the next step
            List<AgentDebugEntry>? carryOverDebug = Globals.ENABLE_TESTING ? [] : null;

            // Carry-forward: time spent on the AI call whose response is consumed by the next step
            long lastAiResponseMs = initialAiSw.ElapsedMilliseconds;

            for (int step = 1; step <= MaxSteps; step++)
            {
                ct.ThrowIfCancellationRequested();

                bool debugging = Globals.ENABLE_TESTING;
                List<AgentDebugEntry>? debugLog = debugging ? [] : null;

                // Prepend any carry-over entries from previous step's context reset
                if (debugLog is not null && carryOverDebug is { Count: > 0 })
                {
                    debugLog.AddRange(carryOverDebug);
                    carryOverDebug!.Clear();
                }

                // Debug: capture the prompt and raw response for this step
                if (debugging)
                {
                    debugLog!.Add(new AgentDebugEntry("Prompt Sent to AI", Text: lastPromptSent));
                    debugLog!.Add(new AgentDebugEntry("Screenshot Sent", Text: $"({screenshot.Length:N0} bytes, {ScreenMimeType})"));
                    debugLog!.Add(new AgentDebugEntry("Raw AI Response", Text: lastRawResponse));
                }

                // Parse the AI's response (with retries on malformed output)
                var stepStopwatch = Stopwatch.StartNew();
                var parseSw = Stopwatch.StartNew();
                AgentParsedResponse? parsed = await TryParseWithRetriesAsync(
                    conversation, response.Text, ct, debugLog).ConfigureAwait(false);
                parseSw.Stop();

                if (parsed is null)
                {
                    session.Status = AgentSessionStatus.Failed;
                    session.FinalResult = "Agent produced unparseable responses after retries.";
                    logger.LogWarning("Session {SessionId} failed: repeated parse failures.", session.SessionId);

                    // Record a debug-only step so the UI shows what happened
                    if (debugLog is { Count: > 0 })
                    {
                        var failAction = new AgentAction(AgentActionKind.Fail, Reason: "Unparseable AI response");
                        var failResult = new ActionExecutionResult(false, "Parse failed after retries.", IsTerminal: true, GoalAchieved: false);
                        var failStep = new AgentStep(step, "(parse failure)", failAction, failResult, DateTimeOffset.UtcNow, stepStopwatch.ElapsedMilliseconds, debugLog);
                        session.Steps.Add(failStep);
                        await session.RaiseStepCompletedAsync(failStep).ConfigureAwait(false);
                    }
                    return;
                }

                if (debugging)
                    debugLog!.Add(new AgentDebugEntry("Parse Result", Text: $"Success → {parsed.Action.Kind}: {FormatActionDetail(parsed.Action)}"));

                logger.LogInformation(
                    "Step {Step}: THOUGHT: {Thought} | ACTION: {ActionKind} {ActionDetail}",
                    step, parsed.Thought, parsed.Action.Kind, FormatActionDetail(parsed.Action));

                // Notify the UI immediately so it can show what the agent is about to do
                // before potentially slow work (e.g. coordinate resolution) begins.
                var preview = new AgentStepPreview(step, parsed.Thought, parsed.Action);
                await session.RaiseStepStartingAsync(preview).ConfigureAwait(false);

                // Create a progress callback that streams each executor debug entry to the UI in real-time
                int currentStep = step;
                Func<AgentDebugEntry, Task>? executorProgress = debugging
                    ? async entry => await session.RaiseSubStepUpdateAsync(new AgentSubStep(currentStep, entry)).ConfigureAwait(false)
                    : null;

                // Execute the action (executor adds its own debug entries)
                var executeSw = Stopwatch.StartNew();
                ActionExecutionResult result = await ExecuteWithTargetRecoveryAsync(
                    parsed.Action, screenshot, conversation, ct, debugLog, executorProgress).ConfigureAwait(false);
                executeSw.Stop();

                if (debugging)
                    debugLog!.Add(new AgentDebugEntry("Execution Result", Text: $"Success={result.Success} | {result.Summary}"));

                stepStopwatch.Stop();

                var timings = new StepTimings(
                    AiResponseMs:      lastAiResponseMs,
                    ParseMs:           parseSw.ElapsedMilliseconds,
                    ExecutionMs:       executeSw.ElapsedMilliseconds,
                    CoordResolutionMs: result.CoordResolutionMs);

                // Record the step
                var agentStep = new AgentStep(step, parsed.Thought, parsed.Action, result, DateTimeOffset.UtcNow,
                    stepStopwatch.ElapsedMilliseconds,
                    debugLog is { Count: > 0 } ? debugLog : null,
                    timings);
                session.Steps.Add(agentStep);
                await session.RaiseStepCompletedAsync(agentStep).ConfigureAwait(false);

                // Check for terminal actions
                if (result.IsTerminal)
                {
                    session.Status = result.GoalAchieved ? AgentSessionStatus.Completed : AgentSessionStatus.Failed;
                    session.FinalResult = result.Summary;
                    logger.LogInformation("Session {SessionId} terminated at step {Step}: {Summary}", session.SessionId, step, result.Summary);
                    return;
                }

                // Settle delay — let the UI update before next screenshot.
                // Skip for Wait actions since the wait itself is already the settle time.
                if (parsed.Action.Kind != AgentActionKind.Wait)
                    await Task.Delay(_settleDelayMs, ct).ConfigureAwait(false);

                // Honour a user-requested pause before doing any more work.
                // The AI response in `response` is preserved across the pause so nothing is discarded.
                await session.WaitIfPausedAsync(ct).ConfigureAwait(false);

                // Observe: take a new screenshot
                screenshot = screenProvider.CaptureScreen();
                screenshot = coordinatePrompter.CreateFullGridOverlayImage(screenshot);

                // Episodic context reset to prevent payload bloat
                if (_enableContextReset && step % ContextResetInterval == 0)
                {
                    conversation = await ResetContextAsync(conversation, session.Goal, screenshot, ct, carryOverDebug).ConfigureAwait(false);
                }

                // Feed the result + new screenshot back to the AI
                string feedback = AgentPromptBuilder.BuildFeedbackPrompt(result);

                var aiFeedbackSw = Stopwatch.StartNew();
                response = await aiProvider.ContinueConversationAsync(
                    conversation, feedback, screenshot, ScreenMimeType, ct).ConfigureAwait(false);
                aiFeedbackSw.Stop();

                if (!response.Success)
                {
                    session.Status = AgentSessionStatus.Error;
                    session.FinalResult = $"AI request failed at step {step}: {response.ErrorMessage}";
                    logger.LogError("AI call failed at step {Step} for session {SessionId}: {Error}", step, session.SessionId, response.ErrorMessage);
                    return;
                }

                lastAiResponseMs = aiFeedbackSw.ElapsedMilliseconds;

                // Track for next step's debug output
                if (debugging)
                {
                    lastPromptSent = feedback;
                    lastRawResponse = response.Text;
                }
            }

            // Exceeded max steps
            session.Status = AgentSessionStatus.Failed;
            session.FinalResult = $"Exceeded maximum of {MaxSteps} steps without completing the goal.";
            logger.LogWarning("Session {SessionId} exceeded max steps ({MaxSteps}).", session.SessionId, MaxSteps);
        }
        catch (OperationCanceledException)
        {
            session.Status = AgentSessionStatus.Cancelled;
            session.FinalResult = "Session was cancelled.";
            logger.LogInformation("Session {SessionId} was cancelled.", session.SessionId);
        }
        catch (Exception ex)
        {
            session.Status = AgentSessionStatus.Error;
            session.FinalResult = $"Unexpected error: {ex.Message}";
            logger.LogError(ex, "Unexpected error in session {SessionId}.", session.SessionId);
        }
        finally
        {
            // Always signal termination so SSE stream waiters are unblocked regardless of how the loop exits.
            session.SignalTerminated();
        }
    }

    /// <summary>
    /// Attempts to parse the AI response, sending correction prompts on failure up to <see cref="MaxParseRetries"/> times.
    /// </summary>
    private async Task<AgentParsedResponse?> TryParseWithRetriesAsync(
        AiConversation conversation, string responseText, CancellationToken ct,
        List<AgentDebugEntry>? debugLog = null)
    {
        for (int attempt = 0; attempt <= MaxParseRetries; attempt++)
        {
            if (AgentActionParser.TryParse(responseText, out AgentParsedResponse? parsed, out string? error))
                return parsed;

            logger.LogWarning("Parse attempt {Attempt} failed: {Error}", attempt + 1, error);
            debugLog?.Add(new AgentDebugEntry($"Parse Attempt {attempt + 1} Failed", Text: error));

            if (attempt == MaxParseRetries)
                break;

            // Send a correction prompt and get a new response
            string correction = AgentPromptBuilder.BuildParseErrorPrompt(error);
            debugLog?.Add(new AgentDebugEntry($"Parse Correction Prompt (Attempt {attempt + 2})", Text: correction));

            AiResponse retryResponse = await aiProvider
                .ContinueConversationAsync(conversation, correction, ct)
                .ConfigureAwait(false);

            if (!retryResponse.Success)
            {
                logger.LogError("AI correction call failed: {Error}", retryResponse.ErrorMessage);
                debugLog?.Add(new AgentDebugEntry("Parse Correction AI Error", Text: retryResponse.ErrorMessage ?? "Unknown error"));
                break;
            }

            debugLog?.Add(new AgentDebugEntry($"Parse Correction AI Response (Attempt {attempt + 2})", Text: retryResponse.Text));
            responseText = retryResponse.Text;
        }

        return null;
    }

    /// <summary>
    /// Executes an action. If it's a coordinate-based action and the <see cref="CoordinatePrompter"/>
    /// fails to locate the target, reports the failure back to the AI instead of crashing.
    /// </summary>
    private async Task<ActionExecutionResult> ExecuteWithTargetRecoveryAsync(
        AgentAction action, byte[] screenshot, AiConversation conversation, CancellationToken ct,
        List<AgentDebugEntry>? debugLog = null, Func<AgentDebugEntry, Task>? onProgress = null)
    {
        ActionExecutionResult result = await executor
            .ExecuteAsync(action, screenshot, ct, onProgress)
            .ConfigureAwait(false);

        // Merge executor's debug entries into our step log
        if (debugLog is not null && result.DebugEntries is { Count: > 0 })
            debugLog.AddRange(result.DebugEntries);

        if (!result.Success && action.Target is not null)
        {
            // Coordinate resolution failed — tell the AI so it can adapt
            logger.LogWarning("Target resolution failed for \"{Target}\": {Summary}", action.Target, result.Summary);

            string recovery = AgentPromptBuilder.BuildTargetNotFoundPrompt(action.Target);
            var recoveryEntry = new AgentDebugEntry("Target Not Found — Recovery Prompt", Text: recovery);
            debugLog?.Add(recoveryEntry);
            if (onProgress is not null) await onProgress(recoveryEntry).ConfigureAwait(false);

            AiResponse recoveryResponse = await aiProvider.ContinueConversationAsync(conversation, recovery, ct).ConfigureAwait(false);
            var responseEntry = new AgentDebugEntry("Target Not Found — AI Recovery Response", Text: recoveryResponse.Text);
            debugLog?.Add(responseEntry);
            if (onProgress is not null) await onProgress(responseEntry).ConfigureAwait(false);
        }

        return result;
    }

    /// <summary>
    /// Resets the conversation context to prevent token bloat.
    /// Asks the AI to summarize progress, then starts a fresh conversation
    /// with the summary and the latest screenshot.
    /// </summary>
    private async Task<AiConversation> ResetContextAsync(
        AiConversation oldConversation, string goal, byte[] latestScreenshot, CancellationToken ct,
        List<AgentDebugEntry>? debugLog = null)
    {
        logger.LogInformation("Performing episodic context reset.");
        debugLog?.Add(new AgentDebugEntry("Context Reset", Text: "Initiating episodic context reset to reduce token usage."));

        // Ask the AI to summarize
        string summarizePrompt = AgentPromptBuilder.BuildSummarizationPrompt();
        debugLog?.Add(new AgentDebugEntry("Context Reset — Summarization Prompt", Text: summarizePrompt));

        AiResponse summaryResponse = await aiProvider
            .ContinueConversationAsync(oldConversation, summarizePrompt, ct)
            .ConfigureAwait(false);

        string summary = (summaryResponse.Success && !string.IsNullOrWhiteSpace(summaryResponse.Text))
            ? summaryResponse.Text
            : "Previous progress summary unavailable.";

        debugLog?.Add(new AgentDebugEntry("Context Reset — AI Summary", Text: summary));

        // Start a fresh conversation with the summary baked in
        var newConversation = new AiConversation();
        string resetPrompt = AgentPromptBuilder.BuildContextResetPrompt(goal, summary);
        debugLog?.Add(new AgentDebugEntry("Context Reset — New System Prompt", Text: resetPrompt));

        AiResponse resetResponse = await aiProvider
            .ContinueConversationAsync(newConversation, resetPrompt, latestScreenshot, ScreenMimeType, ct)
            .ConfigureAwait(false);

        if (!resetResponse.Success)
        {
            logger.LogWarning("Context reset AI call failed: {Error}. Continuing with old context.", resetResponse.ErrorMessage);
            debugLog?.Add(new AgentDebugEntry("Context Reset — Failed", Text: resetResponse.ErrorMessage ?? "Unknown error"));
        }
        else
        {
            debugLog?.Add(new AgentDebugEntry("Context Reset — AI Response", Text: resetResponse.Text));
        }

        return resetResponse.Success ? newConversation : oldConversation;
    }

    private static string FormatActionDetail(AgentAction action) => action.Kind switch
    {
        AgentActionKind.LeftClick or AgentActionKind.RightClick or AgentActionKind.DoubleClick
            or AgentActionKind.MiddleClick or AgentActionKind.MoveMouse => $"\"{action.Target}\"",
        AgentActionKind.TypeText => $"\"{action.Text}\"",
        AgentActionKind.KeyCombo => $"{(action.Modifiers.HasFlag(ModifierKeys.Ctrl) ? "Ctrl+" : "")}{(action.Modifiers.HasFlag(ModifierKeys.Shift) ? "Shift+" : "")}{(action.Modifiers.HasFlag(ModifierKeys.Alt) ? "Alt+" : "")}{(action.Modifiers.HasFlag(ModifierKeys.Win) ? "Win+" : "")}{action.Key}",
        AgentActionKind.ScrollUp or AgentActionKind.ScrollDown => action.Amount.ToString(),
        AgentActionKind.Wait => $"{action.Amount}s",
        AgentActionKind.Fail => action.Reason ?? "",
        _ => "",
    };
}
