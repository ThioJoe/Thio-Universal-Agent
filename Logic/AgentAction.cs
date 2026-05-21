namespace Thio_Universal_Agent.Logic;

/// <summary>Enumerates every tool the AI agent can invoke.</summary>
public enum AgentActionKind
{
    /// <summary>Left-clicks a UI element identified by a natural-language description.</summary>
    LeftClick,

    /// <summary>Right-clicks a UI element identified by a natural-language description.</summary>
    RightClick,

    /// <summary>Double-left-clicks a UI element identified by a natural-language description.</summary>
    DoubleClick,

    /// <summary>Middle-clicks a UI element identified by a natural-language description.</summary>
    MiddleClick,

    /// <summary>Moves the mouse cursor to a UI element without clicking.</summary>
    MoveMouse,

    /// <summary>Left click and hold, then moves the mouse to another location before releasing. </summary>
    ClickDrag,

    /// <summary>Left click and hold at exact coordinates, then move to another set of coordinates before releasing.</summary>
    ClickDragCoords,

    /// <summary>Types a string of text via the keyboard.</summary>
    TypeText,

    /// <summary>Presses a key combination (e.g. ctrl+s, alt+f4, enter).</summary>
    KeyCombo,

    /// <summary>Scrolls up by a given number of notches.</summary>
    ScrollUp,

    /// <summary>Scrolls down by a given number of notches.</summary>
    ScrollDown,

    /// <summary>Pauses execution for a given number of seconds.</summary>
    Wait,

    /// <summary>Signals the goal has been achieved.</summary>
    Done,

    /// <summary>Signals the goal cannot be achieved.</summary>
    Fail,
}

/// <summary>
/// A single parsed action from the AI's response.
/// Only the fields relevant to <see cref="Kind"/> are populated.
/// </summary>
/// <param name="Kind">The tool being invoked.</param>
/// <param name="Target">Natural-language description of the UI element to interact with (click/move/drag actions), or X,Y pair if using exact coordinates via <see cref="AgentActionKind.ClickDragCoords"/></param>
/// <param name="DragTarget">Natural-language description of the drag destination (for <see cref="AgentActionKind.ClickDrag"/>) or X,Y pair if using exact coordinates. <see cref="AgentActionKind.ClickDragCoords"/>.</param>
/// <param name="Text">The text payload (for <see cref="AgentActionKind.TypeText"/>).</param>
/// <param name="Key">The primary key name (for <see cref="AgentActionKind.KeyCombo"/>).</param>
/// <param name="ExactCoords">Whether the Target and DragTarget fields contain exact coordinates rather than natural-language descriptions.</param>
/// <param name="Ctrl">Whether Ctrl is held (for <see cref="AgentActionKind.KeyCombo"/>).</param>
/// <param name="Shift">Whether Shift is held (for <see cref="AgentActionKind.KeyCombo"/>).</param>
/// <param name="Alt">Whether Alt is held (for <see cref="AgentActionKind.KeyCombo"/>).</param>
/// <param name="Amount">Notch count for scroll or seconds for wait.</param>
/// <param name="Reason">Explanation when the agent declares <see cref="AgentActionKind.Fail"/>.</param>
public sealed record AgentAction(
    AgentActionKind Kind,
    string? Target = null,
    string? DragTarget = null,
    string? Text = null,
    string? Key = null,
    bool ExactCoords = false,
    bool Ctrl = false,
    bool Shift = false,
    bool Alt = false,
    int Amount = 1,
    string? Reason = null
    );

/// <summary>The AI's parsed response: a reasoning thought and exactly one action.</summary>
/// <param name="Thought">The AI's reasoning about what it sees and why it chose this action.</param>
/// <param name="Action">The single tool invocation to execute.</param>
public sealed record AgentParsedResponse(string Thought, AgentAction Action);

/// <summary>A single debug log entry captured when <see cref="Globals.ENABLE_TESTING"/> is true.</summary>
/// <param name="Label">Short heading for this entry (e.g. "Prompt Sent to AI").</param>
/// <param name="Text">Optional text content.</param>
/// <param name="ImageBase64">Optional base64-encoded image (PNG/JPEG).</param>
public sealed record AgentDebugEntry(string Label, string? Text = null, string? ImageBase64 = null);

/// <summary>Result of executing a single agent action against the OS.</summary>
/// <param name="Success">Whether the action executed without error.</param>
/// <param name="Summary">Human-readable summary (e.g. "Left-clicked at (523, 847)").</param>
/// <param name="IsTerminal">True for <see cref="AgentActionKind.Done"/> and <see cref="AgentActionKind.Fail"/>.</param>
/// <param name="GoalAchieved">True only when the agent declares <see cref="AgentActionKind.Done"/>.</param>
/// <param name="DebugEntries">Detailed debug entries from execution (only when testing is enabled).</param>
public sealed record ActionExecutionResult(
    bool Success,
    string Summary,
    bool IsTerminal,
    bool GoalAchieved,
    IReadOnlyList<AgentDebugEntry>? DebugEntries = null);

/// <summary>
/// Lightweight preview emitted right after the AI's response is parsed but before execution begins.
/// Allows the UI to show the intended action while coordinate resolution or other slow work proceeds.
/// </summary>
/// <param name="StepNumber">1-based index of the upcoming step.</param>
/// <param name="Thought">The AI's reasoning for this step.</param>
/// <param name="Action">The tool invocation that is about to be executed.</param>
public sealed record AgentStepPreview(int StepNumber, string Thought, AgentAction Action);

/// <summary>
/// A real-time sub-step update emitted during action execution.
/// Streamed to the UI via SSE before the full step completes, providing visibility
/// into slow operations like coordinate resolution.
/// </summary>
/// <param name="StepNumber">The 1-based step number this sub-step belongs to.</param>
/// <param name="Entry">The debug entry being emitted.</param>
public sealed record AgentSubStep(int StepNumber, AgentDebugEntry Entry);

/// <summary>A single completed step in the agent's execution history.</summary>
/// <param name="StepNumber">1-based index of this step.</param>
/// <param name="Thought">The AI's reasoning for this step.</param>
/// <param name="Action">The tool invocation that was executed.</param>
/// <param name="Result">Outcome of the execution.</param>
/// <param name="Timestamp">When this step completed.</param>
/// <param name="DebugLog">Verbose debug entries for this step (only when testing is enabled).</param>
public sealed record AgentStep(
    int StepNumber,
    string Thought,
    AgentAction Action,
    ActionExecutionResult Result,
    DateTimeOffset Timestamp,
    IReadOnlyList<AgentDebugEntry>? DebugLog = null);
