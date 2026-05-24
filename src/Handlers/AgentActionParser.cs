using System.Diagnostics.CodeAnalysis;

namespace Thio_Universal_Agent.Handlers;

/// <summary>
/// Parses the AI model's raw text response into a structured <see cref="AgentParsedResponse"/>.
/// Expected format:
/// <code>
/// THOUGHT: &lt;reasoning&gt;
/// ACTION: &lt;TOOL_NAME&gt; &lt;arguments&gt;
/// </code>
/// </summary>
public static class AgentActionParser
{
    private const string ThoughtPrefix = "REASON:";
    private const string ActionPrefix = "ACTION:";
    private const string QueuePrefix = "QUEUE:";

    /// <summary> Attempts to parse the AI's response text into a thought and action. </summary>
    /// <param name="maxQueueSize">Maximum number of actions permitted in a QUEUE: block; sourced from <see cref="GeneralConfig.MaxQueueSize"/>.</param>
    /// <returns>True if parsing succeeded; false otherwise.</returns>
    public static bool TryParse(string responseText, int maxQueueSize, [NotNullWhen(true)] out AgentParsedResponse? result, [NotNullWhen(false)] out string? error)
    {
        result = null;
        error = null;

        if (string.IsNullOrWhiteSpace(responseText))
        {
            error = "AI response was empty.";
            return false;
        }

        // Detect which keyword comes first: ACTION: or QUEUE:
        // Only match when the keyword appears at the very start of a line (ignoring leading whitespace).
        int actionIndex = IndexOfLineStart(responseText, ActionPrefix);
        int queueIndex  = IndexOfLineStart(responseText, QueuePrefix);

        bool hasQueue  = queueIndex  >= 0 && (actionIndex < 0 || queueIndex < actionIndex);
        bool hasAction = actionIndex >= 0 && !hasQueue;

        if (!hasQueue && !hasAction)
        {
            error = "Response is missing the ACTION: or QUEUE: line.";
            return false;
        }

        if (hasQueue)
            return TryParseQueueResponse(responseText, queueIndex, maxQueueSize, out result, out error);

        // ── Single-action path (original behavior) ────────────────────────────

        // Everything before ACTION: is the thought (strip optional THOUGHT: prefix)
        string thoughtRaw = responseText[..actionIndex].Trim();
        if (thoughtRaw.StartsWith(ThoughtPrefix, StringComparison.OrdinalIgnoreCase))
            thoughtRaw = thoughtRaw[ThoughtPrefix.Length..].Trim();

        string thought = thoughtRaw.Length > 0 ? thoughtRaw : "(no reasoning provided)";

        // The action payload is everything after "ACTION:"
        string actionPayload = responseText[(actionIndex + ActionPrefix.Length)..].Trim();

        if (actionPayload.Length == 0)
        {
            error = "ACTION: line is present but empty.";
            return false;
        }

        if (!TryParseActionLine(actionPayload, out AgentAction? action, out error))
            return false;

        result = new AgentParsedResponse(thought, action);
        return true;
    }

