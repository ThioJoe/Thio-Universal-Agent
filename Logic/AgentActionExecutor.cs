using Microsoft.Extensions.Logging;
using Microsoft.Maui.Graphics;
using System.Globalization;

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
    /// <param name="onProgress">Optional callback invoked for each debug entry as it is produced,
    /// enabling real-time streaming to the UI before the full step completes.</param>
    public async Task<ActionExecutionResult> ExecuteAsync(
        AgentAction action,
        byte[] currentScreenshot,
        CancellationToken cancellationToken = default,
        Func<AgentDebugEntry, Task>? onProgress = null)
    {
        ArgumentNullException.ThrowIfNull(action);
        ArgumentNullException.ThrowIfNull(currentScreenshot);

        List<AgentDebugEntry>? debugLog = Globals.ENABLE_TESTING ? [] : null;

        try
        {
            ActionExecutionResult result = action.Kind switch
            {
                AgentActionKind.LeftClick or AgentActionKind.RightClick or AgentActionKind.DoubleClick or AgentActionKind.MiddleClick or AgentActionKind.MoveMouse
                    => await ExecuteClickAsync(action, currentScreenshot, cancellationToken, debugLog, onProgress).ConfigureAwait(false),

                AgentActionKind.ClickDrag or AgentActionKind.ClickDragCoords
                    => await ExecuteClickDragAsync(action, currentScreenshot, cancellationToken, debugLog, onProgress).ConfigureAwait(false),

                AgentActionKind.TypeText 
                    => await ExecuteTypeTextAsync(action, debugLog, onProgress).ConfigureAwait(false),

                AgentActionKind.KeyCombo 
                    => await ExecuteKeyComboAsync(action, debugLog, onProgress).ConfigureAwait(false),

                AgentActionKind.ScrollUp or AgentActionKind.ScrollDown
                    => await ExecuteScrollAsync(action, debugLog, onProgress).ConfigureAwait(false),

                AgentActionKind.Wait 
                    => await ExecuteWaitAsync(action, cancellationToken, debugLog, onProgress).ConfigureAwait(false),

                AgentActionKind.Done 
                    => new ActionExecutionResult(true, "Agent declared goal achieved.", IsTerminal: true, GoalAchieved: true),

                AgentActionKind.Fail 
                    => new ActionExecutionResult(true, $"Agent declared failure: {action.Reason}", IsTerminal: true, GoalAchieved: false),

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
            await EmitDebugAsync(debugLog, onProgress, new AgentDebugEntry("Execution Exception", Text: ex.ToString())).ConfigureAwait(false);
            var errorResult = new ActionExecutionResult(false, $"Execution error: {ex.Message}", IsTerminal: false, GoalAchieved: false);
            return debugLog is { Count: > 0 } ? errorResult with { DebugEntries = debugLog } : errorResult;
        }
    }

    /// <summary>Resolves the target to coordinates, then dispatches to the correct click/move method.</summary>
    private async Task<ActionExecutionResult> ExecuteClickAsync(
        AgentAction action, 
        byte[] screenshot, 
        CancellationToken cancellationToken,
        List<AgentDebugEntry>? debugLog, 
        Func<AgentDebugEntry, Task>? onProgress = null
        )
    {
        string target = action.Target ?? throw new InvalidOperationException($"{action.Kind} requires a Target.");

        // If the alt mode is CurrentCursorPosition, skip coordinate resolution and click in place.
        if (action.AltMode == AgentActionAltMode.CurrentCursorPosition)
        {
            var (curX, curY) = inputProvider.GetCursorPosition();
            logger.LogInformation("{ActionKind} at current cursor position ({X}, {Y}).", action.Kind, curX, curY);
            await EmitDebugAsync(debugLog, onProgress, new AgentDebugEntry("Current Cursor Position", Text: $"({curX}, {curY})")).ConfigureAwait(false);

            string cursorMethodCalled;
            switch (action.Kind)
            {
                case AgentActionKind.LeftClick:
                    await inputProvider.LeftClick_MonitorCoords(curX, curY).ConfigureAwait(false);
                    cursorMethodCalled = $"LeftClick_MonitorCoords({curX}, {curY})";
                    break;

                case AgentActionKind.RightClick:
                    await inputProvider.RightClick_MonitorCoords(curX, curY).ConfigureAwait(false);
                    cursorMethodCalled = $"RightClick_MonitorCoords({curX}, {curY})";
                    break;

                case AgentActionKind.DoubleClick:
                    await inputProvider.DoubleClick_MonitorCoords(curX, curY).ConfigureAwait(false);
                    cursorMethodCalled = $"DoubleClick_MonitorCoords({curX}, {curY})";
                    break;

                case AgentActionKind.MiddleClick:
                    await inputProvider.MiddleMouse_MonitorCoords(curX, curY).ConfigureAwait(false);
                    cursorMethodCalled = $"MiddleMouse_MonitorCoords({curX}, {curY})";
                    break;

                default:
                    cursorMethodCalled = "N/A";
                    break;
            }

            await EmitDebugAsync(debugLog, onProgress, new AgentDebugEntry("OS Input Call", Text: cursorMethodCalled)).ConfigureAwait(false);

            string cursorCoordStr = $"({curX.ToString(CultureInfo.InvariantCulture)}, {curY.ToString(CultureInfo.InvariantCulture)})";
            return new ActionExecutionResult(true, $"{action.Kind} at current cursor position {cursorCoordStr}.", IsTerminal: false, GoalAchieved: false);
        }
        // If we need to resolve the coordinates from natural language description
        else
        {
            logger.LogInformation("Resolving coordinates for: \"{Target}\"", target);
            await EmitDebugAsync(debugLog, onProgress, new AgentDebugEntry("Coordinate Resolution", Text: $"Resolving target: \"{target}\"")).ConfigureAwait(false);

            // When testing, capture every intermediate CoordinatePrompter step
            Func<CoordinatePrompter.CoordinateStep, Task>? onCoordStep = null;
            if (debugLog is not null || onProgress is not null)
            {
                onCoordStep = async coordStep =>
                {
                    await EmitDebugAsync(debugLog, onProgress, new AgentDebugEntry(
                        $"Coord Step {coordStep.StepNumber}: Grid Image",
                        ImageBase64: Convert.ToBase64String(coordStep.GridImage))).ConfigureAwait(false);

                    await EmitDebugAsync(debugLog, onProgress, new AgentDebugEntry(
                        $"Coord Step {coordStep.StepNumber}: AI Response",
                        Text: coordStep.AiResponseText)).ConfigureAwait(false);

                    if (coordStep.ParsedX.HasValue && coordStep.ParsedY.HasValue)
                    {
                        await EmitDebugAsync(debugLog, onProgress, new AgentDebugEntry(
                            $"Coord Step {coordStep.StepNumber}: Parsed Coordinates",
                            Text: $"({coordStep.ParsedX:F1}, {coordStep.ParsedY:F1})")).ConfigureAwait(false);
                    }

                    await EmitDebugAsync(debugLog, onProgress, new AgentDebugEntry(
                        $"Coord Step {coordStep.StepNumber}: Annotated Image",
                        ImageBase64: Convert.ToBase64String(coordStep.AnnotatedImage))).ConfigureAwait(false);
                };
            }

            (double x, double y, double? normX, double? normY) = await coordinatePrompter
                .GetCoordinatesForItemAsync(screenshot, target, onStepCompleted: onCoordStep, cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            // Shift image-pixel coordinates to absolute screen coordinates.
            // On multi-monitor setups the virtual screen may start at a negative offset
            // (e.g. a secondary monitor positioned to the left of the primary).
            var (originX, originY) = screenProvider.GetVirtualScreenOrigin();
            int px = (int)Math.Round(x) + originX;
            int py = (int)Math.Round(y) + originY;

            logger.LogInformation("{ActionKind} at ({X}, {Y}) for \"{Target}\".", action.Kind, px, py, target);
            await EmitDebugAsync(debugLog, onProgress, new AgentDebugEntry("Final Resolved Coordinates", Text: $"({px}, {py})")).ConfigureAwait(false);

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

            await EmitDebugAsync(debugLog, onProgress, new AgentDebugEntry("OS Input Call", Text: methodCalled)).ConfigureAwait(false);

            string trueCoordStr = $"({px.ToString(CultureInfo.InvariantCulture)}, {py.ToString(CultureInfo.InvariantCulture)})";
            string normSuffix = normX.HasValue && normY.HasValue
                ? $" (1000x1000 Normalized @ X={normX:F0}, Y={normY:F0})"
                : string.Empty;
            return new ActionExecutionResult(true, $"{action.Kind} at {trueCoordStr}{normSuffix} targeting \"{target}\".", IsTerminal: false, GoalAchieved: false);
        }
    }

    /// <summary>Resolves source and destination targets to coordinates, then performs a click-drag.</summary>
    private async Task<ActionExecutionResult> ExecuteClickDragAsync(
        AgentAction action, 
        byte[] screenshot, 
        CancellationToken cancellationToken,
        List<AgentDebugEntry>? debugLog,
        Func<AgentDebugEntry, Task>? onProgress = null
        )
    {
        string source = action.Target ?? throw new InvalidOperationException("ClickDrag requires a Target (source).");
        string destination = action.DragTarget ?? throw new InvalidOperationException("ClickDrag requires a DragTarget (destination).");

        // Resolve source coordinates
        logger.LogInformation("Resolving drag source coordinates for: \"{Target}\"", source);
        await EmitDebugAsync(debugLog, onProgress, new AgentDebugEntry("Drag Source Resolution", Text: $"Resolving source: \"{source}\"")).ConfigureAwait(false);

        int startPx, startPy, endPx, endPy;

        if (action.AltMode == AgentActionAltMode.ExactCoords
            || action.AltMode == AgentActionAltMode.CurrentCursorPositionStart
            || action.AltMode == AgentActionAltMode.CurrentCursorPositionEnd
            || action.AltMode == AgentActionAltMode.CurrentCursorPositionBoth)
        {
            // Resolve start point
            if (action.AltMode == AgentActionAltMode.CurrentCursorPositionStart
                || action.AltMode == AgentActionAltMode.CurrentCursorPositionBoth)
            {
                var (cx, cy) = inputProvider.GetCursorPosition();
                startPx = cx;
                startPy = cy;
                await EmitDebugAsync(debugLog, onProgress, new AgentDebugEntry("Drag Start (Current Cursor)", Text: $"({startPx}, {startPy})")).ConfigureAwait(false);
            }
            else
            {
                (startPx, startPy) = ParseAndNormalizeCoords(source);
            }

            // Resolve end point
            if (action.AltMode == AgentActionAltMode.CurrentCursorPositionEnd
                || action.AltMode == AgentActionAltMode.CurrentCursorPositionBoth)
            {
                var (cx, cy) = inputProvider.GetCursorPosition();
                endPx = cx;
                endPy = cy;
                await EmitDebugAsync(debugLog, onProgress, new AgentDebugEntry("Drag End (Current Cursor)", Text: $"({endPx}, {endPy})")).ConfigureAwait(false);
            }
            else
            {
                (endPx, endPy) = ParseAndNormalizeCoords(destination);
            }

            // When ExactCoords is enabled, the Target and DragTarget fields contain literal "X,Y" coordinate pairs rather than natural language descriptions.
            // This allows the AI to bypass the CoordinatePrompter when it needs to perform precise adjustments based on pixel values from the screenshot.
            (int px, int py) ParseAndNormalizeCoords(string coordStr)
            {
                string[] parts = coordStr.Split(',');
                if (parts.Length != 2
                    || !int.TryParse(parts[0].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int px)
                    || !int.TryParse(parts[1].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int py))
                {
                    throw new FormatException($"Invalid coordinate format: \"{coordStr}\". Expected format: \"X,Y\" with integer values.");
                }
                else
                {
                    (int imgWidth, int imgHeight) = CoordinatePrompter.GetImageResolution(screenshot);
                    (double TrueXCoords, double TrueYCoords) = CoordinatePrompter.UnNormalizeCoordinates(px, py, 1000, 1000, imgWidth, imgHeight);
                    return ((int)TrueXCoords, (int)TrueYCoords);
                }

            } // ---- End local function -----
        }
        else
        {
            (startPx, startPy) = await ResolveTargetCoordinatesAsync(screenshot, source, cancellationToken, debugLog, onProgress)
                .ConfigureAwait(false);

            logger.LogInformation("Drag source at ({X}, {Y}) for \"{Target}\".", startPx, startPy, source);
            await EmitDebugAsync(debugLog, onProgress, new AgentDebugEntry("Drag Source Coordinates", Text: $"({startPx}, {startPy})")).ConfigureAwait(false);

            // Resolve destination coordinates
            logger.LogInformation("Resolving drag destination coordinates for: \"{Target}\"", destination);
            await EmitDebugAsync(debugLog, onProgress, new AgentDebugEntry("Drag Destination Resolution", Text: $"Resolving destination: \"{destination}\"")).ConfigureAwait(false);

            (endPx, endPy) = await ResolveTargetCoordinatesAsync(screenshot, destination, cancellationToken, debugLog, onProgress)
                .ConfigureAwait(false);
        }

        logger.LogInformation("Drag destination at ({X}, {Y}) for \"{Target}\".", endPx, endPy, destination);
        await EmitDebugAsync(debugLog, onProgress, new AgentDebugEntry("Drag Destination Coordinates", Text: $"({endPx}, {endPy})")).ConfigureAwait(false);

        // Perform the drag
        string methodCalled = $"ClickDrag_MonitorCoords({startPx}, {startPy}, {endPx}, {endPy})";
        await EmitDebugAsync(debugLog, onProgress, new AgentDebugEntry("OS Input Call", Text: methodCalled)).ConfigureAwait(false);

        await inputProvider.ClickDrag_MonitorCoords(startPx, startPy, endPx, endPy).ConfigureAwait(false);

        string fromStr = $"({startPx.ToString(CultureInfo.InvariantCulture)}, {startPy.ToString(CultureInfo.InvariantCulture)})";
        string toStr = $"({endPx.ToString(CultureInfo.InvariantCulture)}, {endPy.ToString(CultureInfo.InvariantCulture)})";
        return new ActionExecutionResult(true, $"ClickDrag from {fromStr} to {toStr} (\"{source}\" → \"{destination}\").", IsTerminal: false, GoalAchieved: false);
    }

    /// <summary>Resolves a target description to absolute screen coordinates using the coordinate prompter.</summary>
    private async Task<(int px, int py)> ResolveTargetCoordinatesAsync(
        byte[] screenshot, string target, CancellationToken cancellationToken,
        List<AgentDebugEntry>? debugLog, Func<AgentDebugEntry, Task>? onProgress = null)
    {
        Func<CoordinatePrompter.CoordinateStep, Task>? onCoordStep = null;
        if (debugLog is not null || onProgress is not null)
        {
            onCoordStep = async coordStep =>
            {
                await EmitDebugAsync(debugLog, onProgress, new AgentDebugEntry(
                    $"Coord Step {coordStep.StepNumber}: Grid Image",
                    ImageBase64: Convert.ToBase64String(coordStep.GridImage))).ConfigureAwait(false);

                await EmitDebugAsync(debugLog, onProgress, new AgentDebugEntry(
                    $"Coord Step {coordStep.StepNumber}: AI Response",
                    Text: coordStep.AiResponseText)).ConfigureAwait(false);

                if (coordStep.ParsedX.HasValue && coordStep.ParsedY.HasValue)
                {
                    await EmitDebugAsync(debugLog, onProgress, new AgentDebugEntry(
                        $"Coord Step {coordStep.StepNumber}: Parsed Coordinates",
                        Text: $"({coordStep.ParsedX:F1}, {coordStep.ParsedY:F1})")).ConfigureAwait(false);
                }

                await EmitDebugAsync(debugLog, onProgress, new AgentDebugEntry(
                    $"Coord Step {coordStep.StepNumber}: Annotated Image",
                    ImageBase64: Convert.ToBase64String(coordStep.AnnotatedImage))).ConfigureAwait(false);
            };
        }

        (double x, double y, _, _) = await coordinatePrompter
            .GetCoordinatesForItemAsync(screenshot, target, onStepCompleted: onCoordStep, cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        var (originX, originY) = screenProvider.GetVirtualScreenOrigin();
        int px = (int)Math.Round(x) + originX;
        int py = (int)Math.Round(y) + originY;

        return (px, py);
    }

    private async Task<ActionExecutionResult> ExecuteTypeTextAsync(AgentAction action, List<AgentDebugEntry>? debugLog, Func<AgentDebugEntry, Task>? onProgress = null)
    {
        string text = action.Text ?? throw new InvalidOperationException("TypeText requires Text.");

        logger.LogInformation("Typing text: \"{Text}\"", text);
        await EmitDebugAsync(debugLog, onProgress, new AgentDebugEntry("OS Input Call", Text: $"TypeTextAsync(\"{text}\")")).ConfigureAwait(false);

        await inputProvider.TypeTextAsync(text).ConfigureAwait(false);

        return new ActionExecutionResult(true, $"Typed \"{text}\".", IsTerminal: false, GoalAchieved: false);
    }

    private async Task<ActionExecutionResult> ExecuteKeyComboAsync(AgentAction action, List<AgentDebugEntry>? debugLog, Func<AgentDebugEntry, Task>? onProgress = null)
    {
        string key = action.Key ?? throw new InvalidOperationException("KeyCombo requires Key.");
        bool ctrl  = action.Modifiers.HasFlag(ModifierKeys.Ctrl);
        bool shift = action.Modifiers.HasFlag(ModifierKeys.Shift);
        bool alt   = action.Modifiers.HasFlag(ModifierKeys.Alt);
        bool win   = action.Modifiers.HasFlag(ModifierKeys.Win);

        logger.LogInformation("Key combo: {Key} (ctrl={Ctrl}, shift={Shift}, alt={Alt}, win={Win}).", key, ctrl, shift, alt, win);
        await EmitDebugAsync(debugLog, onProgress, new AgentDebugEntry("OS Input Call",
            Text: $"SendModKeyComboAsync(\"{key}\", ctrl={ctrl}, shift={shift}, alt={alt}, win={win})")).ConfigureAwait(false);

        await inputProvider.SendModKeyComboAsync(key, ctrl ? true : null, shift ? true : null, alt ? true : null, win ? true : null).ConfigureAwait(false);

        string combo = FormatKeyCombo(key, ctrl, shift, alt, win);
        return new ActionExecutionResult(true, $"Pressed {combo}.", IsTerminal: false, GoalAchieved: false);
    }

    private async Task<ActionExecutionResult> ExecuteScrollAsync(AgentAction action, List<AgentDebugEntry>? debugLog, Func<AgentDebugEntry, Task>? onProgress = null)
    {
        logger.LogInformation("{Kind} by {Amount}.", action.Kind, action.Amount);
        await EmitDebugAsync(debugLog, onProgress, new AgentDebugEntry("OS Input Call",
            Text: $"{(action.Kind == AgentActionKind.ScrollUp ? "ScrollUp" : "ScrollDown")}({action.Amount})")).ConfigureAwait(false);

        if (action.Kind == AgentActionKind.ScrollUp)
            await inputProvider.ScrollUp(action.Amount).ConfigureAwait(false);
        else
            await inputProvider.ScrollDown(action.Amount).ConfigureAwait(false);

        return new ActionExecutionResult(true, $"{action.Kind} by {action.Amount}.", IsTerminal: false, GoalAchieved: false);
    }

    private static async Task<ActionExecutionResult> ExecuteWaitAsync(AgentAction action, CancellationToken cancellationToken, List<AgentDebugEntry>? debugLog, Func<AgentDebugEntry, Task>? onProgress = null)
    {
        int ms = action.Amount * 1000;
        await EmitDebugAsync(debugLog, onProgress, new AgentDebugEntry("OS Input Call", Text: $"Task.Delay({ms}ms)")).ConfigureAwait(false);

        await Task.Delay(ms, cancellationToken).ConfigureAwait(false);
        return new ActionExecutionResult(true, $"Waited {action.Amount} second(s).", IsTerminal: false, GoalAchieved: false);
    }

    /// <summary>Adds an entry to the debug log and streams it via the progress callback if available.</summary>
    private static async Task EmitDebugAsync(
        List<AgentDebugEntry>? debugLog,
        Func<AgentDebugEntry, Task>? onProgress,
        AgentDebugEntry entry)
    {
        debugLog?.Add(entry);
        if (onProgress is not null)
            await onProgress(entry).ConfigureAwait(false);
    }

    private static string FormatKeyCombo(string key, bool ctrl, bool shift, bool alt, bool win)
    {
        var parts = new List<string>(5);
        if (ctrl) parts.Add("Ctrl");
        if (shift) parts.Add("Shift");
        if (alt) parts.Add("Alt");
        if (win) parts.Add("Win");
        parts.Add(key.ToUpperInvariant());
        return string.Join('+', parts);
    }
}
