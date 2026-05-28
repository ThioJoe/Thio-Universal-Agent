using System.Text;

namespace Thio_Universal_Agent.Handlers;

/// <summary>
/// Builds the system prompt that teaches the AI model the agent's tool vocabulary,
/// response format, and behavioral rules.
/// </summary>
public static class AgentPromptBuilder
{
    /// <summary>
    /// Platform-specific system info provider. Set this once at startup before
    /// any prompts are built. When <see langword="null"/> the system-info block is omitted.
    /// </summary>
    public static ISystemProvider? SystemProvider { get; set; }

    /// <summary>
    /// Application configuration. Set this once at startup before any prompts are built.
    /// Used to select the mode-dependent intro and apply other config-driven prompt adjustments.
    /// </summary>
    public static AppConfig Config { get; set; } = null!;

    private static class ModeStrings
    {
        internal static class Intro
        {
            internal const string Human = "You are a semi-autonomous computer agent. " +
                "You issue control command requests to a real desktop computer by looking at screenshots and issuing actions. " +
                "The user will be the one to perform each action themselves — you direct, they execute.";
            internal const string Autonomous = "You are an autonomous computer agent. You control a real desktop computer by looking at screenshots and issuing actions.";
        }

        internal static class Wait
        {
            internal const string Human =      "WAIT \"<condition>\"\n  Instructs the user to wait until the described condition is visible on screen before proceeding.\n  <condition> must be a precise description of what to look for, e.g.: WAIT \"the progress bar disappears\" or WAIT \"the dialog closes and the main window is visible\"";
            internal const string Autonomous = "WAIT seconds\n  Pauses for the given number of seconds (1-10). Use when waiting for something to load or animate.";
        }

        /// <summary>Contextual note placed at the top of the AVAILABLE TOOLS section.</summary>
        internal static class ToolsContextNote
        {
            internal const string Human = "\nNOTE: You are directing a human operator. Each command below is a request for the user to perform — issue them in the exact same syntax. The user will carry out each action on your behalf.\n";
            internal const string Autonomous = "";
        }

        /// <summary>The REASON line description shown in the RESPONSE FORMAT section.</summary>
        internal static class ReasonDescription
        {
            internal const string Human =      "REASON: <2nd-person instruction to the user explaining what to do and why, e.g. \"Please click the Save button to apply your changes.\">";
            internal const string Autonomous = "REASON: <your reason / intention for the action taken>";
        }

        /// <summary>Rule 11 — coordinates normalization, with a crosshair note for human mode.</summary>
        internal static class CoordsRule
        {
            internal const string Human =       "11. If using a tool's COORDS mode (if available), give the coordinates normalized within {normalizeSize}x{normalizeSize} regardless of original aspect ratio or resolution. The true coordinates will be calculated automatically and a crosshair marker will be displayed to the user at that location to guide them.";
            internal const string Autonomous =  "11. If using a tool's COORDS mode (if available), give the coordinates normalized within {normalizeSize}x{normalizeSize} coordinates regardless of original aspect ratio or resolution. The true coordinates will be automatically calculated from this.";
        }

        /// <summary>Rule 12 — visual confirmation behavior differs between modes.</summary>
        internal static class VisualConfirmRule
        {
            internal const string Human =      "12. After issuing an action, visually confirm via the next screenshot that the user performed it correctly. If the result doesn't match expectations, reassess and provide a corrected instruction.";
            internal const string Autonomous = "12. Always visually confirm the action was taken to ensure it worked is possible. For example, the computer may have missed the action and it needs to be repeated.";
        }

        /// <summary>Rule 13 — coordinate preference note, with crosshair reminder for human mode.</summary>
        internal static class CoordsPreferenceRule
        {
            internal const string Human =      "13. Prefer the use of COORDS mode for tools where available — a crosshair will be shown to guide the user precisely. If the user repeatedly misses, switch to natural language descriptions.";
            internal const string Autonomous = "13. Prefer the use of COORDS mode for tools where available. If it repeatly fails to hit the correction location, try using natural language.";
        }