    /// <summary>
    /// Parses a QUEUE: block that contains 1–<paramref name="maxQueueSize"/> action lines.
    /// </summary>
    private static bool TryParseQueueResponse(
        string responseText, int queueIndex, int maxQueueSize,
        [NotNullWhen(true)] out AgentParsedResponse? result,
        [NotNullWhen(false)] out string? error)
    {
        result = null;

        // Thought is everything before QUEUE:
        string thoughtRaw = responseText[..queueIndex].Trim();
        if (thoughtRaw.StartsWith(ThoughtPrefix, StringComparison.OrdinalIgnoreCase))
            thoughtRaw = thoughtRaw[ThoughtPrefix.Length..].Trim();
        string thought = thoughtRaw.Length > 0 ? thoughtRaw : "(no reasoning provided)";

        // The queue payload is everything after "QUEUE:"
        string queuePayload = responseText[(queueIndex + QueuePrefix.Length)..].Trim();
        if (queuePayload.Length == 0)
        {
            error = $"QUEUE: block is present but empty. Provide 1 to {maxQueueSize} action lines.";
            return false;
        }

        // Split into lines
        string[] lines = queuePayload.Split(["\r\n", "\n", "\r"], StringSplitOptions.None);

        // Group lines into per-action payloads.
        // A new action starts whenever a line begins with a known tool token.
        // Non-tool lines (e.g. CLICK_DRAG's From:/To: continuations) are appended to the current block.
        var actionPayloads = new List<string>(maxQueueSize);
        var currentBlock   = new System.Text.StringBuilder();

        foreach (string line in lines)
        {
            string trimmed = line.Trim();
            if (trimmed.Length == 0) continue;

            if (IsToolLine(trimmed) && currentBlock.Length > 0)
            {
                actionPayloads.Add(currentBlock.ToString().Trim());
                currentBlock.Clear();
            }

            currentBlock.AppendLine(trimmed);
        }

        if (currentBlock.Length > 0)
            actionPayloads.Add(currentBlock.ToString().Trim());

        if (actionPayloads.Count == 0)
        {
            error = "QUEUE: block contained no recognisable action lines.";
            return false;
        }

        if (actionPayloads.Count > maxQueueSize)
        {
            error = $"QUEUE: block contains {actionPayloads.Count} actions but the maximum is {maxQueueSize}.";
            return false;
        }

        var actions = new List<AgentAction>(actionPayloads.Count);
        foreach (string payload in actionPayloads)
        {
            if (!TryParseActionLine(payload, out AgentAction? act, out error))
                return false;
            if (act.Kind == AgentActionKind.Done)
            {
                error = "DONE cannot be queued. It must be issued as a standalone ACTION: to allow final visual verification before completing the task.";
                return false;
            }
            actions.Add(act);
        }

        result = new AgentParsedResponse(thought, actions[0], actions.Count > 1 ? actions : null);
        error = null;
        return true;
    }

    /// <summary>Returns true when <paramref name="line"/> starts with a recognised tool token.</summary>
    private static bool IsToolLine(string line)
    {
        int spaceIdx  = line.IndexOf(' ');
        string token  = spaceIdx >= 0 ? line[..spaceIdx] : line;
        string normalized = token.Trim().ToUpperInvariant().Replace("-", "_");
        return normalized is
            "LEFT_CLICK" or "RIGHT_CLICK" or "DOUBLE_CLICK" or "MIDDLE_CLICK" or
            "MOVE_MOUSE" or "CLICK_DRAG" or "TYPE_TEXT" or "KEY_COMBO" or
            "SCROLL_UP" or "SCROLL_DOWN" or "WAIT" or "DONE" or "FAIL";
    }

    private static bool TryParseActionLine(string payload, [NotNullWhen(true)] out AgentAction? action, [NotNullWhen(false)] out string? error)
    {
        action = null;
        error = null;

        // Isolate the first line (tool name + optional single-line arguments)
        int newlineIdx = payload.IndexOfAny(['\r', '\n']);
        string firstLine = newlineIdx >= 0 ? payload[..newlineIdx].Trim() : payload;

        // First token is the tool name
        int spaceIdx = firstLine.IndexOf(' ');
        string toolToken = spaceIdx >= 0 ? firstLine[..spaceIdx] : firstLine;
        string args = spaceIdx >= 0 ? firstLine[(spaceIdx + 1)..].Trim() : string.Empty;

        // Normalize: uppercase, underscores
        string normalized = toolToken.Trim().ToUpperInvariant().Replace("-", "_");

        switch (normalized)
        {
            case "LEFT_CLICK":
                return TryParseTargetAction(AgentActionKind.LeftClick, args, out action, out error);

            case "RIGHT_CLICK":
                return TryParseTargetAction(AgentActionKind.RightClick, args, out action, out error);

            case "DOUBLE_CLICK":
                return TryParseTargetAction(AgentActionKind.DoubleClick, args, out action, out error);

            case "MIDDLE_CLICK":
                return TryParseTargetAction(AgentActionKind.MiddleClick, args, out action, out error);

            case "MOVE_MOUSE":
                return TryParseTargetAction(AgentActionKind.MoveMouse, args, out action, out error);

            case "CLICK_DRAG":
                // Multi-line: pass everything after the tool token
                string dragArgs = payload[toolToken.Length..].Trim();
                return TryParseClickDragAction(dragArgs, out action, out error);

            case "TYPE_TEXT":
                return TryParseTextAction(args, out action, out error);

            case "KEY_COMBO":
                return TryParseKeyComboAction(args, out action, out error);

            case "SCROLL_UP":
                return TryParseScrollAction(AgentActionKind.ScrollUp, args, out action, out error);

            case "SCROLL_DOWN":
                return TryParseScrollAction(AgentActionKind.ScrollDown, args, out action, out error);

            case "WAIT":
                return TryParseWaitAction(args, out action, out error);

            case "DONE":
                action = new AgentAction(AgentActionKind.Done);
                return true;

            case "FAIL":
                string reason = StripQuotes(args);
                action = new AgentAction(AgentActionKind.Fail, Reason: reason.Length > 0 ? reason : "No reason provided.");
                return true;

            default:
                error = $"Unknown tool: '{toolToken}'. Expected one of: LEFT_CLICK, RIGHT_CLICK, DOUBLE_CLICK, MIDDLE_CLICK, MOVE_MOUSE, CLICK_DRAG, TYPE_TEXT, KEY_COMBO, SCROLL_UP, SCROLL_DOWN, WAIT, DONE, FAIL.";
                return false;
        }
    }

