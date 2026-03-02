using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Thio_Universal_Agent.AI_API;

namespace Thio_Universal_Agent.AI_API.Gemini;

/// <summary>
/// Gemini REST API implementation of <see cref="IAiProvider"/>.
/// Communicates with the Gemini generateContent endpoint using <see cref="HttpClient"/>.
/// </summary>
public sealed class GeminiProvider(HttpClient httpClient, IConfiguration configuration, ILogger<GeminiProvider> logger) : IAiProvider
{
    private const string BaseUrl = "https://generativelanguage.googleapis.com/v1beta/models";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
    private readonly string _apiKey = configuration["Gemini:ApiKey"] ?? throw new InvalidOperationException("Gemini:ApiKey is not configured.");
    private readonly string _model = configuration["Gemini:Model"] ?? "gemini-2.0-flash";

    public Task<AiResponse> SendPromptAsync(string prompt, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prompt);

        var request = new GeminiRequest([new GeminiContent("user", [new GeminiPart(prompt, null)])]);
        return SendRequestAsync(request, cancellationToken);
    }

    public Task<AiResponse> SendPromptWithImageAsync(string prompt, byte[] imageBytes, string mimeType = "image/jpeg", CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prompt);
        ArgumentNullException.ThrowIfNull(imageBytes);

        GeminiRequest request = new(
        [
            new GeminiContent("user",
            [
                new GeminiPart(prompt, null),
                new GeminiPart(null, new GeminiInlineData(mimeType, Convert.ToBase64String(imageBytes)))
            ])
        ]);

        return SendRequestAsync(request, cancellationToken);
    }

    public async Task<(AiConversation Conversation, AiResponse Response)> StartConversationAsync(
        string prompt, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prompt);

        var conversation = new AiConversation();
        var userMessage = new AiChatMessage { Role = AiChatRole.User, Text = prompt };

        var request = BuildRequest(conversation, userMessage);
        var response = await SendRequestAsync(request, cancellationToken).ConfigureAwait(false);

        if (response.Success)
        {
            conversation.AddMessage(userMessage);
            conversation.AddMessage(new AiChatMessage { Role = AiChatRole.Model, Text = response.Text });
        }

        return (conversation, response);
    }

    public Task<AiResponse> ContinueConversationAsync(
        AiConversation conversation, string prompt, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(conversation);
        ArgumentException.ThrowIfNullOrWhiteSpace(prompt);

        var userMessage = new AiChatMessage { Role = AiChatRole.User, Text = prompt };
        return ContinueConversationCoreAsync(conversation, userMessage, cancellationToken);
    }

    public Task<AiResponse> ContinueConversationAsync(
        AiConversation conversation, byte[] imageBytes, string mimeType = "image/jpeg",
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(conversation);
        ArgumentNullException.ThrowIfNull(imageBytes);

        var userMessage = new AiChatMessage { Role = AiChatRole.User, ImageBytes = imageBytes, MimeType = mimeType };
        return ContinueConversationCoreAsync(conversation, userMessage, cancellationToken);
    }

    public Task<AiResponse> ContinueConversationAsync(
        AiConversation conversation, string prompt, byte[] imageBytes, string mimeType = "image/jpeg",
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(conversation);
        ArgumentException.ThrowIfNullOrWhiteSpace(prompt);
        ArgumentNullException.ThrowIfNull(imageBytes);

        var userMessage = new AiChatMessage { Role = AiChatRole.User, Text = prompt, ImageBytes = imageBytes, MimeType = mimeType };
        return ContinueConversationCoreAsync(conversation, userMessage, cancellationToken);
    }

    private async Task<AiResponse> ContinueConversationCoreAsync(
        AiConversation conversation, AiChatMessage userMessage, CancellationToken cancellationToken)
    {
        var request = BuildRequest(conversation, userMessage);
        var response = await SendRequestAsync(request, cancellationToken).ConfigureAwait(false);

        if (response.Success)
        {
            conversation.AddMessage(userMessage);
            conversation.AddMessage(new AiChatMessage { Role = AiChatRole.Model, Text = response.Text });
        }

        return response;
    }

    private async Task<AiResponse> SendRequestAsync(GeminiRequest request, CancellationToken cancellationToken)
    {
        var url = $"{BaseUrl}/{_model}:generateContent?key={_apiKey}";

        if (logger.IsEnabled(LogLevel.Debug))
            logger.LogDebug("Sending prompt to Gemini model {Model}.", _model);

        using var response = await httpClient.PostAsJsonAsync(url, request, JsonOptions, cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            logger.LogError("Gemini API returned {StatusCode}. Body: {ErrorBody}", (int)response.StatusCode, errorBody);
            return new AiResponse(false, string.Empty, $"HTTP {(int)response.StatusCode}: {errorBody}");
        }

        var geminiResponse = await response.Content
            .ReadFromJsonAsync<GeminiResponse>(JsonOptions, cancellationToken)
            .ConfigureAwait(false);

        if (geminiResponse?.Candidates is not { Count: > 0 })
        {
            var blockReason = geminiResponse?.PromptFeedback?.BlockReason ?? "No candidates returned.";
            if (logger.IsEnabled(LogLevel.Warning))
                logger.LogWarning("Gemini returned no candidates. Reason: {BlockReason}", blockReason);
            return new AiResponse(false, string.Empty, $"Blocked: {blockReason}");
        }

        var text = geminiResponse.Candidates[0].Content.Parts
            .Where(p => p.Text is not null)
            .Select(p => p.Text!)
            .FirstOrDefault() ?? string.Empty;

        if (logger.IsEnabled(LogLevel.Debug))
            logger.LogDebug("Received response from Gemini model {Model}.", _model);
        return new AiResponse(true, text);
    }

    private static GeminiRequest BuildRequest(AiConversation conversation, AiChatMessage additionalMessage)
    {
        var contents = new List<GeminiContent>(conversation.Messages.Count + 1);

        foreach (var message in conversation.Messages)
            contents.Add(ToGeminiContent(message));

        contents.Add(ToGeminiContent(additionalMessage));
        return new GeminiRequest(contents);
    }

    private static GeminiContent ToGeminiContent(AiChatMessage message)
    {
        var role = message.Role == AiChatRole.User ? "user" : "model";
        var parts = new List<GeminiPart>();

        if (message.Text is not null)
            parts.Add(new GeminiPart(message.Text, null));

        if (message.ImageBytes is not null)
            parts.Add(new GeminiPart(null, new GeminiInlineData(message.MimeType ?? "image/jpeg", Convert.ToBase64String(message.ImageBytes))));

        return new GeminiContent(role, parts);
    }

    // --- Private request DTOs ---

    private record GeminiRequest(List<GeminiContent> Contents);
    private record GeminiContent(string Role, List<GeminiPart> Parts);
    private record GeminiPart(string? Text, GeminiInlineData? InlineData);
    private record GeminiInlineData(string MimeType, string Data);

    // --- Private response DTOs ---

    private record GeminiResponse(List<GeminiCandidate>? Candidates, GeminiPromptFeedback? PromptFeedback);
    private record GeminiCandidate(GeminiContent Content, string FinishReason);
    private record GeminiPromptFeedback(string? BlockReason);
}
