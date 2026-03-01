namespace Thio_Universal_Agent.AI_API;

/// <summary>Abstraction for sending prompts to an AI model provider.</summary>
public interface IAiProvider
{
    /// <summary>Sends a text-only prompt and returns the model's response.</summary>
    Task<AiResponse> SendPromptAsync(string prompt, CancellationToken cancellationToken = default);

    /// <summary>Sends a prompt with an attached image and returns the model's response.</summary>
    Task<AiResponse> SendPromptWithImageAsync(string prompt, byte[] imageBytes, string mimeType = "image/jpeg", CancellationToken cancellationToken = default);
}
