using System.Globalization;
using Microsoft.Extensions.Logging;

namespace Thio_Universal_Agent.Logic;

/// <summary>
/// Executes a single <see cref="AgentAction"/> by dispatching to the appropriate
/// <see cref="IInputProvider"/> or <see cref="CoordinatePrompter"/> methods.
/// When <see cref="Globals.ENABLE_TESTING"/> is true, captures detailed debug entries
/// including coordinate resolution steps.
/// </summary>
public sealed class AgentActionExecutor(
    IInputProvider inputProvider,
    IScreenProvider screenProvider,
    CoordinatePrompter coordinatePrompter,
    ILogger<AgentActionExecutor> logger)
{
    private const int DoubleClickDelayMs = 60;

    /// <summary>
    /// Executes the given action against the OS.
    /// For click/move actions, <paramref name="currentScreenshot"/> is used by the
    /// <see cref="CoordinatePrompter"/> to resolve the target description to pixel coordinates.
    /// </summary>
    public async Task<ActionExecutionResult> ExecuteAsync(
        AgentAction action,
        byte[] currentScreenshot,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(action);
        ArgumentNullException.ThrowIfNull(currentScreenshot);

        List<AgentDebugEntry>? debugLog = Globals.ENABLE_TESTING ? [] : null;

        try
        {
            ActionExecutionResult result = action.Kind switch
            {
                AgentActionKind.LeftClick or AgentActionKind.RightClick or AgentActionKind.DoubleClick
                    or AgentActionKind.MiddleClick or AgentActionKind.MoveMouse
                    => await ExecuteClickAsync(action, currentScreenshot, cancellationToken, debugLog).ConfigureAwait(false),
                AgentActionKind.TypeText => await ExecuteTypeTextAsync(action, debugLog).ConfigureAwait(false),
                AgentActionKind.KeyCombo => await ExecuteKeyComboAsync(action, debugLog).ConfigureAwait(false),
                AgentActionKind.ScrollUp or AgentActionKind.ScrollDown
                    => await ExecuteScrollAsync(action, debugLog).ConfigureAwait(false),
                AgentActionKind.Wait => await ExecuteWaitAsync(action, cancellationToken, debugLog).ConfigureAwait(false),
                AgentActionKind.Done => new ActionExecutionResult(true, "Agent declared goal achieved.", IsTerminal: true, GoalAchieved: true),
                AgentActionKind.Fail => new ActionExecutionResult(true, $"Agent declared failure: {action.Reason}", IsTerminal: true, GoalAchieved: false),
                _ => new ActionExecutionResult(false, $"Unknown action kind: {action.Kind}", IsTerminal: false, GoalAchieved: false),
            };

            return debugLog is { Count: > 0 } ? result with { DebugEntries = debugLog } : result;
        }
        catch (OperationCanceledException)
        {
            throw; // Let cancellation propagate
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to execute {ActionKind}.", action.Kind);
            debugLog?.Add(new AgentDebugEntry("Execution Exception", Text: ex.ToString()));
            var errorResult = new ActionExecutionResult(false, $"Execution error: {ex.Message}", IsTerminal: false, GoalAchieved: false);
            return debugLog is { Count: > 0 } ? errorResult with { DebugEntries = debugLog } : errorResult;
        }
    }

    /// <summary>Resolves the target to coordinates, then dispatches to the correct click/move method.</summary>
    private async Task<ActionExecutionResult> ExecuteClickAsync(
        AgentAction action, byte[] screenshot, CancellationToken cancellationToken, List<AgentDebugEntry>? debugLog)
    {
        string target = action.Target ?? throw new InvalidOperationException($"{action.Kind} requires a Target.");

        logger.LogInformation("Resolving coordinates for: \"{Target}\"", target);
        debugLog?.Add(new AgentDebugEntry("Coordinate Resolution", Text: $"Resolving target: \"{target}\""));

        // When testing, capture every intermediate CoordinatePrompter step
        Func<CoordinatePrompter.CoordinateStep, Task>? onCoordStep = null;
        if (debugLog is not null)
        {
            onCoordStep = coordStep =>
            {
                debugLog.Add(new AgentDebugEntry(
                    $"Coord Step {coordStep.StepNumber}: Grid Image",
                    ImageBase64: Convert.ToBase64String(coordStep.GridImage)));

                debugLog.Add(new AgentDebugEntry(
                    $"Coord Step {coordStep.StepNumber}: AI Response",
                    Text: coordStep.AiResponseText));

                if (coordStep.ParsedX.HasValue && coordStep.ParsedY.HasValue)
                {
                    debugLog.Add(new AgentDebugEntry(
                        $"Coord Step {coordStep.StepNumber}: Parsed Coordinates",
                        Text: $"({coordStep.ParsedX:F1}, {coordStep.ParsedY:F1})"));
                }

                debugLog.Add(new AgentDebugEntry(
                    $"Coord Step {coordStep.StepNumber}: Annotated Image",
                    ImageBase64: Convert.ToBase64String(coordStep.AnnotatedImage)));

                return Task.CompletedTask;
            };
        }

        (double x, double y) = await coordinatePrompter
            .GetCoordinatesForItemAsync(screenshot, target, onCoordStep, cancellationToken)
            .ConfigureAwait(false);

        // Shift image-pixel coordinates to absolute screen coordinates.
        // On multi-monitor setups the virtual screen may start at a negative offset
        // (e.g. a secondary monitor positioned to the left of the primary).
        var (originX, originY) = screenProvider.GetVirtualScreenOrigin();
        int px = (int)Math.Round(x) + originX;
        int py = (int)Math.Round(y) + originY;

        logger.LogInformation("{ActionKind} at ({X}, {Y}) for \"{Target}\".", action.Kind, px, py, target);
        debugLog?.Add(new AgentDebugEntry("Final Resolved Coordinates", Text: $"({px}, {py})"));

        string methodCalled;
        switch (action.Kind)
        {
            case AgentActionKind.LeftClick:
                await inputProvider.LeftClick_MonitorCoords(px, py).ConfigureAwait(false);
                methodCalled = $"LeftClick_MonitorCoords({px}, {py})";
                break;

            case AgentActionKind.RightClick:
                await inputProvider.RightClick_MonitorCoords(px, py).ConfigureAwait(false);
                methodCalled = $"RightClick_MonitorCoords({px}, {py})";
                break;

            case AgentActionKind.DoubleClick:
                await inputProvider.DoubleClick_MonitorCoords(px, py).ConfigureAwait(false);
                methodCalled = $"DoubleClick_MonitorCoords({px}, {py})";
                break;

            case AgentActionKind.MiddleClick:
                await inputProvider.MiddleMouse_MonitorCoords(px, py).ConfigureAwait(false);
                methodCalled = $"MiddleMouse_MonitorCoords({px}, {py})";
                break;

            case AgentActionKind.MoveMouse:
                await inputProvider.MoveMouse_MonitorCoords(px, py).ConfigureAwait(false);
                methodCalled = $"MoveMouse_MonitorCoords({px}, {py})";
                break;

            default:
                methodCalled = "N/A";
                break;
        }

        debugLog?.Add(new AgentDebugEntry("OS Input Call", Text: methodCalled));

        string coordStr = $"({px.ToString(CultureInfo.InvariantCulture)}, {py.ToString(CultureInfo.InvariantCulture)})";
        return new ActionExecutionResult(true, $"{action.Kind} at {coordStr} targeting \"{target}\".", IsTerminal: false, GoalAchieved: false);
    }

    private async Task<ActionExecutionResult> ExecuteTypeTextAsync(AgentAction action, List<AgentDebugEntry>? debugLog)
    {
        string text = action.Text ?? throw new InvalidOperationException("TypeText requires Text.");

        logger.LogInformation("Typing text: \"{Text}\"", text);
        debugLog?.Add(new AgentDebugEntry("OS Input Call", Text: $"TypeTextAsync(\"{text}\")"));

        await inputProvider.TypeTextAsync(text).ConfigureAwait(false);

        return new ActionExecutionResult(true, $"Typed \"{text}\".", IsTerminal: false, GoalAchieved: false);
    }

    private async Task<ActionExecutionResult> ExecuteKeyComboAsync(AgentAction action, List<AgentDebugEntry>? debugLog)
    {
        string key = action.Key ?? throw new InvalidOperationException("KeyCombo requires Key.");

        logger.LogInformation("Key combo: {Key} (ctrl={Ctrl}, shift={Shift}, alt={Alt}).", key, action.Ctrl, action.Shift, action.Alt);
        debugLog?.Add(new AgentDebugEntry("OS Input Call",
            Text: $"SendModKeyComboAsync(\"{key}\", ctrl={action.Ctrl}, shift={action.Shift}, alt={action.Alt})"));

        await inputProvider.SendModKeyComboAsync(key, action.Ctrl ? true : null, action.Shift ? true : null, action.Alt ? true : null).ConfigureAwait(false);

        string combo = FormatKeyCombo(key, action.Ctrl, action.Shift, action.Alt);
        return new ActionExecutionResult(true, $"Pressed {combo}.", IsTerminal: false, GoalAchieved: false);
    }

    private async Task<ActionExecutionResult> ExecuteScrollAsync(AgentAction action, List<AgentDebugEntry>? debugLog)
    {
        logger.LogInformation("{Kind} by {Amount}.", action.Kind, action.Amount);
        debugLog?.Add(new AgentDebugEntry("OS Input Call",
            Text: $"{(action.Kind == AgentActionKind.ScrollUp ? "ScrollUp" : "ScrollDown")}({action.Amount})"));

        if (action.Kind == AgentActionKind.ScrollUp)
            await inputProvider.ScrollUp(action.Amount).ConfigureAwait(false);
        else
            await inputProvider.ScrollDown(action.Amount).ConfigureAwait(false);

        return new ActionExecutionResult(true, $"{action.Kind} by {action.Amount}.", IsTerminal: false, GoalAchieved: false);
    }

    private static async Task<ActionExecutionResult> ExecuteWaitAsync(AgentAction action, CancellationToken cancellationToken, List<AgentDebugEntry>? debugLog)
    {
        int ms = action.Amount * 1000;
        debugLog?.Add(new AgentDebugEntry("OS Input Call", Text: $"Task.Delay({ms}ms)"));

        await Task.Delay(ms, cancellationToken).ConfigureAwait(false);
        return new ActionExecutionResult(true, $"Waited {action.Amount} second(s).", IsTerminal: false, GoalAchieved: false);
    }

    private static string FormatKeyCombo(string key, bool ctrl, bool shift, bool alt)
    {
        var parts = new List<string>(4);
        if (ctrl) parts.Add("Ctrl");
        if (shift) parts.Add("Shift");
        if (alt) parts.Add("Alt");
        parts.Add(key.ToUpperInvariant());
        return string.Join('+', parts);
    }
}
