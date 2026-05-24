using System.Globalization;
using System.Reflection;
using Thio_Universal_Agent.AI_API.Gemini;
using Thio_Universal_Agent.AI_API.OpenAI;
using Thio_Universal_Agent.AI_API.Anthropic;
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

/// <summary>
/// Populates all public writable properties on <paramref name="target"/> from a matching
/// <see cref="IConfigurationSection"/> key, using the property name as the key.
/// Handles <see langword="string"/>, <see langword="bool"/>, <see langword="int"/>,
/// <see langword="float"/>, <see langword="double"/>, enums, and their nullable equivalents.
/// </summary>
internal static class ConfigSectionBinder
{
    internal static void Bind(object target, IConfigurationSection section)
    {
        foreach (var prop in target.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (!prop.CanWrite) continue;

            var raw = section[prop.Name];
            if (raw is null) continue;

            var underlying = Nullable.GetUnderlyingType(prop.PropertyType) ?? prop.PropertyType;

            try
            {
                if (underlying == typeof(string))
                    prop.SetValue(target, string.IsNullOrWhiteSpace(raw) ? null : raw);
                else if (underlying == typeof(bool))
                { if (bool.TryParse(raw, out var v)) prop.SetValue(target, v); }
                else if (underlying == typeof(int))
                { if (int.TryParse(raw, out var v)) prop.SetValue(target, v); }
                else if (underlying == typeof(float))
                { if (float.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var v)) prop.SetValue(target, v); }
                else if (underlying == typeof(double))
                { if (double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var v)) prop.SetValue(target, v); }
                else if (underlying.IsEnum)
                { if (Enum.TryParse(underlying, raw, ignoreCase: true, out var v)) prop.SetValue(target, v); }
            }
            catch { /* skip individual invalid values */ }
        }
    }
}

public enum AiProviderType
{
    Gemini,
    ChatGPT,
    Claude
}

// ── General ───────────────────────────────────────────────────────────────────

/// <summary>Application-level settings that apply globally, regardless of AI provider.</summary>
public class GeneralConfig
{
    [ConfigField("Active AI Provider", Description = "Which AI API provider to use for the agent")]
    public AiProviderType ActiveProvider { get; set; } = AiProviderType.Gemini;

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

    [ConfigField("Draw Grid Overlay on Screenshots", Description = "Overlay a grid onto screenshots before sending to the AI. May help with accuracy for some models, others not.")]
    public bool AddGridOverlay { get; set; } = true;

    [ConfigField("System Prompt Template",
        Description = "The full instruction prompt sent to the AI at the start of every session. Use {systemInfo}, {goal}, {maxQueueSize}, and {normalizeSize} as placeholders (including the brackets) — do not rename or remove them.",
        IsPromptTemplate = true)]
    public string? SystemPromptTemplate { get; set; }

    // ── Constructors ──────────────────────────────────────────────────────────

    /// <summary>Creates a <see cref="GeneralConfig"/> with all default values.</summary>
    public GeneralConfig() { }

    /// <summary>Creates a <see cref="GeneralConfig"/> loaded from a <c>General</c> configuration section.</summary>
    public GeneralConfig(IConfigurationSection section) => ConfigSectionBinder.Bind(this, section);
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
    public AgentConfig(IConfigurationSection section) => ConfigSectionBinder.Bind(this, section);
}

// ── Hotkeys ───────────────────────────────────────────────────────────────────

/// <summary>Configuration for system-wide hotkeys that control the running agent.</summary>
public class HotkeyConfig
{
    [ConfigField("Enable Global Hotkeys", Description = "Register system-wide hotkeys so you can control the agent even when the browser window is not focused")]
    public bool Enabled { get; set; } = true;

    [ConfigField("Pause / Resume", Description = "Hotkey to pause / resume the running session. Format: modifier(s)+key, e.g. Ctrl+Shift+P. Supported modifiers: Ctrl, Shift, Alt, Win. Supported keys: A-Z, 0-9, F1-F12, Escape")]
    public string PauseResumeHotkey { get; set; } = "Ctrl+Shift+Alt+P";

    [ConfigField("Stop", Description = "Hotkey to immediately cancel the running session. Format: modifier(s)+key, e.g. Ctrl+Shift+S")]
    public string StopHotkey { get; set; } = "Ctrl+Shift+Alt+S";

    // ── Constructors ──────────────────────────────────────────────────────────

    /// <summary>Creates a <see cref="HotkeyConfig"/> with all default values.</summary>
    public HotkeyConfig() { }

    /// <summary>Creates a <see cref="HotkeyConfig"/> loaded from a <c>Hotkeys</c> configuration section.</summary>
    public HotkeyConfig(IConfigurationSection section) => ConfigSectionBinder.Bind(this, section);
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
    public OpenAIConfig OpenAI { get; set; } = new();
    public AnthropicConfig Anthropic { get; set; } = new();
    public AgentConfig Agent { get; set; } = new();
    public GeneralConfig General { get; set; } = new();
    public HotkeyConfig Hotkeys { get; set; } = new();

    // ── Constructors ──────────────────────────────────────────────────────────

    /// <summary>Creates an <see cref="AppConfig"/> with all default values (no settings loaded).</summary>
    public AppConfig() { }

    /// <summary>Creates an <see cref="AppConfig"/> and loads values from <paramref name="configuration"/>.</summary>
    public AppConfig(IConfiguration configuration)
    {
        Gemini = new GeminiConfig(configuration.GetSection("Gemini"));
        OpenAI = new OpenAIConfig(configuration.GetSection("OpenAI"));
        Anthropic = new AnthropicConfig(configuration.GetSection("Anthropic"));
        Agent = new AgentConfig(configuration.GetSection("Agent"));
        General = new GeneralConfig(configuration.GetSection("General"));
        Hotkeys = new HotkeyConfig(configuration.GetSection("Hotkeys"));
    }
}