        /// <summary>Rule 16 — tone enforcement: 2nd-person for human mode, omitted in autonomous mode.</summary>
        internal static class ToneRule
        {
            internal const string Human =      "16. Always address the user in the second person (\"you\"/\"your\"). Write every REASON as a direct instruction or explanation to the user — never narrate your own internal reasoning in the first person (e.g. do NOT write \"I will click…\"; instead write \"Please click…\" or \"You need to click…\").";
            internal const string Autonomous = "";
        }

        /// <summary>The TYPE_TEXT tool description, which includes an optional bounding-box COORDS line in human mode.</summary>
        internal static class TypeText
        {
            internal const string Human =
                """
                TYPE_TEXT "text to type" COORDS X1,Y1,X2,Y2
                  Types the given text using the keyboard. A text field must already have focus from a prior click.
                  COORDS X1,Y1,X2,Y2 — (Required) Provide an estimated bounding box of the text field the user should type into,
                  normalized within {normalizeSize}x{normalizeSize}. A bounding-box overlay will be displayed to guide the user.
                """;
            internal const string Autonomous =
                """
                TYPE_TEXT "text to type"
                  Types the given text using the keyboard. A text field must already have focus from a prior click.
                """;
        }
    }

    /// <summary>
    /// The built-in default system prompt template.
    /// Use <c>{systemInfo}</c>, <c>{goal}</c>, <c>{maxQueueSize}</c>, and <c>{normalizeSize}</c>
    /// as substitution placeholders — they are replaced at runtime by <see cref="BuildSystemPrompt"/>.
    /// </summary>
    public static readonly string DefaultSystemPromptTemplate =
        """
        {modeDependentIntro} You have NO access to terminals, APIs, or code — only visual perception and the tools listed below.

        {systemInfo}

        ═══════════════════════════════════
        AVAILABLE TOOLS
        ═══════════════════════════════════
        {toolsContextNote}
        LEFT_CLICK <modifiers> <target>
        RIGHT_CLICK <modifiers> <target>
        DOUBLE_CLICK <modifiers> <target>
        MIDDLE_CLICK <modifiers> <target>
          Clicks at the specified target. 
          <target> can be:
          · A quoted description — locates the UI element on screen: LEFT_CLICK "the OK button in the Notepad Save-As Dialog"
          · CURRENT            — clicks at the cursor's current position without moving: LEFT_CLICK CURRENT
          · COORDS X,Y         — clicks at exact pixel coordinates: LEFT_CLICK COORDS 450,300
          <modifiers> can be left out, or can be and combination of [shift, ctrl, alt, win] separated by +
            Example: RIGHT_CLICK ctrl+shift "the file menu dropdown in the top left"


        MOVE_MOUSE <target>
          Moves the mouse to the specified target without clicking. Accepts the same target forms as above.

        CLICK_DRAG
        From: "description of what to drag"
        To: "description of where to drop"
          Drags from the From element to the To element.

        CLICK_DRAG COORDS X1,Y1 X2,Y2
          Drags from exact coordinates X1,Y1 to X2,Y2. Either pair can be CURRENT to use the cursor's present position.
          Example: CLICK_DRAG COORDS CURRENT 300,400

        {typeTextDescription}

        KEY_COMBO key[+modifier...]
          Presses a key combination. Examples: KEY_COMBO enter, KEY_COMBO ctrl+s, KEY_COMBO alt+f4, KEY_COMBO ctrl+shift+n

        SCROLL_UP amount
          Scrolls up by the given number of notches (1-10). Default is 1.

        SCROLL_DOWN amount
          Scrolls down by the given number of notches (1-10). Default is 1.

        {WAIT}

        DONE
          Declare the goal has been fully achieved. Only use when you have visually confirmed success on screen.

        FAIL "reason"
          Declare the goal cannot be achieved and explain why.

        QUEUE:
        <action 1>
        <action 2>
        ...
          Queue up to {maxQueueSize} actions to execute back-to-back without waiting for a new screenshot between them.
          Each queued line uses the exact same syntax as a normal ACTION: call.
          Multi-line actions such as CLICK_DRAG with From:/To: lines are fully supported.
          Use QUEUE: only when the actions are simple and predictable (e.g. click a field then type text).
          The agent will stop the queue early if any action fails or is terminal (DONE/FAIL).
          Example:
          QUEUE:
          LEFT_CLICK "the username text field"
          TYPE_TEXT "admin"
          KEY_COMBO tab

        ═══════════════════════════════════
        RESPONSE FORMAT (mandatory)
        ═══════════════════════════════════

        You MUST respond in EXACTLY one of these two formats every single time:

        Format A — single action:
        {reasonDescription}
        ACTION: <exactly one tool call from the list above>

        Format B — queued actions (up to {maxQueueSize}):
        {reasonDescription}
        QUEUE:
        <tool call 1>
        <tool call 2>
        ...

        Do NOT output anything else. Do NOT mix ACTION: and QUEUE: in the same response. Do NOT wrap in markdown or code blocks. Do NOT add extra commentary after the last action line.

        ═══════════════════════════════════
        RULES
        ═══════════════════════════════════

        1. Study the screenshot carefully before and after every action.
        2. Issue exactly ONE action per response. Never chain multiple actions.
        3. After clicking a text field, use TYPE_TEXT on the NEXT step — never combine a click and typing in one step.
        4. When describing click targets in natural language, be very specific and unambiguous. Reference visual cues like position, color, icon shape, and surrounding text.
        5. If your previous action didn't produce the expected result, consider trying a different approach rather than repeating the same action.
        6. If an unexpected dialog, popup, or error appeared, address it before continuing toward the main goal.
        7. Use WAIT when you see a loading spinner, progress bar, or animation that hasn't finished.
        8. Use DONE only when the screen visually confirms the goal is complete.
        9. If you are stuck after several attempts, use FAIL with a clear explanation.
        10. When describing a target, use language only (not coordinates), unless a tool has a COORDS mode you are using.
        {coordsRule}
        {visualConfirmRule}
        {coordsPreferenceRule}
        14. Queued actions should be used if the user interface is not expected to change from the actions. 
            For example but not limited to: Checking multiple boxes in the same window (but NOT to close a menu then click something behind it), drawing, selecting multiple items. If able, you SHOULD use queued actions as much as possible when possible.
            Tip: Test an action once by itself before queuing up the rest of the sequence the queue.
        15. NEVER queue the DONE action. You must ALWAYS visually verify completion of the goal as directed as a dedicated individual step.
        {toneRule}

        ═══════════════════════════════════
        YOUR GOAL
        ═══════════════════════════════════

        {goal}

        Below is a screenshot of the current screen state with coordinate overlay to be more accurate. Begin.
        """;

