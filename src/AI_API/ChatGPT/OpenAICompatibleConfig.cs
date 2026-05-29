namespace Thio_Universal_Agent.AI_API.OpenAI;

/// <summary>Configuration for an OpenAI-compatible chat completions provider.</summary>
public sealed class OpenAICompatibleConfig : IAiProviderConfig
{
    public string ProviderName => "OpenAI-Compatible";

    [ConfigField("API Key", IsPassword = true, Description = "API key for the OpenAI-compatible service")]
    public string? ApiKey { get; set; }

    [ConfigField("Model", Description = "Model identifier exposed by the OpenAI-compatible service")]
    public string Model { get; set; } = "gpt-4.1-mini";

    [ConfigField("Endpoint URL", Description = "Full chat completions endpoint URL, e.g. http://localhost:1234/v1/chat/completions")]
    public string EndpointUrl { get; set; } = "https://api.openai.com/v1/chat/completions";

    [ConfigField("Temperature", Description = "Sampling temperature (0-2)")]
    public float? Temperature { get; set; }

    [ConfigField("Max Output Tokens", Description = "Maximum token count for the main model response")]
    public int? MaxOutputTokens { get; set; }

    [ConfigField("Input Price ($ / 1M tokens)", Description = "Cost per 1 million input (prompt) tokens for the selected model")]
    public double? InputPricePerMillionTokens { get; set; }

    [ConfigField("Output Price ($ / 1M tokens)", Description = "Cost per 1 million output (completion) tokens for the selected model")]
    public double? OutputPricePerMillionTokens { get; set; }

    [ConfigField("Cached Input Price ($ / 1M tokens)", Description = "Cost per 1 million cached input tokens, if the model supports prompt caching (leave blank if unused)")]
    public double? CachedInputPricePerMillionTokens { get; set; }

    public OpenAICompatibleConfig() { }

    public OpenAICompatibleConfig(IConfigurationSection section)
    {
        ConfigSectionBinder.Bind(this, section);
    }
}