// src/AI_API/OpenAI/OpenAIConfig.cs
namespace Thio_Universal_Agent.AI_API.OpenAI;

/// <summary>Configuration for the OpenAI API provider.</summary>
public class OpenAIConfig : IAiProviderConfig
{
    public string ProviderName => "OpenAI";

    [ConfigField("API Key", IsPassword = true, Description = "Your OpenAI API key")]
    public string? ApiKey { get; set; }

    [ConfigField("Model", Description = "OpenAI model identifier, e.g. gpt-5.5")]
    public string Model { get; set; } = "gpt-5.5";

    [ConfigField("Temperature", Description = "Sampling temperature (0-2)")]
    public float? Temperature { get; set; }

    [ConfigField("Max Output Tokens", Description = "Maximum token count for the main model response")]
    public int? MaxOutputTokens { get; set; }

    public OpenAIConfig() { }

    public OpenAIConfig(IConfigurationSection section) => ConfigSectionBinder.Bind(this, section);
}