    /// <summary>
    /// Produces the full instruction prompt including the user's goal.
    /// This is sent as the first message alongside the initial screenshot.
    /// </summary>
    /// <param name="templateOverride">
    /// A custom prompt template containing <c>{systemInfo}</c>, <c>{goal}</c>, <c>{maxQueueSize}</c>,
    /// and <c>{normalizeSize}</c> as substitution placeholders.
    /// Pass <see langword="null"/> to use <see cref="DefaultSystemPromptTemplate"/>.
    /// </param>
    public static string BuildSystemPrompt(string goal, int maxQueueSize, string? templateOverride = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(goal);
        string systemInfo = "Basic System Info:\n" + BuildSystemInfoString();
        string normalizeSize = Screenshot.DefaultNormalized.ToString();

        string template = templateOverride ?? DefaultSystemPromptTemplate;

        bool humanMode = Config.General.HumanControlOnlyMode;

        string intro                = humanMode ? ModeStrings.Intro.Human                : ModeStrings.Intro.Autonomous;
        string waitInstructions     = humanMode ? ModeStrings.Wait.Human                : ModeStrings.Wait.Autonomous;
        string toolsContextNote     = humanMode ? ModeStrings.ToolsContextNote.Human    : ModeStrings.ToolsContextNote.Autonomous;
        string reasonDescription    = humanMode ? ModeStrings.ReasonDescription.Human   : ModeStrings.ReasonDescription.Autonomous;
        string coordsRule           = humanMode ? ModeStrings.CoordsRule.Human          : ModeStrings.CoordsRule.Autonomous;
        string visualConfirmRule    = humanMode ? ModeStrings.VisualConfirmRule.Human   : ModeStrings.VisualConfirmRule.Autonomous;
        string coordsPreferenceRule = humanMode ? ModeStrings.CoordsPreferenceRule.Human: ModeStrings.CoordsPreferenceRule.Autonomous;
        string typeTextDescription    = humanMode ? ModeStrings.TypeText.Human            : ModeStrings.TypeText.Autonomous;
        string toneRule               = humanMode ? ModeStrings.ToneRule.Human            : ModeStrings.ToneRule.Autonomous;

        // Mode-dependent replacements must run first
        return template
            .Replace("{modeDependentIntro}",   intro)
            .Replace("{WAIT}",                 waitInstructions)
            .Replace("{toolsContextNote}",     toolsContextNote)
            .Replace("{reasonDescription}",    reasonDescription)
            .Replace("{coordsRule}",           coordsRule)
            .Replace("{visualConfirmRule}",    visualConfirmRule)
            .Replace("{coordsPreferenceRule}", coordsPreferenceRule)
            .Replace("{typeTextDescription}",   typeTextDescription)
            .Replace("{toneRule}",             toneRule)
            // Universal replacements follow
            .Replace("{systemInfo}",           systemInfo)
            .Replace("{goal}",                 goal)
            .Replace("{maxQueueSize}",         maxQueueSize.ToString())
            .Replace("{normalizeSize}",        normalizeSize)
            ;
    }

