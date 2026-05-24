// src/AI_API/Anthropic/AnthropicConfig.cs
namespace Thio_Universal_Agent.AI_API.Anthropic;

/// <summary>Configuration for the Anthropic (Claude) API provider.</summary>
public class AnthropicConfig : IAiProviderConfig
{
    public string ProviderName => "Anthropic";

    [ConfigField("API Key", IsPassword = true, Description = "Your Anthropic API key")]
    public string? ApiKey { get; set; }

    [ConfigField("Model", Description = "Anthropic model identifier, e.g. claude-sonnet-4-6")]
    public string Model { get; set; } = "claude-sonnet-4-6";

    [ConfigField("Temperature", Description = "Sampling temperature (0-1)")]
    public float? Temperature { get; set; }

    [ConfigField("Max Output Tokens", Description = "Anthropic strictly requires max_tokens (defaults to 4096)")]
    public int? MaxOutputTokens { get; set; }

    public AnthropicConfig() { }

    public AnthropicConfig(IConfigurationSection section) => ConfigSectionBinder.Bind(this, section);
}