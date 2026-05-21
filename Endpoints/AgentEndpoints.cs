using System.Text.Json;
using Thio_Universal_Agent.Logic;

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
        var group = app.MapGroup("/api/agent");

        // Check if verbose debug output is enabled
        group.MapGet("/debug-enabled", () => Results.Ok(new { enabled = Globals.ENABLE_TESTING }));

        // Start a new agent session
        group.MapPost("/start", (AgentStartRequest req, AgentSessionManager manager, IConfiguration configuration) =>
        {
            if (string.IsNullOrWhiteSpace(req.Goal))
                return Results.BadRequest(new { error = "Goal is required." });

            if (string.IsNullOrWhiteSpace(req.ApiKey))
                return Results.BadRequest(new { error = "API key is required." });

            // Store the key so the next GeminiProvider instance picks it up
            configuration["Gemini:ApiKey"] = req.ApiKey;

            if (!string.IsNullOrWhiteSpace(req.Model))
                configuration["Gemini:Model"] = req.Model;

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
                status = session.Status.ToString(),
                stepCount = session.Steps.Count,
                latestThought = latest?.Thought,
                latestAction = latest is not null ? FormatAction(latest.Action) : null,
                latestResult = latest?.Result.Summary,
                finalResult = session.FinalResult,
                startedAt = session.StartedAt,
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
            var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

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

                    if (step.Result.IsTerminal)
                        tcs.TrySetResult();
                }
                catch (OperationCanceledException)
                {
                    tcs.TrySetResult();
                }
            }

            session.OnSubStepUpdate += OnSubStepUpdate;
            session.OnStepStarting += OnStepStarting;
            session.OnStepCompleted += OnStep;

            try
            {
                // If the session already terminated before we subscribed
                if (session.Status is not AgentSessionStatus.Running)
                {
                    await TryWriteTerminalEventAsync(httpContext.Response, session, ct).ConfigureAwait(false);
                    return;
                }

                // Wait until a terminal step arrives, the session ends for any reason, or the client disconnects
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
            }
        });

        // Cancel a running session
        group.MapPost("/{sessionId}/stop", (string sessionId, AgentSessionManager manager) =>
        {
            bool found = manager.StopSession(sessionId);
            return found ? Results.NoContent() : Results.NotFound(new { error = "Session not found." });
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
            debugLog = step.DebugLog?.Select(e => new { e.Label, e.Text, e.ImageBase64 }),
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
        AgentActionKind.TypeText => $"TYPE_TEXT \"{action.Text}\"",
        AgentActionKind.KeyCombo => $"KEY_COMBO {(action.Ctrl ? "ctrl+" : "")}{(action.Shift ? "shift+" : "")}{(action.Alt ? "alt+" : "")}{action.Key}",
        AgentActionKind.ScrollUp => $"SCROLL_UP {action.Amount}",
        AgentActionKind.ScrollDown => $"SCROLL_DOWN {action.Amount}",
        AgentActionKind.Wait => $"WAIT {action.Amount}",
        AgentActionKind.Done => "DONE",
        AgentActionKind.Fail => $"FAIL \"{action.Reason}\"",
        _ => action.Kind.ToString(),
    };
}

file record AgentStartRequest(string? Goal, string? ApiKey, string? Model);