    /// <summary>
    /// Attempts to strip a leading modifier prefix (e.g. "ctrl+shift ") from <paramref name="args"/>.
    /// Returns the parsed <see cref="ModifierKeys"/> and the remaining argument string.
    /// If the first token contains any non-modifier word the method returns <see cref="ModifierKeys.None"/> and the original string.
    /// </summary>
    private static (ModifierKeys modifiers, string remainingArgs) TryStripModifierPrefix(string args)
    {
        int spaceIdx = args.IndexOf(' ');
        if (spaceIdx <= 0)
            return (ModifierKeys.None, args);

        string firstToken = args[..spaceIdx].ToLowerInvariant();
        string[] parts = firstToken.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        ModifierKeys modifiers = ModifierKeys.None;
        foreach (string part in parts)
        {
            switch (part)
            {
                case "ctrl" or "control": modifiers |= ModifierKeys.Ctrl;  break;
                case "shift":             modifiers |= ModifierKeys.Shift; break;
                case "alt":               modifiers |= ModifierKeys.Alt;   break;
                case "win" or "windows" or "super": modifiers |= ModifierKeys.Win; break;
                default:
                    // Non-modifier word in first token — not a modifier prefix
                    return (ModifierKeys.None, args);
            }
        }

        return modifiers == ModifierKeys.None
            ? (ModifierKeys.None, args)
            : (modifiers, args[(spaceIdx + 1)..].TrimStart());
    }

    /// <summary>Parses a click/move action whose argument is a quoted target description or a COORDS X,Y pair.</summary>
    private static bool TryParseTargetAction(
        AgentActionKind kind, string args,
        [NotNullWhen(true)] out AgentAction? action,
        [NotNullWhen(false)] out string? error)
    {
        action = null;

        StringSplitOptions splitOpts = StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries;
        string kindUpper = kind.ToString().ToUpperInvariant();

        // Strip optional leading modifier prefix, e.g. "ctrl+shift " from "ctrl+shift COORDS 100,200"
        var (modifiers, strippedArgs) = TryStripModifierPrefix(args);
        args = strippedArgs;

        // Handle COORDS keyword: e.g. LEFT_CLICK COORDS 100,200  or  LEFT_CLICK COORDS CURRENT
        if (args.StartsWith("COORDS ", StringComparison.OrdinalIgnoreCase))
        {
            string coordsPart = args["COORDS ".Length..].Trim();

            AgentActionKind coordsKind = kind switch
            {
                AgentActionKind.LeftClick   => AgentActionKind.LeftClickCoords,
                AgentActionKind.RightClick  => AgentActionKind.RightClickCoords,
                AgentActionKind.DoubleClick => AgentActionKind.DoubleClickCoords,
                AgentActionKind.MiddleClick => AgentActionKind.MiddleClickCoords,
                AgentActionKind.MoveMouse   => AgentActionKind.MoveMouseCoords,
                _                           => kind,
            };

            if (coordsPart.Equals("CURRENT", StringComparison.OrdinalIgnoreCase))
            {
                error = null;
                action = new AgentAction(coordsKind, Target: "[Current Cursor Position]", AltMode: AgentActionAltMode.CurrentCursorPosition, Modifiers: modifiers);
                return true;
            }

            // Parse "X,Y" — tolerate a space after the comma ("X, Y")
            string[] parts = coordsPart.Split(' ', splitOpts);
            string? coordStr = parts.Length switch
            {
                1 => parts[0],                          // "X,Y"
                2 => $"{parts[0]}{parts[1]}",           // "X, Y" (space after comma)
                _ => null,
            };

            if (coordStr is null)
            {
                error = $"Invalid COORDS format for {kindUpper}. Expected '{kindUpper} COORDS X,Y' or '{kindUpper} COORDS CURRENT'.";
                return false;
            }

            string[] xy = coordStr.Split(',', splitOpts);
            if (xy.Length != 2
                || !int.TryParse(xy[0], out int x)
                || !int.TryParse(xy[1], out int y))
            {
                error = $"Invalid COORDS format for {kindUpper}: '{coordsPart}'. Expected integer values like '{kindUpper} COORDS 100,200'.";
                return false;
            }

            error = null;
            action = new AgentAction(coordsKind, Target: $"{x},{y}", AltMode: AgentActionAltMode.ExactCoords, Modifiers: modifiers);
            return true;
        }

        string target = StripQuotes(args);
        if (target.Length == 0)
        {
            error = $"{kindUpper} requires a target description (e.g. {kindUpper} \"the OK button\"), the COORDS keyword (e.g. {kindUpper} COORDS 100,200), or the CURRENT keyword for current cursor location.";
            return false;
        }

        AgentActionAltMode altMode = AgentActionAltMode.None;
        // Check if it has the CURRENT keyword
        if (target.Equals("CURRENT", StringComparison.OrdinalIgnoreCase))
        {
            altMode = AgentActionAltMode.CurrentCursorPosition;
            target = "[Current Cursor Position]"; // Clear the target since we're using the CURRENT keyword
        }
        // Check if it starts with CURRENT but has other things, if so it's an error
        else if (target.StartsWith("CURRENT", StringComparison.OrdinalIgnoreCase))
        {
            error = $"{kindUpper} cannot have additional text after the CURRENT keyword.";
            return false;
        }

        error = null;
        action = new AgentAction(kind, Target: target, AltMode: altMode, Modifiers: modifiers);
        return true;
    }

