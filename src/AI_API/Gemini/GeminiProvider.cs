// GeminiProvider.cs
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Thio_Universal_Agent.AI_API.Gemini;

public enum GeminiMediaResolution
{
    Unspecified,
    Low,
    Medium,
    High
}

public enum GeminiThinkingLevel
{
    minimal,
    low,
    medium,
    high
}

/// <summary>
/// Gemini REST API implementation of <see cref="IAiProvider"/>.
/// Communicates with the Gemini generateContent endpoint using <see cref="HttpClient"/>.
/// </summary>
public sealed class GeminiProvider(HttpClient httpClient, AppConfig appConfig, ILogger<GeminiProvider> logger) : IAiProvider
{
    private const string BaseUrl = "https://generativelanguage.googleapis.com/v1beta/models";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
    // Not validated here — the key may be absent at construction time and supplied later
    // via the web UI (AppConfig.Gemini.ApiKey is set by the /api/agent/start endpoint).
    // Validation is deferred to SendRequestAsync so that constructing this type never throws.
    private readonly string? _apiKey = appConfig.Gemini.ApiKey;
    private readonly string _model = appConfig.Gemini.Model;
    private readonly GeminiGenerationConfig? _generationConfig = BuildGenerationConfig(appConfig.Gemini);

    private static GeminiGenerationConfig? BuildGenerationConfig(GeminiConfig gemini)
    {
        string? resString = gemini.MediaResolution is not GeminiMediaResolution.Unspecified
            ? gemini.MediaResolution switch
            {
                GeminiMediaResolution.Low    => "MEDIA_RESOLUTION_LOW",
                GeminiMediaResolution.Medium => "MEDIA_RESOLUTION_MEDIUM",
                GeminiMediaResolution.High   => "MEDIA_RESOLUTION_HIGH",
                _                            => null
            }
            : null;

        float? temp      = gemini.Temperature;
        float? topP      = gemini.TopP;
        int?   topK      = gemini.TopK;
        int?   maxTokens = gemini.MaxOutputTokens;

        GeminiThinkingConfig? thinkingConfig = null;

        if (gemini.Model.Contains("gemini-3", StringComparison.OrdinalIgnoreCase))
        {
            if (gemini.ThinkingLevel is { } level)
                thinkingConfig = new GeminiThinkingConfig(null, level.ToString());
        }
        else
        {
            if (gemini.ThinkingBudget is { } tb)
                thinkingConfig = new GeminiThinkingConfig(tb, null);
        }

        if (resString is null && temp is null && topP is null && topK is null && maxTokens is null && thinkingConfig is null)
            return null;

        return new GeminiGenerationConfig(resString, temp, topP, topK, maxTokens, thinkingConfig);
    }

    public Task<AiResponse> SendPromptAsync(string prompt, CancellationToken cancellationToken = default, AiRequestOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prompt);

