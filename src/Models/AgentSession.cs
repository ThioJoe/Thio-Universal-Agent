namespace Thio_Universal_Agent;

/// <summary>Tracks the lifecycle status of an agent session.</summary>
public enum AgentSessionStatus
{
    /// <summary>The agent loop is actively running.</summary>
    Running,
    /// <summary>The goal was achieved (agent issued DONE).</summary>
    Completed,
    /// <summary>The agent declared failure or exceeded the step limit.</summary>
    Failed,
    /// <summary>The session was cancelled by the user.</summary>
    Cancelled,
    /// <summary>An unexpected error terminated the session.</summary>
    Error,
}

/// <summary>Direction of a pause-state change emitted via <see cref="AgentSession.OnPauseChanged"/>.</summary>
public enum AgentPauseChange { Paused, Resumed }

/// <summary>
/// Holds all mutable state for a single agent execution session.
/// Created by <see cref="AgentSessionManager"/> and driven by <see cref="AgentLoop"/>.
/// </summary>
public sealed class AgentSession
{
    /// <summary>Unique identifier for this session.</summary>
    public string SessionId { get; } = Guid.NewGuid().ToString("N");

    /// <summary>The user-specified goal the agent is working toward.</summary>
    public required string Goal { get; init; }

    /// <summary>Current lifecycle status.</summary>
    public AgentSessionStatus Status { get; set; } = AgentSessionStatus.Running;

    /// <summary>Ordered history of every completed step.</summary>
    public List<AgentStep> Steps { get; } = [];

    /// <summary>Human-readable result message set when the session terminates.</summary>
    public string? FinalResult { get; set; }

    /// <summary>When the session was created.</summary>
    public DateTimeOffset StartedAt { get; } = DateTimeOffset.UtcNow;

    /// <summary>When the session fully terminated (set by <see cref="SignalTerminated"/>).</summary>
    public DateTimeOffset? CompletedAt { get; private set; }

    /// <summary>Cancellation source that the user or timeout logic can trigger.</summary>
    public CancellationTokenSource Cts { get; } = new();

    // ── Pause / Resume ─────────────────────────────────────────────────────────

    private readonly object _pauseLock = new();
    private TaskCompletionSource? _pauseTcs;

    /// <summary>True while the agent loop is suspended at an inter-step pause point.</summary>
    public bool IsPaused { get; private set; }

    /// <summary>
    /// Event raised (on the calling thread) whenever the pause state changes.
    /// Handlers receive the new pause state.
    /// </summary>
    public event Func<AgentPauseChange, Task>? OnPauseChanged;

