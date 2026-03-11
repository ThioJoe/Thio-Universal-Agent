using System.Diagnostics.CodeAnalysis;

namespace Thio_Universal_Agent.Logic;

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
    private const string ThoughtPrefix = "THOUGHT:";
    private const string ActionPrefix = "ACTION:";

    /// <summary>
    /// Attempts to parse the AI's response text into a thought and action.
    /// </summary>
    /// <returns>True if parsing succeeded; false otherwise.</returns>
    public static bool TryParse(string responseText, [NotNullWhen(true)] out AgentParsedResponse? result, [NotNullWhen(false)] out string? error)
    {
        result = null;
        error = null;

        if (string.IsNullOrWhiteSpace(responseText))
        {
            error = "AI response was empty.";
            return false;
        }

        // Find the ACTION: line — it's the authoritative split point
        int actionIndex = responseText.IndexOf(ActionPrefix, StringComparison.OrdinalIgnoreCase);
        if (actionIndex < 0)
        {
            error = "Response is missing the ACTION: line.";
            return false;
        }

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

    /// <summary>Parses a click/move action whose argument is a quoted target description.</summary>
    private static bool TryParseTargetAction(
        AgentActionKind kind, string args,
        [NotNullWhen(true)] out AgentAction? action,
        [NotNullWhen(false)] out string? error)
    {
        action = null;
        string target = StripQuotes(args);
        if (target.Length == 0)
        {
            error = $"{kind} requires a target description (e.g. {kind.ToString().ToUpperInvariant()} \"the OK button\").";
            return false;
        }

        error = null;
        action = new AgentAction(kind, Target: target);
        return true;
    }

    /// <summary>Parses CLICK_DRAG whose arguments are From: and To: lines with quoted target descriptions.</summary>
    private static bool TryParseClickDragAction(
        string args,
        [NotNullWhen(true)] out AgentAction? action,
        [NotNullWhen(false)] out string? error)
    {
        action = null;
        const string usage = "CLICK_DRAG requires From and To targets (e.g. CLICK_DRAG\nFrom: \"the file icon\"\nTo: \"the trash folder\").";

        string? source = null;
        string? destination = null;

        string[] lines = args.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
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
        action = new AgentAction(AgentActionKind.ClickDrag, Target: source, DragTarget: destination);
        return true;
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

    /// <summary>
    /// Parses KEY_COMBO whose argument is a key expression like "enter", "ctrl+s", "ctrl+shift+a".
    /// </summary>
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

        bool ctrl = false, shift = false, alt = false;
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
                default:
                    // Last non-modifier token is the primary key
                    primaryKey = part;
                    break;
            }
        }

        if (primaryKey is null)
        {
            error = "KEY_COMBO must include a primary key (e.g. KEY_COMBO ctrl+s). Only modifiers were found.";
            return false;
        }

        error = null;
        action = new AgentAction(AgentActionKind.KeyCombo, Key: primaryKey, Ctrl: ctrl, Shift: shift, Alt: alt);
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

    /// <summary>Strips optional surrounding double-quotes from a string.</summary>
    private static string StripQuotes(string value)
    {
        if (value.Length >= 2 && value[0] == '"' && value[^1] == '"')
            return value[1..^1];
        return value;
    }
}
