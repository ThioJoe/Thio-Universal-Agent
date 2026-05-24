using System.Diagnostics;
using Thio_Universal_Agent.Handlers;

namespace Thio_Universal_Agent;

/// <summary>
/// The core observe-think-act loop that drives an autonomous agent session.
/// Takes a screenshot, sends it to the AI, parses the response, executes the action,
/// and repeats until the goal is achieved, the agent fails, or the session is cancelled.
/// When <see cref="GeneralConfig.EnableDebugMode"/> is true, captures verbose debug entries at every stage.
/// </summary>
public sealed partial class AgentLoop(
    IAiProvider aiProvider,
    IScreenProvider screenProvider,
    AgentActionExecutor executor,
    AppConfig appConfig,
    ILogger<AgentLoop> logger)
{
    private const string ScreenMimeType = "image/jpeg";

    private readonly int _maxSteps = appConfig.General.MaxSteps;
    private readonly int _maxParseRetries = appConfig.General.MaxParseRetries;
    private readonly int _contextResetInterval = appConfig.General.ContextResetInterval;
    private readonly int _settleDelayMs = appConfig.General.SettleDelayMs;
    private readonly int _queueSettleDelayMs = appConfig.General.QueueSettleDelayMs;

    private readonly bool _enableContextReset = appConfig.General.EnableContextReset;

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
            var screenshot = screenProvider.CaptureScreen();
            //TODO: Add a config for whether to add grid overlay to regular conversation screenshots. For now default to true
            screenshot.Processed = CoordinatePrompter.CreateFullGridOverlayImage(screenshot.Original, appConfig);

            string systemPrompt = AgentPromptBuilder.BuildSystemPrompt(session.Goal, appConfig.General.MaxQueueSize, appConfig.General.SystemPromptTemplate);
            var conversation = new AiConversation();

            LogSessionStarted(logger, session.SessionId, session.Goal);

            var initialAiSw = Stopwatch.StartNew();
            AiResponse response = await aiProvider.ContinueConversationAsync(conversation, systemPrompt, screenshot.Processed, ScreenMimeType, ct)
                .ConfigureAwait(false);
            initialAiSw.Stop();

            if (!response.Success)
            {
                session.Status = AgentSessionStatus.Error;
                session.FinalResult = $"AI request failed on initial prompt: {response.ErrorMessage}";
                LogInitialAiFailed(logger, session.SessionId, response.ErrorMessage);
                return;
            }

            // Debug tracking: the prompt/response that produced the AI output we're about to parse
            string lastPromptSent = systemPrompt;
            string lastRawResponse = response.Text;

            // Debug entries from context resets carry forward to the next step
            List<AgentDebugEntry>? carryOverDebug = appConfig.General.EnableDebugMode ? [] : null;

            // Carry-forward: time spent on the AI call whose response is consumed by the next step
            long lastAiResponseMs = initialAiSw.ElapsedMilliseconds;

            for (int step = 1; step <= _maxSteps; step++)
            {
                ct.ThrowIfCancellationRequested();

                bool debugging = appConfig.General.EnableDebugMode;
                List<AgentDebugEntry>? debugLog = [];

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
                    debugLog!.Add(new AgentDebugEntry("Screenshot Sent", Text: $"({screenshot.Processed.Length:N0} bytes, {ScreenMimeType})"));
                    debugLog!.Add(new AgentDebugEntry("Raw AI Response", Text: lastRawResponse));
                }

                // Parse the AI's response (with retries on malformed output)
                var stepStopwatch = Stopwatch.StartNew();
                var parseSw = Stopwatch.StartNew();
                var (parsed, parseRejections) = await TryParseWithRetriesAsync(
                    conversation, response.Text, ct, debugLog).ConfigureAwait(false);
                parseSw.Stop();

                // Emit a separate red step entry for each rejected attempt so they're visible in the log.
                if (parseRejections is not null)
                {
                    foreach (var (rawText, rejectionError) in parseRejections)
                    {
                        var rejectedAction = new AgentAction(AgentActionKind.Fail, Reason: rejectionError);
                        var rejectedResult = new ActionExecutionResult(false, rejectionError, IsTerminal: false, GoalAchieved: false);
                        var rejectedStep = new AgentStep(step, rawText, rejectedAction, rejectedResult,
                            DateTimeOffset.UtcNow, 0, IsParseRejected: true);
                        session.Steps.Add(rejectedStep);
                        await session.RaiseStepCompletedAsync(rejectedStep).ConfigureAwait(false);
                    }
                }

                if (parsed is null)
                {
                    session.Status = AgentSessionStatus.Failed;
                    session.FinalResult = "Agent produced unparseable responses after retries.";
                    LogParseFailures(logger, session.SessionId);

                    // Commit a terminal fail step so the UI knows the session ended here.
                    var failAction = new AgentAction(AgentActionKind.Fail, Reason: "Unparseable AI response");
                    var failResult = new ActionExecutionResult(false, "Parse failed after retries.", IsTerminal: true, GoalAchieved: false);
                    var failStep = new AgentStep(step, "(parse failure)", failAction, failResult, DateTimeOffset.UtcNow, stepStopwatch.ElapsedMilliseconds,
                        debugLog is { Count: > 0 } ? debugLog : null);
                    session.Steps.Add(failStep);
                    await session.RaiseStepCompletedAsync(failStep).ConfigureAwait(false);
                    return;
                }

                if (debugging)
                    debugLog!.Add(new AgentDebugEntry("Parse Result", Text: $"Success → {parsed.Action.Kind}: {FormatActionDetail(parsed.Action)}"));

                if (logger.IsEnabled(LogLevel.Information))
                    #pragma warning disable CA1873 // Avoid potentially expensive logging
                    LogStepAction(logger, step, parsed.Thought, parsed.Action.Kind, FormatActionDetail(parsed.Action));
                    #pragma warning restore CA1873 // Avoid potentially expensive logging

                // Determine the ordered list of actions for this iteration.
                // QueuedActions is non-null only when the AI used QUEUE: syntax with ≥2 actions.
                IReadOnlyList<AgentAction> actionsToRun = parsed.QueuedActions ?? [parsed.Action];
                bool isBatch = actionsToRun.Count > 1;

                // Emit ONE preview for the whole batch — UI shows the first action and a queue badge.
                // This is intentionally done BEFORE the pause gate so the user can see the AI's
                // plan even while the session is paused.
                var preview = new AgentStepPreview(step, parsed.Thought, actionsToRun[0], actionsToRun.Count);
                await session.RaiseStepStartingAsync(preview).ConfigureAwait(false);

                // If the user paused while the AI was responding, block here until resumed.
                // Skip the countdown entirely when the next action is already flagged for cancellation —
                // there's nothing pending to execute, so there's no need to give the user preparation time.
                if (session.HasCancelNextAction)
                    await session.WaitIfPausedAsync(ct).ConfigureAwait(false);
                else
                    await session.WaitIfPausedWithCountdownAsync(5, ct).ConfigureAwait(false);

                // If the user submitted guidance with "cancel next action" while paused, skip
                // execution entirely, emit a cancelled step to the log, then redirect the AI.
                if (session.ConsumeCancelNextAction())
                {
                    var cancelledResult = new ActionExecutionResult(false, "Action cancelled by user.", IsTerminal: false, GoalAchieved: false);
                    var cancelTimings = new StepTimings(lastAiResponseMs, parseSw.ElapsedMilliseconds, 0, null);
                    var cancelledStep = new AgentStep(step, parsed.Thought, actionsToRun[0], cancelledResult,
                        DateTimeOffset.UtcNow, parseSw.ElapsedMilliseconds,
                        debugLog is { Count: > 0 } ? debugLog : null, cancelTimings);
                    session.Steps.Add(cancelledStep);
                    await session.RaiseStepCompletedAsync(cancelledStep).ConfigureAwait(false);

                    // Take a fresh screenshot so the AI has current state.
                    screenshot = screenProvider.CaptureScreen();
                    screenshot.Processed = CoordinatePrompter.CreateFullGridOverlayImage(screenshot.Original, appConfig);

                    // Drain guidance (may include the cancellation message itself) and tell the AI.
                    var cancelGuidance = new List<string>();
                    session.DrainGuidance(cancelGuidance);
                    string cancelFeedback = AgentPromptBuilder.BuildActionCancelledPrompt(cancelGuidance);

                    if (debugging) lastPromptSent = cancelFeedback;

                    var cancelAiSw = Stopwatch.StartNew();
                    response = await aiProvider.ContinueConversationAsync(
                        conversation, cancelFeedback, screenshot.Processed, ScreenMimeType, ct).ConfigureAwait(false);
                    cancelAiSw.Stop();
                    lastAiResponseMs = cancelAiSw.ElapsedMilliseconds;

                    if (!response.Success)
                    {
                        session.Status = AgentSessionStatus.Error;
                        session.FinalResult = $"AI request failed after user cancellation at step {step}: {response.ErrorMessage}";
                        LogAiCallFailed(logger, step, session.SessionId, response.ErrorMessage);
                        return;
                    }

                    if (debugging) lastRawResponse = response.Text;

                    // Restart the loop iteration with the fresh AI response.
                    step--;  // counter will be incremented by the for-loop
                    continue;
                }

                // Single progress callback — all sub-steps are attributed to the parent step number.
                async Task executorProgress(AgentDebugEntry entry)
                {
                    if (!debugging && entry.ImageBase64 is null) return;
                    await session.RaiseSubStepUpdateAsync(new AgentSubStep(step, entry)).ConfigureAwait(false);
                }

                var batchResults = new List<ActionExecutionResult>(actionsToRun.Count);
                List<QueuedSubStep>? subSteps = isBatch ? new(actionsToRun.Count) : null;
                bool batchTerminated = false;

                for (int qi = 0; qi < actionsToRun.Count; qi++)
                {
                    ct.ThrowIfCancellationRequested();

                    AgentAction currentAction = actionsToRun[qi];

                    // First action shares the main debugLog; each subsequent action gets its own segment.
                    List<AgentDebugEntry>? qiDebugLog = qi == 0 ? debugLog : (debugging ? [] : null);

                    var executeSw = Stopwatch.StartNew();
                    ActionExecutionResult result = await ExecuteWithTargetRecoveryAsync(
                        currentAction, screenshot, conversation, ct, qiDebugLog, executorProgress).ConfigureAwait(false);
                    executeSw.Stop();

                    if (debugging)
                        qiDebugLog!.Add(new AgentDebugEntry("Execution Result", Text: $"Success={result.Success} | {result.Summary}"));

                    if (isBatch)
                    {
                        var subTimings = new StepTimings(
                            AiResponseMs:      qi == 0 ? lastAiResponseMs : 0,
                            ParseMs:           qi == 0 ? parseSw.ElapsedMilliseconds : 0,
                            ExecutionMs:       executeSw.ElapsedMilliseconds,
                            CoordResolutionMs: result.CoordResolutionMs);
                        subSteps!.Add(new QueuedSubStep(qi, currentAction, result, executeSw.ElapsedMilliseconds,
                            qiDebugLog is { Count: > 0 } ? qiDebugLog : null, subTimings));
                    }

                    batchResults.Add(result);

                    if (result.IsTerminal || !result.Success)
                    {
                        batchTerminated = result.IsTerminal;
                        break;
                    }

                    // No full settle between queued actions — but a short pause lets the OS register the input.
                    if (currentAction.Kind != AgentActionKind.Wait && qi < actionsToRun.Count - 1 && _queueSettleDelayMs > 0)
                        await Task.Delay(_queueSettleDelayMs, ct).ConfigureAwait(false);
                }

                stepStopwatch.Stop();

                ActionExecutionResult lastResult = batchResults[^1];

                if (debugging && isBatch)
                    debugLog!.Add(new AgentDebugEntry("Batch Result",
                        Text: $"Executed {batchResults.Count}/{actionsToRun.Count} action(s). Terminal={batchTerminated}"));

                var stepTimings = new StepTimings(
                    AiResponseMs:      lastAiResponseMs,
                    ParseMs:           parseSw.ElapsedMilliseconds,
                    ExecutionMs:       stepStopwatch.ElapsedMilliseconds - parseSw.ElapsedMilliseconds,
                    CoordResolutionMs: isBatch ? null : lastResult.CoordResolutionMs);

                // Emit ONE completed step for the entire batch.
                var agentStep = new AgentStep(step, parsed.Thought, actionsToRun[0], lastResult,
                    DateTimeOffset.UtcNow, stepStopwatch.ElapsedMilliseconds,
                    debugLog is { Count: > 0 } ? debugLog : null,
                    stepTimings,
                    subSteps);
                session.Steps.Add(agentStep);
                await session.RaiseStepCompletedAsync(agentStep).ConfigureAwait(false);

                if (batchTerminated)
                {
                    session.Status = lastResult.GoalAchieved ? AgentSessionStatus.Completed : AgentSessionStatus.Failed;
                    session.FinalResult = lastResult.Summary;
                    LogSessionTerminated(logger, session.SessionId, step, lastResult.Summary);
                    return;
                }

                // Settle delay after the batch (skip if last action was a Wait).
                AgentAction lastAction = actionsToRun[batchResults.Count - 1];
                if (lastAction.Kind != AgentActionKind.Wait)
                    await Task.Delay(_settleDelayMs, ct).ConfigureAwait(false);

                // Honour a user-requested pause before doing any more work.
                await session.WaitIfPausedAsync(ct).ConfigureAwait(false);

                // Observe: take a new screenshot
                screenshot = screenProvider.CaptureScreen();
                screenshot.Processed = CoordinatePrompter.CreateFullGridOverlayImage(screenshot.Original, appConfig);

                // Episodic context reset to prevent payload bloat
                if (_enableContextReset && step % _contextResetInterval == 0)
                {
                    conversation = await ResetContextAsync(conversation, session.Goal, screenshot, ct, carryOverDebug).ConfigureAwait(false);
                }

                // Feed all results + new screenshot back to the AI in a single message.
                string feedback = batchResults.Count > 1
                    ? AgentPromptBuilder.BuildQueuedFeedbackPrompt(batchResults)
                    : AgentPromptBuilder.BuildFeedbackPrompt(lastResult);

                // Prepend any user guidance messages that arrived since the last step.
                // (Skip if cancel-next-action was already set — that path drains guidance separately.)
                var guidanceMessages = new List<string>();
                if (!session.HasCancelNextAction && session.DrainGuidance(guidanceMessages))
                    feedback = AgentPromptBuilder.BuildGuidancePrompt(guidanceMessages) + feedback;

                var aiFeedbackSw = Stopwatch.StartNew();
                response = await aiProvider.ContinueConversationAsync(
                    conversation, feedback, screenshot.Processed, ScreenMimeType, ct).ConfigureAwait(false);
                aiFeedbackSw.Stop();

                if (!response.Success)
                {
                    session.Status = AgentSessionStatus.Error;
                    session.FinalResult = $"AI request failed at step {step}: {response.ErrorMessage}";
                    LogAiCallFailed(logger, step, session.SessionId, response.ErrorMessage);
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
            session.FinalResult = $"Exceeded maximum of {_maxSteps} steps without completing the goal.";
            LogMaxStepsExceeded(logger, session.SessionId, _maxSteps);
        }
        catch (OperationCanceledException)
        {
            session.Status = AgentSessionStatus.Cancelled;
            session.FinalResult = "Session was cancelled.";
            LogSessionCancelled(logger, session.SessionId);
        }
        catch (Exception ex)
        {
            session.Status = AgentSessionStatus.Error;
            session.FinalResult = $"Unexpected error: {ex.Message}";
            LogUnexpectedError(logger, ex, session.SessionId);
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
    private async Task<(AgentParsedResponse? Parsed, List<(string RawText, string Error)>? ParseRejections)> TryParseWithRetriesAsync(
        AiConversation conversation, string responseText, CancellationToken ct,
        List<AgentDebugEntry>? debugLog = null)
    {
        List<(string RawText, string Error)>? parseRejections = null;

        for (int attempt = 0; attempt <= _maxParseRetries; attempt++)
        {
            if (AgentActionParser.TryParse(responseText, appConfig.General.MaxQueueSize, out AgentParsedResponse? parsed, out string? error))
                return (parsed, parseRejections);

            parseRejections ??= [];
            parseRejections.Add((responseText, error));

            LogParseAttemptFailed(logger, attempt + 1, error);
            debugLog?.Add(new AgentDebugEntry($"Parse Attempt {attempt + 1} Failed", Text: error));

            if (attempt == _maxParseRetries)
                break;

            // Send a correction prompt and get a new response
            string correction = AgentPromptBuilder.BuildParseErrorPrompt(error);
            debugLog?.Add(new AgentDebugEntry($"Parse Correction Prompt (Attempt {attempt + 2})", Text: correction));

            AiResponse retryResponse = await aiProvider
                .ContinueConversationAsync(conversation, correction, ct)
                .ConfigureAwait(false);

            if (!retryResponse.Success)
            {
                LogCorrectionAiFailed(logger, retryResponse.ErrorMessage);
                debugLog?.Add(new AgentDebugEntry("Parse Correction AI Error", Text: retryResponse.ErrorMessage ?? "Unknown error"));
                break;
            }

            debugLog?.Add(new AgentDebugEntry($"Parse Correction AI Response (Attempt {attempt + 2})", Text: retryResponse.Text));
            responseText = retryResponse.Text;
        }

        return (null, parseRejections);
    }

    /// <summary>
    /// Executes an action.
    /// fails to locate the target, reports the failure back to the AI instead of crashing.
    /// </summary>
    private async Task<ActionExecutionResult> ExecuteWithTargetRecoveryAsync(
        AgentAction action, Screenshot screenshot,
        AiConversation conversation,
        CancellationToken ct,
        List<AgentDebugEntry>? debugLog = null,
        Func<AgentDebugEntry, Task>? onProgress = null
        )
    {
        ActionExecutionResult result = await executor
            .ExecuteAsync(action, screenshot, onProgress, ct)
            .ConfigureAwait(false);

        // Merge executor's debug entries into our step log
        if (debugLog is not null && result.DebugEntries is { Count: > 0 })
            debugLog.AddRange(result.DebugEntries);

        if (!result.Success && action.Target is not null)
        {
            // Coordinate resolution failed — tell the AI so it can adapt
            LogTargetResolutionFailed(logger, action.Target, result.Summary);

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
        AiConversation oldConversation, string goal, Screenshot latestScreenshot, CancellationToken ct,
        List<AgentDebugEntry>? debugLog = null)
    {
        LogContextReset(logger);
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
        string resetPrompt = AgentPromptBuilder.BuildContextResetPrompt(goal, summary, appConfig.General.MaxQueueSize, appConfig.General.SystemPromptTemplate);
        debugLog?.Add(new AgentDebugEntry("Context Reset — New System Prompt", Text: resetPrompt));

        AiResponse resetResponse = await aiProvider
            .ContinueConversationAsync(newConversation, resetPrompt, latestScreenshot.Processed, ScreenMimeType, ct)
            .ConfigureAwait(false);

        if (!resetResponse.Success)
        {
            LogContextResetFailed(logger, resetResponse.ErrorMessage);
            debugLog?.Add(new AgentDebugEntry("Context Reset — Failed", Text: resetResponse.ErrorMessage ?? "Unknown error"));
        }
        else
        {
            debugLog?.Add(new AgentDebugEntry("Context Reset — AI Response", Text: resetResponse.Text));
        }

        return resetResponse.Success ? newConversation : oldConversation;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Agent session {SessionId} started. Goal: \"{Goal}\".")]
    private static partial void LogSessionStarted(ILogger logger, string sessionId, string goal);

    [LoggerMessage(Level = LogLevel.Error, Message = "Initial AI call failed for session {SessionId}: {Error}")]
    private static partial void LogInitialAiFailed(ILogger logger, string sessionId, string? error);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Session {SessionId} failed: repeated parse failures.")]
    private static partial void LogParseFailures(ILogger logger, string sessionId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Step {Step}: THOUGHT: {Thought} | ACTION: {ActionKind} {ActionDetail}")]
    private static partial void LogStepAction(ILogger logger, int step, string thought, AgentActionKind actionKind, string actionDetail);

    [LoggerMessage(Level = LogLevel.Information, Message = "Session {SessionId} terminated at step {Step}: {Summary}")]
    private static partial void LogSessionTerminated(ILogger logger, string sessionId, int step, string summary);

    [LoggerMessage(Level = LogLevel.Error, Message = "AI call failed at step {Step} for session {SessionId}: {Error}")]
    private static partial void LogAiCallFailed(ILogger logger, int step, string sessionId, string? error);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Session {SessionId} exceeded max steps ({MaxSteps}).")]
    private static partial void LogMaxStepsExceeded(ILogger logger, string sessionId, int maxSteps);

    [LoggerMessage(Level = LogLevel.Information, Message = "Session {SessionId} was cancelled.")]
    private static partial void LogSessionCancelled(ILogger logger, string sessionId);

    [LoggerMessage(Level = LogLevel.Error, Message = "Unexpected error in session {SessionId}.")]
    private static partial void LogUnexpectedError(ILogger logger, Exception ex, string sessionId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Parse attempt {Attempt} failed: {Error}")]
    private static partial void LogParseAttemptFailed(ILogger logger, int attempt, string? error);

    [LoggerMessage(Level = LogLevel.Error, Message = "AI correction call failed: {Error}")]
    private static partial void LogCorrectionAiFailed(ILogger logger, string? error);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Target resolution failed for \"{Target}\": {Summary}")]
    private static partial void LogTargetResolutionFailed(ILogger logger, string? target, string summary);

    [LoggerMessage(Level = LogLevel.Information, Message = "Performing episodic context reset.")]
    private static partial void LogContextReset(ILogger logger);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Context reset AI call failed: {Error}. Continuing with old context.")]
    private static partial void LogContextResetFailed(ILogger logger, string? error);

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
