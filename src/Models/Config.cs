using Thio_Universal_Agent.AI_API.Gemini;
using Thio_Universal_Agent;

#pragma warning disable IDE0130 // Namespace does not match folder structure
namespace Thio_Universal_Agent;

// ── ConfigField annotation ────────────────────────────────────────────────────

/// <summary>
/// Marks a config property as user-visible. The schema endpoint reflects over these to build
/// the dynamic web UI; any property without this attribute is invisible to the config page.
/// </summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class ConfigFieldAttribute(string label) : Attribute
{
    /// <summary>Human-readable label shown in the UI.</summary>
    public string Label { get; } = label;

    /// <summary>Short explanation shown beneath the field.</summary>
    public string? Description { get; init; }

    /// <summary>When true the input is rendered as a password field with a show/hide toggle.</summary>
    public bool IsPassword { get; init; }

    /// <summary>When true the field is rendered as a full-width prompt-template editor in the UI.</summary>
    public bool IsPromptTemplate { get; init; }
}

// ── General ───────────────────────────────────────────────────────────────────

/// <summary>Application-level settings that apply globally, regardless of AI provider.</summary>
public class GeneralConfig
{
    [ConfigField("Settle Delay (ms)", Description = "Milliseconds to wait after each action before taking the next screenshot")]
    public int SettleDelayMs { get; set; } = 1000;

    [ConfigField("Queue Settle Delay (ms)", Description = "Milliseconds to wait between individual actions inside a QUEUE: batch")]
    public int QueueSettleDelayMs { get; set; } = 50;

    [ConfigField("Enable Context Reset", Description = "Periodically trim conversation history to keep token usage in check")]
    public bool EnableContextReset { get; set; } = true;

    [ConfigField("Strip History Images", Description = "Remove screenshots from older messages to reduce token usage")]
    public bool StripHistoryImages { get; set; } = true;

    [ConfigField("Enable Debug Mode", Description = "Capture verbose debug entries and annotated screenshots; adds significant delay between actions, so disable to reduce per-step overhead")]
    public bool EnableDebugMode { get; set; } = false;

    [ConfigField("Max Queue Size", Description = "Maximum number of actions the AI may queue in a single QUEUE")]
    public int MaxQueueSize { get; set; } = 10;

    [ConfigField("Max Steps", Description = "Maximum number of observe-think-act steps the agent may take before the session is cancelled")]
    public int MaxSteps { get; set; } = 100;

    [ConfigField("Context Reset Interval", Description = "Trim conversation history every N steps when context reset is enabled")]
    public int ContextResetInterval { get; set; } = 20;

    [ConfigField("Max Parse Retries", Description = "Number of times the agent may ask the AI to correct a malformed response before giving up")]
    public int MaxParseRetries { get; set; } = 2;

    [ConfigField("Double-Click Delay (ms)", Description = "Milliseconds between the two clicks of a double-click action")]
    public int DoubleClickDelayMs { get; set; } = 60;

    [ConfigField("System Prompt Template",
        Description = "The full instruction prompt sent to the AI at the start of every session. Use {systemInfo}, {goal}, {maxQueueSize}, and {normalizeSize} as placeholders (including the brackets) — do not rename or remove them.",
        IsPromptTemplate = true)]
    public string? SystemPromptTemplate { get; set; }

    // ── Constructors ──────────────────────────────────────────────────────────

    /// <summary>Creates a <see cref="GeneralConfig"/> with all default values.</summary>
    public GeneralConfig() { }