    /// <summary>Suspends the agent loop after its current step finishes.</summary>
    public void Pause()
    {
        lock (_pauseLock)
        {
            if (IsPaused || Status != AgentSessionStatus.Running) return;
            IsPaused = true;
            _pauseTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        _ = OnPauseChanged?.Invoke(AgentPauseChange.Paused);
    }

    /// <summary>Resumes a paused agent loop.</summary>
    public void Resume()
    {
        TaskCompletionSource? tcs;
        lock (_pauseLock)
        {
            if (!IsPaused) return;
            IsPaused = false;
            tcs = _pauseTcs;
            _pauseTcs = null;
        }
        tcs?.TrySetResult();
        _ = OnPauseChanged?.Invoke(AgentPauseChange.Resumed);
    }

    /// <summary>
    /// Called by <see cref="AgentLoop"/> at safe inter-step boundaries.
    /// Blocks asynchronously while the session is paused; returns immediately otherwise.
    /// Throws <see cref="OperationCanceledException"/> if the session is cancelled while waiting.
    /// </summary>
    internal async Task WaitIfPausedAsync(CancellationToken ct)
    {
        Task? pauseTask;
        lock (_pauseLock)
        {
            pauseTask = _pauseTcs?.Task;
        }
        if (pauseTask is not null)
            await pauseTask.WaitAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Like <see cref="WaitIfPausedAsync"/>, but after the pause lifts fires a descending countdown
    /// (from <paramref name="countdownSeconds"/> down to 0) before returning, giving the user time to
    /// reposition windows before the pending action executes.
    /// Each tick raises <see cref="OnResumeCountdown"/> with the remaining seconds.
    /// </summary>
    internal async Task WaitIfPausedWithCountdownAsync(int countdownSeconds, CancellationToken ct)
    {
        while (true)
        {
            Task? pauseTask;
            lock (_pauseLock)
            {
                pauseTask = _pauseTcs?.Task;
            }
            if (pauseTask is null) return;

            await pauseTask.WaitAsync(ct).ConfigureAwait(false);

            // Count down, but bail out immediately if the user pauses again mid-countdown.
            bool repaused = false;
            for (int i = countdownSeconds; i >= 1; i--)
            {
                await RaiseResumeCountdownAsync(i).ConfigureAwait(false);
                await Task.Delay(1000, ct).ConfigureAwait(false);

                lock (_pauseLock)
                {
                    if (_pauseTcs is not null)
                    {
                        repaused = true;
                        break;
                    }
                }
            }

            // Always send 0 to hide the banner, then either loop back to wait or return.
            await RaiseResumeCountdownAsync(0).ConfigureAwait(false);
            if (!repaused) return;
        }
    }

    /// <summary>
    /// Fired once per second during the post-resume countdown with the number of seconds remaining.
    /// A value of 0 signals the countdown has finished and execution is about to resume.
    /// </summary>
    public event Func<int, Task>? OnResumeCountdown;

    /// <summary>Raises <see cref="OnResumeCountdown"/> with the given remaining-seconds value.</summary>
    internal async Task RaiseResumeCountdownAsync(int secondsRemaining)
    {
        Func<int, Task>? handler = OnResumeCountdown;
        if (handler is not null)
            await handler(secondsRemaining).ConfigureAwait(false);
    }

    /// <summary>
    /// Event raised right after the AI's response is parsed but before the action is executed.
    /// Allows the UI to show the intended action while coordinate resolution or other slow work proceeds.
    /// </summary>
    public event Func<AgentStepPreview, Task>? OnStepStarting;

    /// <summary>Raises <see cref="OnStepStarting"/> for the given preview.</summary>
    internal async Task RaiseStepStartingAsync(AgentStepPreview preview)
    {
        Func<AgentStepPreview, Task>? handler = OnStepStarting;
        if (handler is not null)
            await handler(preview).ConfigureAwait(false);
    }

    /// <summary>
    /// Event raised in real-time during action execution for each intermediate debug entry.
    /// Allows the UI to show coordinate resolution progress without waiting for the full step to complete.
    /// </summary>
    public event Func<AgentSubStep, Task>? OnSubStepUpdate;

    /// <summary>Raises <see cref="OnSubStepUpdate"/> for the given sub-step.</summary>
    internal async Task RaiseSubStepUpdateAsync(AgentSubStep subStep)
    {
        Func<AgentSubStep, Task>? handler = OnSubStepUpdate;
        if (handler is not null)
            await handler(subStep).ConfigureAwait(false);
    }

    /// <summary>
    /// Event raised after each step completes. The <see cref="AgentEndpoints"/> SSE stream subscribes to this.
    /// </summary>
    public event Func<AgentStep, Task>? OnStepCompleted;

    /// <summary>Raises <see cref="OnStepCompleted"/> for the given step.</summary>
    internal async Task RaiseStepCompletedAsync(AgentStep step)
    {
        Func<AgentStep, Task>? handler = OnStepCompleted;
        if (handler is not null)
            await handler(step).ConfigureAwait(false);
    }

    private readonly TaskCompletionSource _terminated = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>
    /// Completes when the session has fully terminated (status and FinalResult are guaranteed to be set).
    /// Resolved by <see cref="SignalTerminated"/> at the end of <see cref="AgentLoop.RunAsync"/>.
    /// </summary>
    public Task Terminated => _terminated.Task;

    /// <summary>Called by <see cref="AgentLoop"/> once the loop exits to unblock any SSE stream waiters.</summary>
    internal void SignalTerminated()
    {
        CompletedAt = DateTimeOffset.UtcNow;
        _terminated.TrySetResult();
    }
}
