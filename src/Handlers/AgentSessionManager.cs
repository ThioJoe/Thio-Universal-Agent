using System.Collections.Concurrent;

namespace Thio_Universal_Agent;

/// <summary>
/// Singleton service that manages active <see cref="AgentSession"/> instances.
/// Creates sessions, launches the <see cref="AgentLoop"/> on a background task,
/// and exposes lookup/cancellation for the HTTP layer.
/// </summary>
public sealed class AgentSessionManager(
    IServiceProvider serviceProvider,
    ILogger<AgentSessionManager> logger)
{
    private readonly ConcurrentDictionary<string, AgentSession> _sessions = new();

    /// <summary> Creates a new agent session for the given goal and starts the agent loop on a background task.</summary>
    /// <returns>The session ID.</returns>
    public string StartSession(string goal)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(goal);

        var session = new AgentSession { Goal = goal };
        _sessions[session.SessionId] = session;

        logger.LogInformation("Starting agent session {SessionId} with goal: \"{Goal}\".", session.SessionId, goal);

        // Resolve a scoped AgentLoop from DI and run on a background task.
        // The scope ensures transient/scoped dependencies are properly disposed.
        _ = Task.Run(async () =>
        {
            using IServiceScope scope = serviceProvider.CreateScope();
            AgentLoop loop = scope.ServiceProvider.GetRequiredService<AgentLoop>();

            await loop.RunAsync(session).ConfigureAwait(false);

            logger.LogInformation(
                "Agent session {SessionId} finished with status {Status}.",
                session.SessionId, session.Status);
        });

        return session.SessionId;
    }

    /// <summary>Retrieves a session by its ID, or null if not found.</summary>
    public AgentSession? GetSession(string sessionId)
    {
        _sessions.TryGetValue(sessionId, out AgentSession? session);
        return session;
    }

    /// <summary>Returns the first session that is currently running or paused, or null if none is active.</summary>
    public AgentSession? GetActiveSession() =>
        _sessions.Values.FirstOrDefault(s => s.Status == AgentSessionStatus.Running || s.IsPaused);

    /// <summary>Cancels a running session. Returns false if the session was not found.</summary>
    public bool StopSession(string sessionId)
    {
        if (!_sessions.TryGetValue(sessionId, out AgentSession? session))
            return false;

        logger.LogInformation("Cancelling agent session {SessionId}.", sessionId);
        session.Cts.Cancel();
        return true;
    }

    /// <summary>Pauses a running session after its current step finishes. Returns false if not found.</summary>
    public bool PauseSession(string sessionId)
    {
        if (!_sessions.TryGetValue(sessionId, out AgentSession? session))
            return false;

        logger.LogInformation("Pausing agent session {SessionId}.", sessionId);
        session.Pause();
        return true;
    }

    /// <summary>Resumes a paused session. Returns false if not found.</summary>
    public bool ResumeSession(string sessionId)
    {
        if (!_sessions.TryGetValue(sessionId, out AgentSession? session))
            return false;

        logger.LogInformation("Resuming agent session {SessionId}.", sessionId);
        session.Resume();
        return true;
    }

    /// <summary>
    /// Enqueues a user guidance message to be injected into the next AI feedback call.
    /// Returns false if the session is not found or is no longer active.
    /// </summary>
    public bool SendGuidance(string sessionId, string message, bool cancelNextAction = false)
    {
        if (!_sessions.TryGetValue(sessionId, out AgentSession? session))
            return false;

        if (session.Status != AgentSessionStatus.Running && !session.IsPaused)
            return false;

        logger.LogInformation("User guidance queued for session {SessionId} (cancelNext={CancelNext}).", sessionId, cancelNextAction);
        session.EnqueueGuidance(message, cancelNextAction);
        return true;
    }
}
