namespace Thio_Universal_Agent.AI_API.Gemini;

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

    [ConfigField("Coordinate Max Tokens", Description = "Token limit for coordinate-finding requests (much smaller saves cost). Leave blank to use the API default, which varies by model.")]
    public int? CoordinateMaxOutputTokens { get; set; }

    [ConfigField("Thinking Budget", Description = "Extended-thinking token budget (Gemini 2.x flash models only)")]
    public int? ThinkingBudget { get; set; }

    [ConfigField("Thinking Level", Description = "Pre-set thinking intensity (Gemini 3.x models)")]
    public GeminiThinkingLevel? ThinkingLevel { get; set; }

    // ── Constructors ──────────────────────────────────────────────────────────

    /// <summary>Creates a <see cref="GeminiConfig"/> with all default values.</summary>
    public GeminiConfig() { }

    /// <summary>Creates a <see cref="GeminiConfig"/> loaded from a <c>Gemini</c> configuration section.</summary>
    public GeminiConfig(IConfigurationSection section) => ConfigSectionBinder.Bind(this, section);
}
