using System.Text.Json;

namespace Thio_Universal_Agent.Endpoints;

/// <summary>
/// Minimal API endpoints that expose the agent session lifecycle to the web UI.
/// </summary>
internal static class AgentEndpoints
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    internal static void MapAgentEndpoints(this WebApplication app)
    {
        RouteGroupBuilder group = app.MapGroup("/api/agent");

        // Check if verbose debug output is enabled
        group.MapGet("/debug-enabled", (AppConfig appConfig) => Results.Ok(new { enabled = appConfig.General.EnableDebugMode }));

        // List connected monitors
        group.MapGet("/monitors", (IScreenProvider screenProvider) =>
            Results.Ok(screenProvider.GetMonitors()));

        // Start a new agent session
        group.MapPost("/start", (AgentStartRequest req, AgentSessionManager manager, AppConfig appConfig) =>
        {
            if (string.IsNullOrWhiteSpace(req.Goal))
                return Results.BadRequest(new { error = "Goal is required." });

            AiProviderType? activeProvider = appConfig.General.ActiveProvider;
            if (activeProvider is null)
                return Results.BadRequest(new { error = "No active AI provider is configured. Select one in Configuration first." });

            // Use the per-request key if provided; otherwise fall back to whatever is already in AppConfig for the active provider.
            string? defaultKey = activeProvider.Value switch
            {
                AiProviderType.ChatGPT => appConfig.OpenAI.ApiKey,
                AiProviderType.OpenAICompatible => appConfig.OpenAICompatible.ApiKey,
                AiProviderType.Claude => appConfig.Anthropic.ApiKey,
                AiProviderType.Onnx => null,
                AiProviderType.Gemini => appConfig.Gemini.ApiKey,
                _ => throw new InvalidOperationException($"Unsupported AI provider: {activeProvider.Value}.")
            };

            string? resolvedKey = string.IsNullOrWhiteSpace(req.ApiKey) ? defaultKey : req.ApiKey;
            bool requiresApiKey = activeProvider.Value is not AiProviderType.OpenAICompatible and not AiProviderType.Onnx;
            if (requiresApiKey && string.IsNullOrWhiteSpace(resolvedKey))
                return Results.BadRequest(new { error = "No API key found. Set one in Configuration or enter it here." });

            if (activeProvider == AiProviderType.ChatGPT)
            {
                appConfig.OpenAI.ApiKey = resolvedKey;
                if (!string.IsNullOrWhiteSpace(req.Model)) appConfig.OpenAI.Model = req.Model;
            }
            else if (activeProvider == AiProviderType.OpenAICompatible)
            {
                appConfig.OpenAICompatible.ApiKey = resolvedKey;
                if (!string.IsNullOrWhiteSpace(req.Model)) appConfig.OpenAICompatible.Model = req.Model;
            }
            else if (activeProvider == AiProviderType.Claude)
            {
                appConfig.Anthropic.ApiKey = resolvedKey;
                if (!string.IsNullOrWhiteSpace(req.Model)) appConfig.Anthropic.Model = req.Model;
            }
            else if (activeProvider == AiProviderType.Onnx)
            {
                if (!string.IsNullOrWhiteSpace(req.Model)) appConfig.Onnx.Model = req.Model;
            }
            else
            {
                appConfig.Gemini.ApiKey = resolvedKey;
                if (!string.IsNullOrWhiteSpace(req.Model)) appConfig.Gemini.Model = req.Model;
            }

            // Set or clear the monitor selection for this session
            appConfig.Agent.MonitorIndex = req.MonitorIndex;

            string sessionId = manager.StartSession(req.Goal);
            return Results.Ok(new { sessionId });
        });

        // Get the current status of a session
        group.MapGet("/{sessionId}/status", (string sessionId, AgentSessionManager manager) =>
        {
            AgentSession? session = manager.GetSession(sessionId);
            if (session is null)
                return Results.NotFound(new { error = "Session not found." });

            AgentStep? latest = session.Steps.Count > 0 ? session.Steps[^1] : null;

            return Results.Ok(new
            {
                goal = session.Goal,
                status = session.Status.ToString(),
                isPaused = session.IsPaused,
                stepCount = session.Steps.Count,
                latestThought = latest?.Thought,
                latestAction = latest is not null ? FormatAction(latest.Action) : null,
                latestResult = latest?.Result.Summary,
                finalResult = session.FinalResult,
                startedAt = session.StartedAt,
                completedAt = session.CompletedAt,
                totalDurationMs = session.CompletedAt.HasValue
                    ? (long)(session.CompletedAt.Value - session.StartedAt).TotalMilliseconds
                    : (long?)null,
                totalTokensUsed = session.TotalTokensUsed,
            });
        });

        // Get the full step history
        group.MapGet("/{sessionId}/steps", (string sessionId, AgentSessionManager manager) =>
        {
            AgentSession? session = manager.GetSession(sessionId);
            if (session is null)
                return Results.NotFound(new { error = "Session not found." });

            var steps = session.Steps.Select(s => new
            {
                s.StepNumber,
                s.Thought,
                action = FormatAction(s.Action),
                result = s.Result.Summary,
                s.Result.Success,
                s.Timestamp,
                s.DurationMs,
                s.Usage,
                s.TotalTokensUsed
            });

            return Results.Ok(steps);
        });

        // Server-Sent Events stream for real-time step updates
        group.MapGet("/{sessionId}/stream", async (string sessionId, AgentSessionManager manager, HttpContext httpContext) =>
        {
            AgentSession? session = manager.GetSession(sessionId);
            if (session is null)
            {
                httpContext.Response.StatusCode = 404;
                return;
            }

            httpContext.Response.ContentType = "text/event-stream";
            httpContext.Response.Headers.CacheControl = "no-cache";
            httpContext.Response.Headers.Connection = "keep-alive";
            CancellationToken ct = httpContext.RequestAborted;

            // Write any steps that already exist (client may connect after some have completed)
            foreach (AgentStep existing in session.Steps)
            {
                await WriteStepEventAsync(httpContext.Response, existing, ct).ConfigureAwait(false);
            }

            // Subscribe to future steps
            TaskCompletionSource tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            async Task OnSubStepUpdate(AgentSubStep subStep)
            {
                try
                {
                    await WriteSubStepEventAsync(httpContext.Response, subStep, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    tcs.TrySetResult();
                }
            }

            async Task OnStepStarting(AgentStepPreview preview)
            {
                try
                {
                    await WriteStepStartingEventAsync(httpContext.Response, preview, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    tcs.TrySetResult();
                }
            }

            async Task OnStep(AgentStep step)
            {
                try
                {
                    await WriteStepEventAsync(httpContext.Response, step, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    tcs.TrySetResult();
                }
            }

            async Task OnPauseChanged(AgentPauseChange change)
            {
                try
                {
                    await WritePauseEventAsync(httpContext.Response, change, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    tcs.TrySetResult();
                }
            }

            async Task OnResumeCountdown(int secondsRemaining)
            {
                try
                {
                    await WriteCountdownEventAsync(httpContext.Response, secondsRemaining, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    tcs.TrySetResult();
                }
            }

            async Task OnGuidanceQueued(string message, bool cancelNextAction)
            {
                try
                {
                    await WriteGuidanceQueuedEventAsync(httpContext.Response, message, cancelNextAction, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    tcs.TrySetResult();
                }
            }

            session.OnSubStepUpdate += OnSubStepUpdate;
            session.OnStepStarting += OnStepStarting;
            session.OnStepCompleted += OnStep;
            session.OnPauseChanged += OnPauseChanged;
            session.OnResumeCountdown += OnResumeCountdown;
            session.OnGuidanceQueued += OnGuidanceQueued;

            try
            {
                // If the session already terminated before we subscribed
                if (session.IsTerminated)
                {
                    await TryWriteTerminalEventAsync(httpContext.Response, session, ct).ConfigureAwait(false);
                    return;
                }

                // Catch up: if the session was already paused when the client connected, send the event now
                if (session.IsPaused)
                    await WritePauseEventAsync(httpContext.Response, AgentPauseChange.Paused, ct).ConfigureAwait(false);

                // Wait until the session ends for any reason or the client disconnects.
                using CancellationTokenRegistration registration = ct.Register(() => tcs.TrySetResult());
                _ = session.Terminated.ContinueWith(_ => tcs.TrySetResult(), TaskContinuationOptions.ExecuteSynchronously);
                await tcs.Task.ConfigureAwait(false);

                await TryWriteTerminalEventAsync(httpContext.Response, session, ct).ConfigureAwait(false);
            }
            finally
            {
                session.OnSubStepUpdate -= OnSubStepUpdate;
                session.OnStepStarting -= OnStepStarting;
                session.OnStepCompleted -= OnStep;
                session.OnPauseChanged -= OnPauseChanged;
                session.OnResumeCountdown -= OnResumeCountdown;
                session.OnGuidanceQueued -= OnGuidanceQueued;
            }
        });

        // Cancel a running session
        group.MapPost("/{sessionId}/stop", (string sessionId, AgentSessionManager manager) =>
        {
            bool found = manager.StopSession(sessionId);
            return found ? Results.NoContent() : Results.NotFound(new { error = "Session not found." });
        });

        // Pause a running session after its current step finishes
        group.MapPost("/{sessionId}/pause", (string sessionId, AgentSessionManager manager) =>
        {
            bool found = manager.PauseSession(sessionId);
            return found ? Results.NoContent() : Results.NotFound(new { error = "Session not found." });
        });

        // Resume a paused session
        group.MapPost("/{sessionId}/resume", (string sessionId, AgentSessionManager manager) =>
        {
            bool found = manager.ResumeSession(sessionId);
            return found ? Results.NoContent() : Results.NotFound(new { error = "Session not found." });
        });

        // Skip the resume countdown (proceed to execution immediately)
        group.MapPost("/{sessionId}/skip-countdown", (string sessionId, AgentSessionManager manager) =>
        {
            AgentSession? session = manager.GetSession(sessionId);
            if (session is null)
                return Results.NotFound(new { error = "Session not found." });
            session.SkipCountdown();
            return Results.NoContent();
        });

        // Enqueue a user guidance message for the running/paused session
        group.MapPost("/{sessionId}/guidance", (string sessionId, GuidanceRequest req, AgentSessionManager manager) =>
        {
            if (string.IsNullOrWhiteSpace(req.Message))
                return Results.BadRequest(new { error = "Message is required." });

            bool found = manager.SendGuidance(sessionId, req.Message, req.CancelNextAction ?? false);
            return found ? Results.NoContent() : Results.NotFound(new { error = "Session not found or not active." });
        });
    }

    private static async Task WriteSubStepEventAsync(HttpResponse response, AgentSubStep subStep, CancellationToken ct)
    {
        var payload = new
        {
            type = "subStep",
            subStep.StepNumber,
            label = subStep.Entry.Label,
            text = subStep.Entry.Text,
            imageBase64 = subStep.Entry.ImageBase64,
        };

        string json = JsonSerializer.Serialize(payload, JsonOptions);
        await response.WriteAsync($"data: {json}\n\n", ct).ConfigureAwait(false);
        await response.Body.FlushAsync(ct).ConfigureAwait(false);
    }

    private static async Task WriteStepStartingEventAsync(HttpResponse response, AgentStepPreview preview, CancellationToken ct)
    {
        var payload = new
        {
            type = "stepStarting",
            preview.StepNumber,
            preview.Thought,
            action = FormatAction(preview.Action),
            preview.QueueSize,
        };

        string json = JsonSerializer.Serialize(payload, JsonOptions);
        await response.WriteAsync($"data: {json}\n\n", ct).ConfigureAwait(false);
        await response.Body.FlushAsync(ct).ConfigureAwait(false);
    }

    private static async Task WriteStepEventAsync(HttpResponse response, AgentStep step, CancellationToken ct)
    {
        var payload = new
        {
            type = "step",
            step.StepNumber,
            step.Thought,
            action = FormatAction(step.Action),
            result = step.Result.Summary,
            step.Result.Success,
            step.Result.IsTerminal,
            step.Result.GoalAchieved,
            step.Timestamp,
            step.DurationMs,
            timings = step.Timings is null ? null : new
            {
                aiResponseMs      = step.Timings.AiResponseMs,
                parseMs           = step.Timings.ParseMs,
                executionMs       = step.Timings.ExecutionMs,
                coordResolutionMs = step.Timings.CoordResolutionMs,
            },
            usage = step.Usage,
            totalTokensUsed = step.TotalTokensUsed,
            debugLog = step.DebugLog?.Select(e => new { e.Label, e.Text, e.ImageBase64 }),
            isParseRejected = step.IsParseRejected,
            queuedSubSteps = step.QueuedSubSteps?.Select(s => new
            {
                s.QueueIndex,
                action = FormatAction(s.Action),
                result = s.Result.Summary,
                s.Result.Success,
                s.DurationMs,
                timings = s.Timings is null ? null : new
                {
                    aiResponseMs      = s.Timings.AiResponseMs,
                    parseMs           = s.Timings.ParseMs,
                    executionMs       = s.Timings.ExecutionMs,
                    coordResolutionMs = s.Timings.CoordResolutionMs,
                },
                usage = s.Usage,
                debugLog = s.DebugLog?.Select(e => new { e.Label, e.Text, e.ImageBase64 }),
            }),
        };

        string json = JsonSerializer.Serialize(payload, JsonOptions);
        await response.WriteAsync($"data: {json}\n\n", ct).ConfigureAwait(false);
        await response.Body.FlushAsync(ct).ConfigureAwait(false);
    }

    private static async Task WriteCountdownEventAsync(HttpResponse response, int secondsRemaining, CancellationToken ct)
    {
        var payload = new
        {
            type = "countdown",
            seconds = secondsRemaining,
        };

        string json = JsonSerializer.Serialize(payload, JsonOptions);
        await response.WriteAsync($"data: {json}\n\n", ct).ConfigureAwait(false);
        await response.Body.FlushAsync(ct).ConfigureAwait(false);
    }

    private static async Task WritePauseEventAsync(HttpResponse response, AgentPauseChange change, CancellationToken ct)
    {
        var payload = new
        {
            type = change == AgentPauseChange.Paused ? "paused" : "resumed",
        };

        string json = JsonSerializer.Serialize(payload, JsonOptions);
        await response.WriteAsync($"data: {json}\n\n", ct).ConfigureAwait(false);
        await response.Body.FlushAsync(ct).ConfigureAwait(false);
    }

    private static async Task WriteGuidanceQueuedEventAsync(HttpResponse response, string message, bool cancelNextAction, CancellationToken ct)
    {
        var payload = new
        {
            type = "guidanceQueued",
            message,
            cancelNextAction,
        };

        string json = JsonSerializer.Serialize(payload, JsonOptions);
        await response.WriteAsync($"data: {json}\n\n", ct).ConfigureAwait(false);
        await response.Body.FlushAsync(ct).ConfigureAwait(false);
    }

    private static async Task WriteTerminalEventAsync(HttpResponse response, AgentSession session, CancellationToken ct)
    {
        var payload = new
        {
            type = "done",
            status = session.Status.ToString(),
            finalResult = session.FinalResult,
            totalDurationMs = session.CompletedAt.HasValue
                ? (long)(session.CompletedAt.Value - session.StartedAt).TotalMilliseconds
                : (long?)null,
        };

        string json = JsonSerializer.Serialize(payload, JsonOptions);
        await response.WriteAsync($"data: {json}\n\n", ct).ConfigureAwait(false);
        await response.Body.FlushAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Best-effort wrapper around <see cref="WriteTerminalEventAsync"/>.
    /// If the client has already disconnected (token cancelled), the write is silently skipped.
    /// </summary>
    private static async Task TryWriteTerminalEventAsync(HttpResponse response, AgentSession session, CancellationToken ct)
    {
        if (ct.IsCancellationRequested)
            return;

        try
        {
            await WriteTerminalEventAsync(response, session, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Client disconnected between the check and the write — safe to ignore.
        }
    }

    private static string FormatAction(AgentAction action) => action.Kind switch
    {
        AgentActionKind.LeftClick => $"LEFT_CLICK \"{action.Target}\"",
        AgentActionKind.RightClick => $"RIGHT_CLICK \"{action.Target}\"",
        AgentActionKind.DoubleClick => $"DOUBLE_CLICK \"{action.Target}\"",
        AgentActionKind.MiddleClick => $"MIDDLE_CLICK \"{action.Target}\"",
        AgentActionKind.MoveMouse => $"MOVE_MOUSE \"{action.Target}\"",
        AgentActionKind.ClickDrag => $"CLICK_DRAG\nFrom: \"{action.Target}\"\nTo: \"{action.DragTarget}\"",
        AgentActionKind.ClickDragCoords => $"CLICK_DRAG_COORDS\nFrom: {action.Target}\nTo: {action.DragTarget}",
        AgentActionKind.TypeText => $"TYPE_TEXT \"{action.Text}\"",
        AgentActionKind.KeyCombo => $"KEY_COMBO {FormatKeyCombo(action)}",
        AgentActionKind.ScrollUp => $"SCROLL_UP {action.Amount}",
        AgentActionKind.ScrollDown => $"SCROLL_DOWN {action.Amount}",
        AgentActionKind.Wait => $"WAIT {action.Amount}",
        AgentActionKind.Done => "DONE",
        AgentActionKind.Fail => $"FAIL \"{action.Reason}\"",
        _ => action.Kind.ToString(),
    };

    private static string FormatKeyCombo(AgentAction action)
    {
        List<string> keys = new List<string>(5);

        if (action.Modifiers.HasFlag(ModifierKeys.Ctrl))
            keys.Add("ctrl");
        if (action.Modifiers.HasFlag(ModifierKeys.Shift))
            keys.Add("shift");
        if (action.Modifiers.HasFlag(ModifierKeys.Alt))
            keys.Add("alt");
        if (action.Modifiers.HasFlag(ModifierKeys.Win))
            keys.Add("win");
        if (!string.IsNullOrWhiteSpace(action.Key))
            keys.Add(action.Key);

        return string.Join('+', keys);
    }
}

file record AgentStartRequest(string? Goal, string? ApiKey, string? Model, int? MonitorIndex);
file record GuidanceRequest(string? Message, bool? CancelNextAction);