    /// <summary>Parses CLICK_DRAG whose arguments are From: and To: lines with quoted target descriptions.</summary>
    private static bool TryParseClickDragAction(
        string args,
        [NotNullWhen(true)] out AgentAction? action,
        [NotNullWhen(false)] out string? error)
    {
        action = null;
        const string usage = "CLICK_DRAG requires From and To targets (e.g. CLICK_DRAG\nFrom: \"the file icon\"\nTo: \"the trash folder\")"
            + ", or the keyword COORDS followed by X1,Y1 X2,Y2 (e.g. CLICK_DRAG COORDS 100,200 300,400).";

        string? source = null;
        string? destination = null;

        StringSplitOptions splitOpts = StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries;

        // First check if it's a COORDS string
        if (args.StartsWith("COORDS ", StringComparison.OrdinalIgnoreCase))
        {
            // Split on spaces first
            string[] parts = args["COORDS ".Length..].Split(' ', splitOpts);

            // Each coordinate pair is either an "X,Y" token or the keyword CURRENT.
            // Supported part counts:
            //   2 — "X1,Y1 X2,Y2"  or  "CURRENT X2,Y2"  or  "X1,Y1 CURRENT"  or  "CURRENT CURRENT"
            //   4 — model added a space after comma: "X1, Y1 X2, Y2"
            //        (CURRENT produces only 1 token so length-4 only applies when both pairs are numeric)

            bool pair1IsCurrent = parts.Length >= 1 && parts[0].Equals("CURRENT", StringComparison.OrdinalIgnoreCase);
            bool pair2IsCurrent = false;
            string? pair1Str = null;   // "X1,Y1" — null when CURRENT
            string? pair2Str = null;   // "X2,Y2" — null when CURRENT

            if (parts.Length == 2)
            {
                pair1IsCurrent = parts[0].Equals("CURRENT", StringComparison.OrdinalIgnoreCase);
                pair2IsCurrent = parts[1].Equals("CURRENT", StringComparison.OrdinalIgnoreCase);
                if (!pair1IsCurrent) pair1Str = parts[0];
                if (!pair2IsCurrent) pair2Str = parts[1];
            }
            else if (parts.Length == 4)
            {
                // Model wrote "X1, Y1 X2, Y2" — rejoin comma-separated halves
                pair1Str = $"{parts[0]}{parts[1]}";
                pair2Str = $"{parts[2]}{parts[3]}";
            }
            else if (parts.Length == 3)
            {
                // One side is CURRENT, the other used "X, Y" (space after comma)
                if (parts[0].Equals("CURRENT", StringComparison.OrdinalIgnoreCase))
                {
                    pair1IsCurrent = true;
                    pair2Str = $"{parts[1]}{parts[2]}";
                }
                else
                {
                    pair1Str = $"{parts[0]}{parts[1]}";
                    pair2IsCurrent = true;
                }
            }
            else
            {
                error = $"Invalid COORDS format. Expected 'CLICK_DRAG COORDS X1,Y1 X2,Y2' with optional CURRENT for either pair (e.g. CLICK_DRAG COORDS CURRENT 300,400).";
                return false;
            }

            // Parse whichever pairs are not CURRENT
            int x1 = 0, y1 = 0, x2 = 0, y2 = 0;
            if (!pair1IsCurrent)
            {
                string[] p1 = pair1Str!.Split(',', splitOpts);
                if (p1.Length != 2 || !int.TryParse(p1[0], out x1) || !int.TryParse(p1[1], out y1))
                {
                    error = $"Invalid COORDS format for start pair '{pair1Str}'. Expected 'CLICK_DRAG COORDS X1,Y1 X2,Y2'.";
                    return false;
                }
            }
            if (!pair2IsCurrent)
            {
                string[] p2 = pair2Str!.Split(',', splitOpts);
                if (p2.Length != 2 || !int.TryParse(p2[0], out x2) || !int.TryParse(p2[1], out y2))
                {
                    error = $"Invalid COORDS format for end pair '{pair2Str}'. Expected 'CLICK_DRAG COORDS X1,Y1 X2,Y2'.";
                    return false;
                }
            }

            // Determine AltMode and build the action
            AgentActionAltMode dragAltMode;
            string sourceTarget;
            string destTarget;

            if (pair1IsCurrent && pair2IsCurrent)
            {
                dragAltMode = AgentActionAltMode.CurrentCursorPositionBoth;
                sourceTarget = "[Current Cursor Position]";
                destTarget   = "[Current Cursor Position]";
            }
            else if (pair1IsCurrent)
            {
                dragAltMode  = AgentActionAltMode.CurrentCursorPositionStart;
                sourceTarget = "[Current Cursor Position]";
                destTarget   = $"{x2},{y2}";
            }
            else if (pair2IsCurrent)
            {
                dragAltMode  = AgentActionAltMode.CurrentCursorPositionEnd;
                sourceTarget = $"{x1},{y1}";
                destTarget   = "[Current Cursor Position]";
            }
            else
            {
                dragAltMode  = AgentActionAltMode.ExactCoords;
                sourceTarget = $"{x1},{y1}";
                destTarget   = $"{x2},{y2}";
            }

            error = null;
            action = new AgentAction(AgentActionKind.ClickDragCoords, Target: sourceTarget, DragTarget: destTarget, AltMode: dragAltMode);
            return true;
        }
        else
        {

            string[] lines = args.Split(['\r', '\n'], splitOpts);
            foreach (string line in lines)
            {
                if (line.StartsWith("From:", StringComparison.OrdinalIgnoreCase))
                    source = StripQuotes(line["From:".Length..].Trim());
                else if (line.StartsWith("To:", StringComparison.OrdinalIgnoreCase))
                    destination = StripQuotes(line["To:".Length..].Trim());
            }

            if (string.IsNullOrEmpty(source) || string.IsNullOrEmpty(destination))
            {
                error = usage;
                return false;
            }

            error = null;
            action = new AgentAction(AgentActionKind.ClickDrag, Target: source, DragTarget: destination, AltMode: AgentActionAltMode.None);
            return true;
        }
    }

