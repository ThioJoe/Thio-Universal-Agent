namespace Thio_Universal_Agent.AI_API;

/// <summary>Abstraction for sending prompts to an AI model provider.</summary>
public interface IAiProvider
{
    /// <summary>Sends a text-only prompt and returns the model's response.</summary>
    Task<AiResponse> SendPromptAsync(string prompt, CancellationToken cancellationToken = default, AiRequestOptions? options = null);

    /// <summary>Sends a prompt with an attached image and returns the model's response.</summary>
    Task<AiResponse> SendPromptWithImageAsync(string prompt, byte[] imageBytes, string mimeType = "image/jpeg", CancellationToken cancellationToken = default, AiRequestOptions? options = null);

    /// <summary>Starts a new multi-turn conversation with a text prompt.</summary>
    /// <returns>The conversation context and the model's first response.</returns>
    Task<(AiConversation Conversation, AiResponse Response)> StartConversationAsync(string prompt, CancellationToken cancellationToken = default, AiRequestOptions? options = null);

    /// <summary>Continues a conversation with a text-only follow-up.</summary>
    Task<AiResponse> ContinueConversationAsync(AiConversation conversation, string prompt, CancellationToken cancellationToken = default, AiRequestOptions? options = null);

    /// <summary>Continues a conversation with an image-only follow-up.</summary>
    Task<AiResponse> ContinueConversationAsync(AiConversation conversation, byte[] imageBytes, string mimeType = "image/jpeg", CancellationToken cancellationToken = default, AiRequestOptions? options = null);

    /// <summary>Continues a conversation with both text and an image follow-up.</summary>
    Task<AiResponse> ContinueConversationAsync(AiConversation conversation, string prompt, byte[] imageBytes, string mimeType = "image/jpeg", CancellationToken cancellationToken = default, AiRequestOptions? options = null);
}
