using Microsoft.Extensions.Configuration;
using Thio_Universal_Agent.AI_API.Gemini;
using Thio_Universal_Agent.Logic;

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
}

// ── Provider config interface ─────────────────────────────────────────────────

/// <summary>
/// Common contract for every AI provider's configuration block.
/// Each concrete implementation (e.g. <see cref="GeminiConfig"/>) adds provider-specific settings.
/// </summary>
public interface IAiProviderConfig
{
    /// <summary>Identifies the provider (e.g. "Gemini", "Anthropic").</summary>
    string ProviderName { get; }

    /// <summary>REST API key. May be updated at runtime via the web UI.</summary>
    string? ApiKey { get; set; }

    /// <summary>Model identifier. May be updated at runtime via the web UI.</summary>
    string Model { get; set; }
}

// ── Gemini ────────────────────────────────────────────────────────────────────

/// <summary>Configuration for the Google Gemini provider.</summary>
public class GeminiConfig : IAiProviderConfig
{
    public string ProviderName => "Gemini";

    /// <inheritdoc/>
    [ConfigField("API Key", IsPassword = true, Description = "Your Google AI Studio API key (aistudio.google.com)")]
    public string? ApiKey { get; set; }

    /// <inheritdoc/>
    [ConfigField("Model", Description = "Gemini model identifier, e.g. gemini-2.0-flash")]
    public string Model { get; set; } = "gemini-flash-latest";

    [ConfigField("Media Resolution", Description = "Resolution hint when sending images to the model")]
    public GeminiMediaResolution MediaResolution { get; set; } = GeminiMediaResolution.High;

    [ConfigField("Temperature", Description = "Sampling temperature — 0 is deterministic, higher values increase randomness")]
    public float? Temperature { get; set; }

    [ConfigField("Top-P", Description = "Nucleus sampling: cumulative probability mass of tokens to consider")]
    public float? TopP { get; set; }

    [ConfigField("Top-K", Description = "Number of highest-probability tokens considered at each step")]
    public int? TopK { get; set; }

    [ConfigField("Max Output Tokens", Description = "Maximum token count for the main model response")]
    public int? MaxOutputTokens { get; set; }

    [ConfigField("Coordinate Max Tokens", Description = "Token limit for coordinate-finding requests (much smaller saves cost)")]
    public int? CoordinateMaxOutputTokens { get; set; }

    [ConfigField("Thinking Budget", Description = "Extended-thinking token budget (Gemini 2.x flash models only)")]
    public int? ThinkingBudget { get; set; }

    [ConfigField("Thinking Level", Description = "Pre-set thinking intensity (Gemini 3.x models)")]
    public GeminiThinkingLevel? ThinkingLevel { get; set; }

    // ── Constructors ──────────────────────────────────────────────────────────

    /// <summary>Creates a <see cref="GeminiConfig"/> with all default values.</summary>
    public GeminiConfig() { }

    /// <summary>Creates a <see cref="GeminiConfig"/> loaded from a <c>Gemini</c> configuration section.</summary>
    public GeminiConfig(IConfigurationSection section)
    {
        ApiKey = section["ApiKey"] is { } k && !string.IsNullOrWhiteSpace(k) ? k : null;
        Model  = section["Model"] ?? Model;

        if (Enum.TryParse<GeminiMediaResolution>(section["MediaResolution"], ignoreCase: true, out var res))
            MediaResolution = res;

        if (float.TryParse(section["Temperature"], out var temp))             Temperature = temp;
        if (float.TryParse(section["TopP"], out var topP))                    TopP = topP;
        if (int.TryParse(section["TopK"], out var topK))                      TopK = topK;
        if (int.TryParse(section["MaxOutputTokens"], out var mOut))           MaxOutputTokens = mOut;
        if (int.TryParse(section["CoordinateMaxOutputTokens"], out var cOut)) CoordinateMaxOutputTokens = cOut;
        if (int.TryParse(section["ThinkingBudget"], out var tb))              ThinkingBudget = tb;
        if (Enum.TryParse<GeminiThinkingLevel>(section["ThinkingLevel"], ignoreCase: true, out var tl))
            ThinkingLevel = tl;
    }
}

// ── Agent ─────────────────────────────────────────────────────────────────────

/// <summary>Configuration for the agent loop and session behaviour.</summary>
public class AgentConfig
{
    [ConfigField("Settle Delay (ms)", Description = "Milliseconds to wait after each action before taking the next screenshot")]
    public int SettleDelayMs { get; set; } = 1500;

    [ConfigField("Coordinate Mode", Description = "Algorithm used to locate UI elements on screen")]
    public CoordinateMode CoordinateMode { get; set; } = CoordinateMode.DirectAutoNormalize;

    /// <summary>Zero-based monitor index, or <c>null</c> for all-monitors mode. May be updated at runtime per session.</summary>
    [ConfigField("Monitor Index", Description = "Zero-based index of the monitor to capture; leave empty for all monitors")]
    public int? MonitorIndex { get; set; }

    [ConfigField("Enable Context Reset", Description = "Periodically trim conversation history to keep token usage in check")]
    public bool EnableContextReset { get; set; } = true;

    [ConfigField("Strip History Images", Description = "Remove screenshots from older messages to reduce token usage")]
    public bool StripHistoryImages { get; set; } = true;

    // ── Constructors ──────────────────────────────────────────────────────────

    /// <summary>Creates an <see cref="AgentConfig"/> with all default values.</summary>
    public AgentConfig() { }

    /// <summary>Creates an <see cref="AgentConfig"/> loaded from an <c>Agent</c> configuration section.</summary>
    public AgentConfig(IConfigurationSection section)
    {
        if (int.TryParse(section["SettleDelayMs"], out var d) && d > 0)
            SettleDelayMs = d;

        if (Enum.TryParse<CoordinateMode>(section["CoordinateMode"], ignoreCase: true, out var coordMode))
            CoordinateMode = coordMode;

        MonitorIndex = section.GetValue<int?>("MonitorIndex");

        if (bool.TryParse(section["EnableContextReset"], out var r)) EnableContextReset = r;
        if (bool.TryParse(section["StripHistoryImages"], out var s)) StripHistoryImages = s;
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
    public AgentConfig  Agent  { get; set; } = new();

    // ── Constructors ──────────────────────────────────────────────────────────

    /// <summary>Creates an <see cref="AppConfig"/> with all default values (no settings loaded).</summary>
    public AppConfig() { }

    /// <summary>Creates an <see cref="AppConfig"/> and loads values from <paramref name="configuration"/>.</summary>
    public AppConfig(IConfiguration configuration)
    {
        Gemini = new GeminiConfig(configuration.GetSection("Gemini"));
        Agent  = new AgentConfig(configuration.GetSection("Agent"));
    }
}