    /// <summary>Parses TYPE_TEXT whose argument is the quoted text to type.</summary>
    private static bool TryParseTextAction(
        string args,
        [NotNullWhen(true)] out AgentAction? action,
        [NotNullWhen(false)] out string? error)
    {
        action = null;
        string text = StripQuotes(args);
        if (text.Length == 0)
        {
            error = "TYPE_TEXT requires text (e.g. TYPE_TEXT \"Hello world\").";
            return false;
        }

        error = null;
        action = new AgentAction(AgentActionKind.TypeText, Text: text);
        return true;
    }

    /// <summary> Parses KEY_COMBO whose argument is a key expression like "enter", "ctrl+s", "ctrl+shift+a". </summary>
    private static bool TryParseKeyComboAction(
        string args,
        [NotNullWhen(true)] out AgentAction? action,
        [NotNullWhen(false)] out string? error)
    {
        action = null;
        string expr = StripQuotes(args).ToLowerInvariant();
        if (expr.Length == 0)
        {
            error = "KEY_COMBO requires a key expression (e.g. KEY_COMBO ctrl+s).";
            return false;
        }

        bool ctrl = false, shift = false, alt = false, win = false;
        string? primaryKey = null;

        string[] parts = expr.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (string part in parts)
        {
            switch (part)
            {
                case "ctrl" or "control":
                    ctrl = true;
                    break;
                case "shift":
                    shift = true;
                    break;
                case "alt":
                    alt = true;
                    break;
                case "win" or "windows" or "super":
                    win = true;
                    break;
                default:
                    // Last non-modifier token is the primary key
                    primaryKey = part;
                    break;
            }
        }

        //if (primaryKey is null)
        //{
        //    error = "KEY_COMBO must include a primary key (e.g. KEY_COMBO ctrl+s). Only modifiers were found.";
        //    return false;
        //}

        ModifierKeys modifiers = ModifierKeys.None;
        if (ctrl)  modifiers |= ModifierKeys.Ctrl;
        if (shift) modifiers |= ModifierKeys.Shift;
        if (alt)   modifiers |= ModifierKeys.Alt;
        if (win)   modifiers |= ModifierKeys.Win;

        error = null;
        action = new AgentAction(AgentActionKind.KeyCombo, Key: primaryKey, Modifiers: modifiers);
        return true;
    }