    /// <summary>Creates a <see cref="GeneralConfig"/> loaded from a <c>General</c> configuration section.</summary>
    public GeneralConfig(IConfigurationSection section)
    {
        if (int.TryParse(section["SettleDelayMs"], out var d) && d > 0)
            SettleDelayMs = d;

        if (int.TryParse(section["QueueSettleDelayMs"], out var qd) && qd >= 0)
            QueueSettleDelayMs = qd;

        if (bool.TryParse(section["EnableContextReset"], out var r)) EnableContextReset = r;
        if (bool.TryParse(section["StripHistoryImages"], out var s)) StripHistoryImages = s;
        if (bool.TryParse(section["EnableDebugMode"], out var dbg)) EnableDebugMode = dbg;
        if (int.TryParse(section["MaxQueueSize"], out var mq) && mq >= 1) MaxQueueSize = mq;
        if (int.TryParse(section["MaxSteps"], out var ms) && ms >= 1) MaxSteps = ms;
        if (int.TryParse(section["ContextResetInterval"], out var cri) && cri >= 1) ContextResetInterval = cri;
        if (int.TryParse(section["MaxParseRetries"], out var mpr) && mpr >= 0) MaxParseRetries = mpr;
        if (int.TryParse(section["DoubleClickDelayMs"], out var dcd) && dcd >= 0) DoubleClickDelayMs = dcd;
        var spt = section["SystemPromptTemplate"];
        if (!string.IsNullOrEmpty(spt)) SystemPromptTemplate = spt;
    }
}

// ── Agent ─────────────────────────────────────────────────────────────────────

/// <summary>Configuration for the agent's coordinate resolution and screen capture.</summary>
public class AgentConfig
{
    [ConfigField("Coordinate Mode", Description = "Algorithm used to locate UI elements on screen")]
    public CoordinateMode CoordinateMode { get; set; } = CoordinateMode.DirectAutoNormalize;

    /// <summary>Zero-based monitor index, or <c>null</c> for all-monitors mode. May be updated at runtime per session.</summary>
    [ConfigField("Monitor Index", Description = "Zero-based index of the monitor to capture; leave empty for all monitors")]
    public int? MonitorIndex { get; set; }

    [ConfigField("Max Zoom Iterations", Description = "Maximum number of grid zoom steps when using Zoom coordinate mode")]
    public int MaxZoomIterations { get; set; } = 10;

    // ── Constructors ──────────────────────────────────────────────────────────

    /// <summary>Creates an <see cref="AgentConfig"/> with all default values.</summary>
    public AgentConfig() { }

    /// <summary>Creates an <see cref="AgentConfig"/> loaded from an <c>Agent</c> configuration section.</summary>
    public AgentConfig(IConfigurationSection section)
    {
        if (Enum.TryParse<CoordinateMode>(section["CoordinateMode"], ignoreCase: true, out var coordMode))
            CoordinateMode = coordMode;

        MonitorIndex = section.GetValue<int?>("MonitorIndex");
        if (int.TryParse(section["MaxZoomIterations"], out var mzi) && mzi >= 1) MaxZoomIterations = mzi;
    }
}

// ── AppConfig (root) ──────────────────────────────────────────────────────────

/// <summary>
/// Central, strongly-typed store for all application configuration.
/// Loaded once from <see cref="IConfiguration"/> at startup and registered as a singleton.
/// Sub-objects (<see cref="Gemini"/>, <see cref="Agent"/>) hold provider- and feature-specific settings.
/// Mutable properties on those sub-objects may be updated at runtime by endpoint handlers.
/// </summary>
public class AppConfig
{
    public GeminiConfig Gemini { get; set; } = new();
    public AgentConfig Agent { get; set; } = new();
    public GeneralConfig General { get; set; } = new();

    // ── Constructors ──────────────────────────────────────────────────────────

    /// <summary>Creates an <see cref="AppConfig"/> with all default values (no settings loaded).</summary>
    public AppConfig() { }

    /// <summary>Creates an <see cref="AppConfig"/> and loads values from <paramref name="configuration"/>.</summary>
    public AppConfig(IConfiguration configuration)
    {
        Gemini  = new GeminiConfig(configuration.GetSection("Gemini"));
        Agent   = new AgentConfig(configuration.GetSection("Agent"));
        General = new GeneralConfig(configuration.GetSection("General"));
    }
}