        var request = new GeminiRequest([new GeminiContent("user", [new GeminiPart(prompt, null)])], _generationConfig);
        return SendRequestAsync(request, cancellationToken, options);
    }

    public Task<AiResponse> SendPromptWithImageAsync(string prompt, byte[] imageBytes, string mimeType = "image/jpeg", CancellationToken cancellationToken = default, AiRequestOptions? options = null)
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
        ], _generationConfig);

        return SendRequestAsync(request, cancellationToken, options);
    }

    public async Task<(AiConversation Conversation, AiResponse Response)> StartConversationAsync(
        string prompt, CancellationToken cancellationToken = default, AiRequestOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prompt);

        var conversation = new AiConversation();
        var userMessage = new AiChatMessage { Role = AiChatRole.User, Text = prompt };

        var request = BuildRequest(conversation, userMessage);
        var response = await SendRequestAsync(request, cancellationToken, options).ConfigureAwait(false);

        if (response.Success)
        {
            conversation.AddMessage(userMessage);
            conversation.AddMessage(new AiChatMessage { Role = AiChatRole.Model, Text = response.Text });
        }

        return (conversation, response);
    }

    public Task<AiResponse> ContinueConversationAsync(
        AiConversation conversation, string prompt, CancellationToken cancellationToken = default, AiRequestOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(conversation);
        ArgumentException.ThrowIfNullOrWhiteSpace(prompt);

        var userMessage = new AiChatMessage { Role = AiChatRole.User, Text = prompt };
        return ContinueConversationCoreAsync(conversation, userMessage, cancellationToken, options);
    }

    public Task<AiResponse> ContinueConversationAsync(
        AiConversation conversation, byte[] imageBytes, string mimeType = "image/jpeg",
        CancellationToken cancellationToken = default, AiRequestOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(conversation);
        ArgumentNullException.ThrowIfNull(imageBytes);

        var userMessage = new AiChatMessage { Role = AiChatRole.User, ImageBytes = imageBytes, MimeType = mimeType };
        return ContinueConversationCoreAsync(conversation, userMessage, cancellationToken, options);
    }

    public Task<AiResponse> ContinueConversationAsync(
        AiConversation conversation, string prompt, byte[] imageBytes, string mimeType = "image/jpeg",
        CancellationToken cancellationToken = default, AiRequestOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(conversation);
        ArgumentException.ThrowIfNullOrWhiteSpace(prompt);
        ArgumentNullException.ThrowIfNull(imageBytes);

        var userMessage = new AiChatMessage { Role = AiChatRole.User, Text = prompt, ImageBytes = imageBytes, MimeType = mimeType };
        return ContinueConversationCoreAsync(conversation, userMessage, cancellationToken, options);
    }

    private async Task<AiResponse> ContinueConversationCoreAsync(
        AiConversation conversation, AiChatMessage userMessage, CancellationToken cancellationToken, AiRequestOptions? options = null)
    {
        var request = BuildRequest(conversation, userMessage);
        var response = await SendRequestAsync(request, cancellationToken, options).ConfigureAwait(false);

        if (response.Success)
        {
            conversation.AddMessage(userMessage);
            conversation.AddMessage(new AiChatMessage { Role = AiChatRole.Model, Text = response.Text });
        }

        return response;
    }

    private async Task<AiResponse> SendRequestAsync(GeminiRequest request, CancellationToken cancellationToken, AiRequestOptions? options = null)
    {
        // Re-read from AppConfig at call time so a key set after construction
        // (e.g. via the web UI /api/agent/start endpoint) is always picked up.
        var apiKey = _apiKey ?? appConfig.Gemini.ApiKey;
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new InvalidOperationException("Gemini:ApiKey is not configured. Provide an API key via the web UI.");

        if (options?.MaxOutputTokens is not null)
        {
            var baseConfig = request.GenerationConfig ?? new GeminiGenerationConfig(null, null, null, null, null, null);
            request = request with { GenerationConfig = baseConfig with { MaxOutputTokens = options.MaxOutputTokens } };
        }

        var url = $"{BaseUrl}/{_model}:generateContent?key={apiKey}";

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

        var candidate = geminiResponse.Candidates[0];

        if (candidate.Content?.Parts is not { Count: > 0 })
        {
            var reason = candidate.FinishReason ?? "Unknown";
            logger.LogWarning("Gemini candidate has no content parts. FinishReason: {FinishReason}", reason);
            return new AiResponse(false, string.Empty, $"Gemini returned an empty candidate (finish reason: {reason}).");
        }

        var text = string.Concat(candidate.Content.Parts
            .Where(p => p.Text is not null && p.Thought is not true)
            .Select(p => p.Text!));

        if (string.Equals(candidate.FinishReason, "MAX_TOKENS", StringComparison.OrdinalIgnoreCase))
            logger.LogWarning("Gemini response was truncated (finish reason: MAX_TOKENS). Consider increasing Gemini:MaxOutputTokens.");

        if (logger.IsEnabled(LogLevel.Debug))
            logger.LogDebug("Received response from Gemini model {Model}.", _model);
        return new AiResponse(true, text);
    }

    private GeminiRequest BuildRequest(AiConversation conversation, AiChatMessage additionalMessage)
    {
        bool stripHistoryImages = appConfig.General.StripHistoryImages;

        var contents = new List<GeminiContent>(conversation.Messages.Count + 1);

        foreach (var message in conversation.Messages)
            contents.Add(ToGeminiContent(message, stripImages: stripHistoryImages));

        contents.Add(ToGeminiContent(additionalMessage, stripImages: false));
        return new GeminiRequest(contents, _generationConfig);
    }

    private static GeminiContent ToGeminiContent(AiChatMessage message, bool stripImages = false)
    {
        var role = message.Role == AiChatRole.User ? "user" : "model";
        var parts = new List<GeminiPart>();

        if (message.Text is not null)
            parts.Add(new GeminiPart(message.Text, null));

        if (!stripImages && message.ImageBytes is not null)
            parts.Add(new GeminiPart(null, new GeminiInlineData(message.MimeType ?? "image/jpeg", Convert.ToBase64String(message.ImageBytes))));

        return new GeminiContent(role, parts);
    }

    // --- Private request DTOs ---

    private record GeminiRequest(List<GeminiContent> Contents, GeminiGenerationConfig? GenerationConfig = null);
    private record GeminiGenerationConfig(string? MediaResolution, float? Temperature, float? TopP, int? TopK, int? MaxOutputTokens, GeminiThinkingConfig? ThinkingConfig);
    private record GeminiThinkingConfig(int? ThinkingBudget, string? ThinkingLevel);
    private record GeminiContent(string Role, List<GeminiPart> Parts);
    private record GeminiPart(string? Text, GeminiInlineData? InlineData, bool? Thought = null);
    private record GeminiInlineData(string MimeType, string Data);

    // --- Private response DTOs ---

    private record GeminiResponse(List<GeminiCandidate>? Candidates, GeminiPromptFeedback? PromptFeedback);
    private record GeminiCandidate(GeminiContent? Content, string? FinishReason);
    private record GeminiPromptFeedback(string? BlockReason);
}