    /// <summary>Parses SCROLL_UP / SCROLL_DOWN whose argument is an integer amount.</summary>
    private static bool TryParseScrollAction(
        AgentActionKind kind, string args,
        [NotNullWhen(true)] out AgentAction? action,
        [NotNullWhen(false)] out string? error)
    {
        action = null;
        string raw = StripQuotes(args);
        int amount = 1; // default if omitted

        if (raw.Length > 0 && !int.TryParse(raw, out amount))
        {
            error = $"{kind} amount must be an integer (e.g. SCROLL_UP 3).";
            return false;
        }

        amount = Math.Clamp(amount, 1, 10);

        error = null;
        action = new AgentAction(kind, Amount: amount);
        return true;
    }

    /// <summary>Parses WAIT whose argument is a number of seconds.</summary>
    private static bool TryParseWaitAction(
        string args,
        [NotNullWhen(true)] out AgentAction? action,
        [NotNullWhen(false)] out string? error)
    {
        action = null;
        string raw = StripQuotes(args);
        int seconds = 1;

        if (raw.Length > 0 && !int.TryParse(raw, out seconds))
        {
            error = "WAIT requires an integer number of seconds (e.g. WAIT 2).";
            return false;
        }

        seconds = Math.Clamp(seconds, 1, 10);

        error = null;
        action = new AgentAction(AgentActionKind.Wait, Amount: seconds);
        return true;
    }

    /// <summary>
    /// Returns the index of the first occurrence of <paramref name="keyword"/> that starts at the
    /// beginning of a line (after optional whitespace), or -1 if not found.
    /// This prevents matching keywords embedded inside prose (e.g. "previous action:").
    /// </summary>
    private static int IndexOfLineStart(string text, string keyword)
    {
        int searchFrom = 0;
        while (true)
        {
            int idx = text.IndexOf(keyword, searchFrom, StringComparison.OrdinalIgnoreCase);
            if (idx < 0) return -1;

            // Walk back to find the start of this line
            int lineStart = idx;
            while (lineStart > 0 && text[lineStart - 1] != '\n' && text[lineStart - 1] != '\r')
                lineStart--;

            // Only leading whitespace is allowed before the keyword on this line
            bool onlyWhitespaceBefore = true;
            for (int i = lineStart; i < idx; i++)
            {
                if (!char.IsWhiteSpace(text[i]))
                {
                    onlyWhitespaceBefore = false;
                    break;
                }
            }

            if (onlyWhitespaceBefore)
                return idx;

            searchFrom = idx + keyword.Length;
        }
    }

    /// <summary>Strips optional surrounding double-quotes from a string.</summary>
    private static string StripQuotes(string value)
    {
        if (value.Length >= 2 && value[0] == '"' && value[^1] == '"')
            return value[1..^1];
        return value;
    }
}