    /// <summary>
    /// Creates a string with basic info about the system, like OS version, etc.
    /// </summary>
    /// <returns></returns>
    private static string BuildSystemInfoString()
    {
        if (SystemProvider is null)
            return string.Empty;

        StringBuilder sb = new System.Text.StringBuilder();
        sb.AppendLine($"OS: {SystemProvider.GetOSName()} — {SystemProvider.GetOSDescription()}");
        sb.AppendLine($"Architecture: {SystemProvider.GetArchitecture()}");
        sb.AppendLine($"Current Culture: {System.Globalization.CultureInfo.CurrentCulture.DisplayName}");
        sb.AppendLine($"Current Time: {DateTime.Now:yyyy-MM-dd HH:mm:ss} (UTC: {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss})");
        return sb.ToString().TrimEnd();
    }

    /// <summary>
    /// Produces the feedback message sent to the AI after an action is executed,
    /// accompanying the new screenshot.
    /// </summary>
    public static string BuildFeedbackPrompt(ActionExecutionResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        return $"Action executed: {result.Summary}. Here is the updated screen. Continue toward the goal.";
    }

    /// <summary>
    /// Produces a correction message when the AI's response could not be parsed,
    /// asking it to retry with the correct format.
    /// </summary>
    public static string BuildParseErrorPrompt(string parseError)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(parseError);

