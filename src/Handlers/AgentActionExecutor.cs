using System.Diagnostics;
using System.Globalization;

namespace Thio_Universal_Agent;

/// <summary>
/// Executes a single <see cref="AgentAction"/> by dispatching to the appropriate <see cref="IInputProvider"/> or <see cref="CoordinatePrompter"/> methods.
/// When <see cref="GeneralConfig.EnableDebugMode"/> is true, captures detailed debug entries including coordinate resolution steps.
/// </summary>
public sealed partial class AgentActionExecutor(
    IInputProvider inputProvider,
    CoordinatePrompter coordinatePrompter,
    AppConfig appConfig,
    ILogger<AgentActionExecutor> logger)
{
    private readonly int _doubleClickDelayMs = appConfig.General.DoubleClickDelayMs;

    /// <summary>
    /// Executes the given action against the OS.
    /// For click/move actions, <paramref name="currentScreenshot"/> is used by the
    /// <see cref="CoordinatePrompter"/> to resolve the target description to pixel coordinates.
    /// </summary>
    /// <param name="onProgress">Optional callback invoked for each debug entry as it is produced, enabling real-time streaming to the UI before the full step completes.</param>
    public async Task<ActionExecutionResult> ExecuteAsync(
        AgentAction action,
        Screenshot screenshot,
        Func<AgentDebugEntry, Task>? onProgress = null,
        CancellationToken cancellationToken = default
        )
    {
        ArgumentNullException.ThrowIfNull(action);
        ArgumentNullException.ThrowIfNull(screenshot);

        List<AgentDebugEntry>? debugLog = [];

        try
        {
            ActionExecutionResult result = action.Kind switch
            {
                AgentActionKind.LeftClick or AgentActionKind.RightClick or AgentActionKind.DoubleClick or AgentActionKind.MiddleClick or AgentActionKind.MoveMouse or
                AgentActionKind.LeftClickCoords or AgentActionKind.RightClickCoords or AgentActionKind.DoubleClickCoords or AgentActionKind.MiddleClickCoords or AgentActionKind.MoveMouseCoords
                    => await ExecuteClickAsync(action, screenshot, debugLog, cancellationToken, onProgress).ConfigureAwait(false),

                AgentActionKind.ClickDrag or AgentActionKind.ClickDragCoords
                    => await ExecuteClickDragAsync(action, screenshot, debugLog, cancellationToken, onProgress).ConfigureAwait(false),

                AgentActionKind.TypeText
                    => await ExecuteTypeTextAsync(action, debugLog, onProgress).ConfigureAwait(false),

                AgentActionKind.KeyCombo
                    => await ExecuteKeyComboAsync(action, debugLog, onProgress).ConfigureAwait(false),

                AgentActionKind.ScrollUp or AgentActionKind.ScrollDown
                    => await ExecuteScrollAsync(action, debugLog, onProgress).ConfigureAwait(false),

                AgentActionKind.Wait
                    => await ExecuteWaitAsync(action, debugLog, cancellationToken, onProgress).ConfigureAwait(false),

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
            LogFailedToExecute(logger, ex, action.Kind);
            await EmitDebugAsync(debugLog, onProgress, new AgentDebugEntry("Execution Exception", Text: ex.ToString())).ConfigureAwait(false);
            var errorResult = new ActionExecutionResult(false, $"Execution error: {ex.Message}", IsTerminal: false, GoalAchieved: false);
            return debugLog is { Count: > 0 } ? errorResult with { DebugEntries = debugLog } : errorResult;
        }
    }

    /// <summary>Resolves the target to coordinates, then dispatches to the correct click/move method.</summary>
    private async Task<ActionExecutionResult> ExecuteClickAsync(
        AgentAction action,
        Screenshot screenshot,
        List<AgentDebugEntry>? debugLog,
        CancellationToken cancellationToken,
        Func<AgentDebugEntry, Task>? onProgress = null
        )
    {
        string target = action.Target ?? throw new InvalidOperationException($"{action.Kind} requires a Target.");

        // If the alt mode is CurrentCursorPosition, skip coordinate resolution and click in place.
        if (action.AltMode == AgentActionAltMode.CurrentCursorPosition)
        {
            var (curX, curY) = inputProvider.GetCursorPosition();
            LogActionAtCursorPosition(logger, action.Kind, curX, curY);
            await EmitDebugAsync(debugLog, onProgress, new AgentDebugEntry("Current Cursor Position", Text: $"({curX}, {curY})")).ConfigureAwait(false);

            if (appConfig.General.EnableDebugMode && (debugLog is not null || onProgress is not null))
            {
                var (imgX, imgY) = screenshot.ToImageRelative(curX, curY);
                screenshot.Annotated = CoordinatePrompter.CreateAnnotatedImage_PixelCoords(screenshot.Processed, imgX, imgY);
                await EmitDebugAsync(debugLog, onProgress, new AgentDebugEntry("Annotated Screenshot", ImageBase64: Convert.ToBase64String(screenshot.Annotated))).ConfigureAwait(false);
            }

            string cursorMethodCalled;
            switch (action.Kind)
            {
                case AgentActionKind.LeftClick or AgentActionKind.LeftClickCoords:
                    await inputProvider.LeftClick_MonitorCoords(curX, curY).ConfigureAwait(false);
                    cursorMethodCalled = $"LeftClick_MonitorCoords({curX}, {curY})";
                    break;

                case AgentActionKind.RightClick or AgentActionKind.RightClickCoords:
                    await inputProvider.RightClick_MonitorCoords(curX, curY).ConfigureAwait(false);
                    cursorMethodCalled = $"RightClick_MonitorCoords({curX}, {curY})";
                    break;

                case AgentActionKind.DoubleClick or AgentActionKind.DoubleClickCoords:
                    await inputProvider.DoubleClick_MonitorCoords(curX, curY).ConfigureAwait(false);
                    cursorMethodCalled = $"DoubleClick_MonitorCoords({curX}, {curY})";
                    break;

                case AgentActionKind.MiddleClick or AgentActionKind.MiddleClickCoords:
                    await inputProvider.MiddleMouse_MonitorCoords(curX, curY).ConfigureAwait(false);
                    cursorMethodCalled = $"MiddleMouse_MonitorCoords({curX}, {curY})";
                    break;

                case AgentActionKind.MoveMouse or AgentActionKind.MoveMouseCoords:
                    await inputProvider.MoveMouse_MonitorCoords(curX, curY).ConfigureAwait(false);
                    cursorMethodCalled = $"MoveMouse_MonitorCoords({curX}, {curY})";
                    break;

                default:
                    cursorMethodCalled = "N/A";
                    break;
            }

            await EmitDebugAsync(debugLog, onProgress, new AgentDebugEntry("OS Input Call", Text: cursorMethodCalled)).ConfigureAwait(false);

            string cursorCoordStr = $"({curX.ToString(CultureInfo.InvariantCulture)}, {curY.ToString(CultureInfo.InvariantCulture)})";
            return new ActionExecutionResult(true, $"{action.Kind} at current cursor position {cursorCoordStr}.", IsTerminal: false, GoalAchieved: false);
        }
        // If exact coordinates were supplied directly, parse and dispatch without AI resolution.
        else if (action.AltMode == AgentActionAltMode.ExactCoords)
        {
            ScreenCoordinate coord = ScreenCoordinate.FromNormalizedCoordsString(target, screenshot);

            if (appConfig.General.EnableDebugMode && (debugLog is not null || onProgress is not null))
            {
                screenshot.Annotated = CoordinatePrompter.CreateAnnotatedImage_PixelCoords(screenshot.Processed, coord.ImageX, coord.ImageY);
                await EmitDebugAsync(debugLog, onProgress, new AgentDebugEntry("Annotated Screenshot", ImageBase64: Convert.ToBase64String(screenshot.Annotated))).ConfigureAwait(false);
            }

            LogActionAtExactCoords(logger, action.Kind, coord.AbsoluteX, coord.AbsoluteY);
            await EmitDebugAsync(debugLog, onProgress, new AgentDebugEntry("Exact Coordinates", Text: coord.ToString())).ConfigureAwait(false);

            string exactMethodCalled;
            switch (action.Kind)
            {
                case AgentActionKind.LeftClick or AgentActionKind.LeftClickCoords:
                    await inputProvider.LeftClick_MonitorCoords(coord.AbsoluteX, coord.AbsoluteY).ConfigureAwait(false);
                    exactMethodCalled = $"LeftClick_MonitorCoords({coord.AbsoluteX}, {coord.AbsoluteY})";
                    break;

                case AgentActionKind.RightClick or AgentActionKind.RightClickCoords:
                    await inputProvider.RightClick_MonitorCoords(coord.AbsoluteX, coord.AbsoluteY).ConfigureAwait(false);
                    exactMethodCalled = $"RightClick_MonitorCoords({coord.AbsoluteX}, {coord.AbsoluteY})";
                    break;

                case AgentActionKind.DoubleClick or AgentActionKind.DoubleClickCoords:
                    await inputProvider.DoubleClick_MonitorCoords(coord.AbsoluteX, coord.AbsoluteY).ConfigureAwait(false);
                    exactMethodCalled = $"DoubleClick_MonitorCoords({coord.AbsoluteX}, {coord.AbsoluteY})";
                    break;

                case AgentActionKind.MiddleClick or AgentActionKind.MiddleClickCoords:
                    await inputProvider.MiddleMouse_MonitorCoords(coord.AbsoluteX, coord.AbsoluteY).ConfigureAwait(false);
                    exactMethodCalled = $"MiddleMouse_MonitorCoords({coord.AbsoluteX}, {coord.AbsoluteY})";
                    break;

                case AgentActionKind.MoveMouse or AgentActionKind.MoveMouseCoords:
                    await inputProvider.MoveMouse_MonitorCoords(coord.AbsoluteX, coord.AbsoluteY).ConfigureAwait(false);
                    exactMethodCalled = $"MoveMouse_MonitorCoords({coord.AbsoluteX}, {coord.AbsoluteY})";
                    break;

                default:
                    exactMethodCalled = "N/A";
                    break;
            }

            await EmitDebugAsync(debugLog, onProgress, new AgentDebugEntry("OS Input Call", Text: exactMethodCalled)).ConfigureAwait(false);

            return new ActionExecutionResult(true, $"{action.Kind} at exact coordinates {coord}.", IsTerminal: false, GoalAchieved: false);
        }
        // If we need to resolve the coordinates from natural language description
        else
        {
            LogResolvingCoordinates(logger, target);
            await EmitDebugAsync(debugLog, onProgress, new AgentDebugEntry("Coordinate Resolution", Text: $"Resolving target: \"{target}\"")).ConfigureAwait(false);

            // When debug mode is enabled, capture every intermediate CoordinatePrompter step
            Func<CoordinatePrompter.CoordinateStep, Task>? onCoordStep = null;
            if (appConfig.General.EnableDebugMode && (debugLog is not null || onProgress is not null))
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

            var coordSw = Stopwatch.StartNew();
            ScreenCoordinate coord = await coordinatePrompter
                .GetCoordinatesForItemAsync(screenshot, target, onStepCompleted: onCoordStep, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            coordSw.Stop();

            LogActionAtResolvedCoords(logger, action.Kind, coord.AbsoluteX, coord.AbsoluteY, target);
            await EmitDebugAsync(debugLog, onProgress, new AgentDebugEntry("Final Resolved Coordinates", Text: coord.ToString())).ConfigureAwait(false);

            string methodCalled;
            switch (action.Kind)
            {
                case AgentActionKind.LeftClick or AgentActionKind.LeftClickCoords:
                    await inputProvider.LeftClick_MonitorCoords(coord.AbsoluteX, coord.AbsoluteY).ConfigureAwait(false);
                    methodCalled = $"LeftClick_MonitorCoords({coord.AbsoluteX}, {coord.AbsoluteY})";
                    break;

                case AgentActionKind.RightClick or AgentActionKind.RightClickCoords:
                    await inputProvider.RightClick_MonitorCoords(coord.AbsoluteX, coord.AbsoluteY).ConfigureAwait(false);
                    methodCalled = $"RightClick_MonitorCoords({coord.AbsoluteX}, {coord.AbsoluteY})";
                    break;

                case AgentActionKind.DoubleClick or AgentActionKind.DoubleClickCoords:
                    await inputProvider.DoubleClick_MonitorCoords(coord.AbsoluteX, coord.AbsoluteY).ConfigureAwait(false);
                    methodCalled = $"DoubleClick_MonitorCoords({coord.AbsoluteX}, {coord.AbsoluteY})";
                    break;

                case AgentActionKind.MiddleClick or AgentActionKind.MiddleClickCoords:
                    await inputProvider.MiddleMouse_MonitorCoords(coord.AbsoluteX, coord.AbsoluteY).ConfigureAwait(false);
                    methodCalled = $"MiddleMouse_MonitorCoords({coord.AbsoluteX}, {coord.AbsoluteY})";
                    break;

                case AgentActionKind.MoveMouse or AgentActionKind.MoveMouseCoords:
                    await inputProvider.MoveMouse_MonitorCoords(coord.AbsoluteX, coord.AbsoluteY).ConfigureAwait(false);
                    methodCalled = $"MoveMouse_MonitorCoords({coord.AbsoluteX}, {coord.AbsoluteY})";
                    break;

                default:
                    methodCalled = "N/A";
                    break;
            }

            await EmitDebugAsync(debugLog, onProgress, new AgentDebugEntry("OS Input Call", Text: methodCalled)).ConfigureAwait(false);

            string normSuffix = $" (1000x1000 Normalized @ X={coord.NormalizedX:F0}, Y={coord.NormalizedY:F0})";
            return new ActionExecutionResult(true, $"{action.Kind} at {coord}{normSuffix} targeting \"{target}\".", IsTerminal: false, GoalAchieved: false, CoordResolutionMs: coordSw.ElapsedMilliseconds);
        }
    }



    /// <summary>Resolves source and destination targets to coordinates, then performs a click-drag.</summary>
    private async Task<ActionExecutionResult> ExecuteClickDragAsync(
        AgentAction action,
        Screenshot screenshot,
        List<AgentDebugEntry>? debugLog,
        CancellationToken cancellationToken,
        Func<AgentDebugEntry, Task>? onProgress = null
        )
    {
        string source = action.Target ?? throw new InvalidOperationException("ClickDrag requires a Target (source).");
        string destination = action.DragTarget ?? throw new InvalidOperationException("ClickDrag requires a DragTarget (destination).");

        // Resolve source coordinates
        LogResolvingDragSource(logger, source);
        await EmitDebugAsync(debugLog, onProgress, new AgentDebugEntry("Drag Source Resolution", Text: $"Resolving source: \"{source}\"")).ConfigureAwait(false);

        int startPx, startPy, endPx, endPy;
        ScreenCoordinate? startCoordAi = null, endCoordAi = null;
        long startCoordMs = 0, endCoordMs = 0;
        long? totalCoordMs = null;

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
                ScreenCoordinate startCoord = ScreenCoordinate.FromNormalizedCoordsString(source, screenshot);
                startPx = startCoord.AbsoluteX;
                startPy = startCoord.AbsoluteY;
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
                ScreenCoordinate endCoord = ScreenCoordinate.FromNormalizedCoordsString(destination, screenshot);
                endPx = endCoord.AbsoluteX;
                endPy = endCoord.AbsoluteY;
            }
        }
        else
        {
            (startCoordAi, startCoordMs) = await ResolveTargetCoordinatesAsync(screenshot, source, debugLog, cancellationToken, onProgress)
                .ConfigureAwait(false);
            startPx = startCoordAi.AbsoluteX;
            startPy = startCoordAi.AbsoluteY;

            LogDragSourceCoords(logger, startPx, startPy, source);
            await EmitDebugAsync(debugLog, onProgress, new AgentDebugEntry("Drag Source Coordinates", Text: startCoordAi.ToString())).ConfigureAwait(false);

            // Resolve destination coordinates
            LogResolvingDragDestination(logger, destination);
            await EmitDebugAsync(debugLog, onProgress, new AgentDebugEntry("Drag Destination Resolution", Text: $"Resolving destination: \"{destination}\"")).ConfigureAwait(false);

            (endCoordAi, endCoordMs) = await ResolveTargetCoordinatesAsync(screenshot, destination, debugLog, cancellationToken, onProgress)
                .ConfigureAwait(false);
            endPx = endCoordAi.AbsoluteX;
            endPy = endCoordAi.AbsoluteY;

            totalCoordMs = startCoordMs + endCoordMs;
        }

        LogDragDestinationCoords(logger, endPx, endPy, destination);
        await EmitDebugAsync(debugLog, onProgress, new AgentDebugEntry("Drag Destination Coordinates", Text: endCoordAi?.ToString() ?? $"({endPx}, {endPy})")).ConfigureAwait(false);

        // Emit a combined annotated screenshot showing both start (green) and end (red) crosshairs
        if (appConfig.General.EnableDebugMode && (debugLog is not null || onProgress is not null))
        {
            int startImgX = startCoordAi?.ImageX ?? startPx - screenshot.OriginX;
            int startImgY = startCoordAi?.ImageY ?? startPy - screenshot.OriginY;
            int endImgX   = endCoordAi?.ImageX   ?? endPx   - screenshot.OriginX;
            int endImgY   = endCoordAi?.ImageY   ?? endPy   - screenshot.OriginY;
            screenshot.Annotated = CoordinatePrompter.CreateAnnotatedImageDrag(
                screenshot.Processed,
                startImgX, startImgY,
                endImgX, endImgY);
            await EmitDebugAsync(debugLog, onProgress, new AgentDebugEntry(
                "Drag: Start → End",
                ImageBase64: Convert.ToBase64String(screenshot.Annotated))).ConfigureAwait(false);
        }

        // Perform the drag
        string methodCalled = $"ClickDrag_MonitorCoords({startPx}, {startPy}, {endPx}, {endPy})";
        await EmitDebugAsync(debugLog, onProgress, new AgentDebugEntry("OS Input Call", Text: methodCalled)).ConfigureAwait(false);

        await inputProvider.ClickDrag_MonitorCoords(startPx, startPy, endPx, endPy).ConfigureAwait(false);

        string fromStr = $"({startPx.ToString(CultureInfo.InvariantCulture)}, {startPy.ToString(CultureInfo.InvariantCulture)})";
        string toStr = $"({endPx.ToString(CultureInfo.InvariantCulture)}, {endPy.ToString(CultureInfo.InvariantCulture)})";
        return new ActionExecutionResult(true, $"ClickDrag from {fromStr} to {toStr} (\"{source}\" → \"{destination}\").", IsTerminal: false, GoalAchieved: false, CoordResolutionMs: totalCoordMs);
    }

    /// <summary>Resolves a target description to absolute screen coordinates using the coordinate prompter.</summary>
    private async Task<(ScreenCoordinate coord, long coordMs)> ResolveTargetCoordinatesAsync(
        Screenshot screenshot, string target,
        List<AgentDebugEntry>? debugLog,
        CancellationToken cancellationToken,
        Func<AgentDebugEntry, Task>? onProgress = null
        )
    {
        Func<CoordinatePrompter.CoordinateStep, Task>? onCoordStep = null;
        if (appConfig.General.EnableDebugMode && (debugLog is not null || onProgress is not null))
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

        var coordSw = Stopwatch.StartNew();
        ScreenCoordinate coord = await coordinatePrompter
            .GetCoordinatesForItemAsync(screenshot, target, onStepCompleted: onCoordStep, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        coordSw.Stop();

        return (coord, coordSw.ElapsedMilliseconds);
    }

    private async Task<ActionExecutionResult> ExecuteTypeTextAsync(AgentAction action, List<AgentDebugEntry>? debugLog, Func<AgentDebugEntry, Task>? onProgress = null)
    {
        string text = action.Text ?? throw new InvalidOperationException("TypeText requires Text.");

        LogTypingText(logger, text);
        await EmitDebugAsync(debugLog, onProgress, new AgentDebugEntry("OS Input Call", Text: $"TypeTextAsync(\"{text}\")")).ConfigureAwait(false);

        await inputProvider.TypeTextAsync(text).ConfigureAwait(false);

        return new ActionExecutionResult(true, $"Typed \"{text}\".", IsTerminal: false, GoalAchieved: false);
    }

    private async Task<ActionExecutionResult> ExecuteKeyComboAsync(AgentAction action, List<AgentDebugEntry>? debugLog, Func<AgentDebugEntry, Task>? onProgress = null)
    {
        // Require at least one key press. We'll even allow only modifiers to be pressed.
        if (action.Key == null && action.Modifiers == ModifierKeys.None)
            throw new InvalidOperationException("KeyCombo requires Key.");

        string? key = action.Key;
        bool ctrl  = action.Modifiers.HasFlag(ModifierKeys.Ctrl);
        bool shift = action.Modifiers.HasFlag(ModifierKeys.Shift);
        bool alt   = action.Modifiers.HasFlag(ModifierKeys.Alt);
        bool win   = action.Modifiers.HasFlag(ModifierKeys.Win);

        LogKeyCombo(logger, key ?? "[None]", ctrl, shift, alt, win);
        await EmitDebugAsync(debugLog, onProgress, new AgentDebugEntry("OS Input Call",
            Text: $"SendModKeyComboAsync(\"{key ?? "[None]"}\", ctrl={ctrl}, shift={shift}, alt={alt}, win={win})")).ConfigureAwait(false);

        await inputProvider.SendModKeyComboAsync(key, ctrl ? true : null, shift ? true : null, alt ? true : null, win ? true : null).ConfigureAwait(false);

        string combo = FormatKeyCombo(key??"[None]", ctrl, shift, alt, win);
        return new ActionExecutionResult(true, $"Pressed {combo}.", IsTerminal: false, GoalAchieved: false);
    }

    private async Task<ActionExecutionResult> ExecuteScrollAsync(AgentAction action, List<AgentDebugEntry>? debugLog, Func<AgentDebugEntry, Task>? onProgress = null)
    {
        LogScroll(logger, action.Kind, action.Amount);
        await EmitDebugAsync(debugLog, onProgress, new AgentDebugEntry("OS Input Call",
            Text: $"{(action.Kind == AgentActionKind.ScrollUp ? "ScrollUp" : "ScrollDown")}({action.Amount})")).ConfigureAwait(false);

        if (action.Kind == AgentActionKind.ScrollUp)
            await inputProvider.ScrollUp(action.Amount).ConfigureAwait(false);
        else
            await inputProvider.ScrollDown(action.Amount).ConfigureAwait(false);

        return new ActionExecutionResult(true, $"{action.Kind} by {action.Amount}.", IsTerminal: false, GoalAchieved: false);
    }

    private static async Task<ActionExecutionResult> ExecuteWaitAsync(AgentAction action, List<AgentDebugEntry>? debugLog, CancellationToken cancellationToken, Func<AgentDebugEntry, Task>? onProgress = null)
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

    [LoggerMessage(Level = LogLevel.Error, Message = "Failed to execute {ActionKind}.")]
    private static partial void LogFailedToExecute(ILogger logger, Exception ex, AgentActionKind actionKind);

    [LoggerMessage(Level = LogLevel.Information, Message = "{ActionKind} at current cursor position ({X}, {Y}).")]
    private static partial void LogActionAtCursorPosition(ILogger logger, AgentActionKind actionKind, int x, int y);

    [LoggerMessage(Level = LogLevel.Information, Message = "{ActionKind} at exact coordinates ({X}, {Y}).")]
    private static partial void LogActionAtExactCoords(ILogger logger, AgentActionKind actionKind, int x, int y);

    [LoggerMessage(Level = LogLevel.Information, Message = "Resolving coordinates for: \"{Target}\"")]
    private static partial void LogResolvingCoordinates(ILogger logger, string target);

    [LoggerMessage(Level = LogLevel.Information, Message = "{ActionKind} at ({X}, {Y}) for \"{Target}\".")]
    private static partial void LogActionAtResolvedCoords(ILogger logger, AgentActionKind actionKind, int x, int y, string target);

    [LoggerMessage(Level = LogLevel.Information, Message = "Resolving drag source coordinates for: \"{Target}\"")]
    private static partial void LogResolvingDragSource(ILogger logger, string target);

    [LoggerMessage(Level = LogLevel.Information, Message = "Drag source at ({X}, {Y}) for \"{Target}\".")]
    private static partial void LogDragSourceCoords(ILogger logger, int x, int y, string target);

    [LoggerMessage(Level = LogLevel.Information, Message = "Resolving drag destination coordinates for: \"{Target}\"")]
    private static partial void LogResolvingDragDestination(ILogger logger, string target);

    [LoggerMessage(Level = LogLevel.Information, Message = "Drag destination at ({X}, {Y}) for \"{Target}\".")]
    private static partial void LogDragDestinationCoords(ILogger logger, int x, int y, string target);

    [LoggerMessage(Level = LogLevel.Information, Message = "Typing text: \"{Text}\"")]
    private static partial void LogTypingText(ILogger logger, string text);

    [LoggerMessage(Level = LogLevel.Information, Message = "Key combo: {Key} (ctrl={Ctrl}, shift={Shift}, alt={Alt}, win={Win}).")]
    private static partial void LogKeyCombo(ILogger logger, string key, bool ctrl, bool shift, bool alt, bool win);

    [LoggerMessage(Level = LogLevel.Information, Message = "{Kind} by {Amount}.")]
    private static partial void LogScroll(ILogger logger, AgentActionKind kind, int amount);
}