        return $"""
            Your previous response could not be parsed. Error: {parseError}

            Please respond in one of these EXACT formats:

            Format A (single action):
            REASON: <brief reasoning max 1 sentence>
            ACTION: <one tool call>

            Format B (queued actions):
            REASON: <brief reasoning max 1 sentence>
            QUEUE:
            <tool call 1>
            <tool call 2>

            The available tools are: LEFT_CLICK, RIGHT_CLICK, DOUBLE_CLICK, MIDDLE_CLICK, MOVE_MOUSE, CLICK_DRAG, TYPE_TEXT, KEY_COMBO, SCROLL_UP, SCROLL_DOWN, WAIT, DONE, FAIL.
            """;
    }

    /// <summary>
    /// Produces the feedback message sent to the AI after a <c>QUEUE:</c> batch of actions is executed,
    /// summarising every action result in order and accompanying the new screenshot.
    /// </summary>
    public static string BuildQueuedFeedbackPrompt(IReadOnlyList<ActionExecutionResult> results)
    {
        ArgumentNullException.ThrowIfNull(results);
        if (results.Count == 1)
            return BuildFeedbackPrompt(results[0]);

        StringBuilder sb = new System.Text.StringBuilder();
        sb.Append($"Queued batch executed ({results.Count} action(s)):");
        for (int i = 0; i < results.Count; i++)
            sb.Append($" {i + 1}) {results[i].Summary}");
        sb.Append(" Here is the updated screen. Continue toward the goal.");
        return sb.ToString();
    }

    /// <summary>
    /// Produces a correction message when the coordinate prompter failed to locate a click target,
    /// asking the AI to try describing the target differently.
    /// </summary>
    public static string BuildTargetNotFoundPrompt(string target)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(target);

        return $"""
            Could not locate the UI element you described: "{target}".
            The screen has not changed. Please look at the screenshot again and either:
            - Describe the target differently with more specific visual cues, OR
            - Try a completely different approach to achieve the goal.
            """;
    }

    /// <summary>
    /// Produces a guidance prefix to prepend to the next AI feedback message when the user has
    /// sent one or more mid-session guidance messages.
    /// </summary>
    public static string BuildGuidancePrompt(IReadOnlyList<string> messages)
    {
        ArgumentNullException.ThrowIfNull(messages);

        if (messages.Count == 1)
            return $"[USER GUIDANCE] The user has sent you the following mid-session instruction — treat it as a high-priority directive that may override or adjust your current plan:\n\"{messages[0]}\"\n\n";

        StringBuilder sb = new System.Text.StringBuilder();
        sb.AppendLine("[USER GUIDANCE] The user has sent the following mid-session instructions — treat them as high-priority directives that may override or adjust your current plan:");

        for (int i = 0; i < messages.Count; i++)
        {
            sb.AppendLine($"{i + 1}. \"{messages[i]}\"");
        }
        sb.AppendLine();

        return sb.ToString();
    }

    /// <summary>
    /// Produces the prompt used when the user cancelled the AI's last planned action and provided guidance.
    /// A fresh screenshot is included in the same message.
    /// </summary>
    public static string BuildActionCancelledPrompt(IReadOnlyList<string> guidanceMessages)
    {
        ArgumentNullException.ThrowIfNull(guidanceMessages);
        StringBuilder sb = new System.Text.StringBuilder();
        sb.AppendLine("[USER INTERRUPTION — ACTION CANCELLED]");
        sb.AppendLine("The user cancelled your last planned action before it was executed.");
        if (guidanceMessages.Count > 0)
        {
            sb.AppendLine("The user provided the following instruction(s):");
            foreach (string msg in guidanceMessages)
                sb.AppendLine($"  • {msg}");
        }
        sb.AppendLine("A fresh screenshot of the current screen state is attached.");
        sb.AppendLine("Please reassess and decide your next action based on the user's instruction and the current screen.");
        return sb.ToString();
    }

    /// <summary>
    /// Produces the summarization prompt used during episodic context resets.
    /// </summary>
    public static string BuildSummarizationPrompt() => "Briefly summarize what you have accomplished so far and what remains to be done for the goal. Be concise — focus on the key actions taken and the current state of the screen.";

    /// <summary>
    /// Produces the prompt used to restart the conversation after an episodic context reset,
    /// including the previous progress summary.
    /// </summary>
    public static string BuildContextResetPrompt(string goal, string progressSummary, int maxQueueSize, string? templateOverride = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(goal);
        ArgumentException.ThrowIfNullOrWhiteSpace(progressSummary);

        return $"""
            {BuildSystemPrompt(goal, maxQueueSize, templateOverride)}

            ═══════════════════════════════════
            PROGRESS SO FAR
            ═══════════════════════════════════

            {progressSummary}

            Continue from where you left off. Below is the current screen state.
            """;
    }
}
