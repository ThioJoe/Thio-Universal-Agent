// src/AI_API/OpenAI/OpenAIProvider.cs
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Thio_Universal_Agent.AI_API.OpenAI;

/// <summary>
/// OpenAI REST API implementation of <see cref="IAiProvider"/>.
/// </summary>
public sealed class OpenAIProvider(HttpClient httpClient, AppConfig appConfig, ILogger<OpenAIProvider> logger) : IAiProvider
{
    private const string BaseUrl = "https://api.openai.com/v1/chat/completions";
    private readonly string? _apiKey = appConfig.OpenAI.ApiKey;
    private readonly string _model = appConfig.OpenAI.Model;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public Task<AiResponse> SendPromptAsync(string prompt, CancellationToken cancellationToken = default, AiRequestOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prompt);

        OpenAIRequest request = new OpenAIRequest(
            Model: _model,
            Messages: [
                new OpenAIMessage(
                    "user",
                    Content: [
                        new OpenAIContentPart(
                            Type: "text",
                            Text: prompt,
                            ImageUrl: null
                        )
                    ]
                )
            ],
            Temperature: appConfig.OpenAI.Temperature,
            MaxTokens: options?.MaxOutputTokens ?? appConfig.OpenAI.MaxOutputTokens
            );
        return SendRequestAsync(request, cancellationToken);
    }

    public Task<AiResponse> SendPromptWithImageAsync(string prompt, byte[] imageBytes, string mimeType = "image/png", CancellationToken cancellationToken = default, AiRequestOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prompt);
        ArgumentNullException.ThrowIfNull(imageBytes);

        OpenAIRequest request = new OpenAIRequest(
            _model,
            Messages: [
                new OpenAIMessage(
                    Role: "user",
                    Content: [
                        new OpenAIContentPart(
                            Type: "text",
                            Text: prompt,
                            ImageUrl: null
                        ),
                        new OpenAIContentPart(
                            Type: "image_url",
                            Text: null,
                            ImageUrl: new OpenAIImageUrl($"data:{mimeType};base64,{Convert.ToBase64String(imageBytes)}"
                            )
                        )
                    ]
                )
            ],
            Temperature: appConfig.OpenAI.Temperature,
            MaxTokens: options?.MaxOutputTokens ?? appConfig.OpenAI.MaxOutputTokens
        );

        return SendRequestAsync(request, cancellationToken);
    }

    public async Task<(AiConversation Conversation, AiResponse Response)> StartConversationAsync(string prompt, CancellationToken cancellationToken = default, AiRequestOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prompt);

        AiConversation conversation = new AiConversation();
        AiChatMessage userMessage = new AiChatMessage { Role = AiChatRole.User, Text = prompt };

        OpenAIRequest request = BuildRequest(
            conversation: conversation,
            additionalMessage: userMessage,
            options: options
        );

        AiResponse response = await SendRequestAsync(request, cancellationToken).ConfigureAwait(false);

        if (response.Success)
        {
            conversation.AddMessage(message: userMessage);
            conversation.AddMessage(message: new AiChatMessage { Role = AiChatRole.Model, Text = response.Text });
        }

        return (conversation, response);
    }

    public Task<AiResponse> ContinueConversationAsync(AiConversation conversation, string prompt, CancellationToken cancellationToken = default, AiRequestOptions? options = null)
    {
        AiChatMessage userMessage = new AiChatMessage { Role = AiChatRole.User, Text = prompt };

        return ContinueConversationCoreAsync(
            conversation: conversation,
            userMessage: userMessage,
            cancellationToken: cancellationToken,
            options: options
        );
    }

    public Task<AiResponse> ContinueConversationAsync(AiConversation conversation, byte[] imageBytes, string mimeType = "image/png", CancellationToken cancellationToken = default, AiRequestOptions? options = null)
    {
        AiChatMessage userMessage = new AiChatMessage { Role = AiChatRole.User, ImageBytes = imageBytes, MimeType = mimeType };

        return ContinueConversationCoreAsync(
            conversation: conversation,
            userMessage: userMessage,
            cancellationToken: cancellationToken,
            options: options
        );
    }

    public Task<AiResponse> ContinueConversationAsync(AiConversation conversation, string prompt, byte[] imageBytes, string mimeType = "image/png", CancellationToken cancellationToken = default, AiRequestOptions? options = null)
    {
        AiChatMessage userMessage = new AiChatMessage { Role = AiChatRole.User, Text = prompt, ImageBytes = imageBytes, MimeType = mimeType };

        return ContinueConversationCoreAsync(
            conversation: conversation,
            userMessage: userMessage,
            cancellationToken: cancellationToken,
            options: options
        );
    }

    private async Task<AiResponse> ContinueConversationCoreAsync(AiConversation conversation, AiChatMessage userMessage, CancellationToken cancellationToken, AiRequestOptions? options)
    {
        OpenAIRequest request = BuildRequest(conversation, userMessage, options);
        AiResponse response = await SendRequestAsync(request, cancellationToken).ConfigureAwait(false);

        if (response.Success)
        {
            conversation.AddMessage(message: userMessage);
            conversation.AddMessage(message: new AiChatMessage { Role = AiChatRole.Model, Text = response.Text });
        }

        return response;
    }

    private async Task<AiResponse> SendRequestAsync(OpenAIRequest request, CancellationToken cancellationToken)
    {
        string? apiKey = _apiKey ?? appConfig.OpenAI.ApiKey;

        if (string.IsNullOrWhiteSpace(apiKey))
            throw new InvalidOperationException("OpenAI:ApiKey is not configured. Provide an API key via the web UI.");

        if (logger.IsEnabled(LogLevel.Debug))
            logger.LogDebug("Sending prompt to OpenAI model {Model}.", _model);

        using HttpRequestMessage httpRequest = new HttpRequestMessage(HttpMethod.Post, BaseUrl);
        httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        httpRequest.Content = JsonContent.Create(request, options: JsonOptions);

        using HttpResponseMessage response = await httpClient.SendAsync(httpRequest, cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            string errorBody = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            logger.LogError("OpenAI API returned {StatusCode}. Body: {ErrorBody}", (int)response.StatusCode, errorBody);

            return new AiResponse(false, string.Empty, $"HTTP {(int)response.StatusCode}: {errorBody}");
        }

        OpenAIResponse? openAiResponse = await response.Content.ReadFromJsonAsync<OpenAIResponse>(JsonOptions, cancellationToken).ConfigureAwait(false);
        string? text = openAiResponse?.Choices?.FirstOrDefault()?.Message?.Content;

        if (string.IsNullOrWhiteSpace(text))
            return new AiResponse(false, string.Empty, "OpenAI returned an empty response.");

        TokenUsage? usage = null;
        if (openAiResponse?.Usage != null)
        {
            int? reasoningTokens = openAiResponse.Usage.CompletionTokensDetails?.ReasoningTokens;
            usage = new TokenUsage(
                openAiResponse.Usage.PromptTokens ?? 0,
                openAiResponse.Usage.CompletionTokens ?? 0,
                openAiResponse.Usage.TotalTokens ?? 0,
                reasoningTokens > 0 ? reasoningTokens : null
            );
        }

        return new AiResponse(true, text, Usage: usage);
    }

    private OpenAIRequest BuildRequest(AiConversation conversation, AiChatMessage additionalMessage, AiRequestOptions? options)
    {
        bool stripHistoryImages = appConfig.General.StripHistoryImages;
        List<OpenAIMessage> messages = new List<OpenAIMessage>(conversation.Messages.Count + 1);

        foreach (AiChatMessage message in conversation.Messages)
            messages.Add(ToOpenAIMessage(message: message, stripImages: stripHistoryImages));

        messages.Add(ToOpenAIMessage(message: additionalMessage, stripImages: false));

        return new OpenAIRequest(
            Model: _model,
            Messages: messages,
            Temperature: appConfig.OpenAI.Temperature,
            MaxTokens: options?.MaxOutputTokens ?? appConfig.OpenAI.MaxOutputTokens
        );
    }

    private static OpenAIMessage ToOpenAIMessage(AiChatMessage message, bool stripImages)
    {
        string role = message.Role == AiChatRole.User ? "user" : "assistant";
        List<OpenAIContentPart> content = new List<OpenAIContentPart>();

        if (message.Text is not null)
        {
            content.Add(new OpenAIContentPart(
                Type: "text",
                Text: message.Text,
                ImageUrl: null
            ));
        }

        if (!stripImages && message.ImageBytes is not null)
        {
            content.Add(new OpenAIContentPart(
                Type: "image_url",
                Text: null,
                ImageUrl: new OpenAIImageUrl($"data:{message.MimeType ?? "image/png"};base64,{Convert.ToBase64String(message.ImageBytes)}")
            ));
        }

        return new OpenAIMessage(role, content);
    }

    // --- Private DTOs ---

    private record OpenAIRequest(string Model, List<OpenAIMessage> Messages, float? Temperature, [property: JsonPropertyName("max_tokens")] int? MaxTokens);
    private record OpenAIMessage(string Role, List<OpenAIContentPart> Content);
    private record OpenAIContentPart(string Type, string? Text, [property: JsonPropertyName("image_url")] OpenAIImageUrl? ImageUrl);
    private record OpenAIImageUrl([property: JsonPropertyName("url")] string Url);
    private record OpenAICompletionTokensDetails([property: JsonPropertyName("reasoning_tokens")] int? ReasoningTokens);
    private record OpenAIUsage([property: JsonPropertyName("prompt_tokens")] int? PromptTokens, [property: JsonPropertyName("completion_tokens")] int? CompletionTokens, [property: JsonPropertyName("total_tokens")] int? TotalTokens, [property: JsonPropertyName("completion_tokens_details")] OpenAICompletionTokensDetails? CompletionTokensDetails);
    private record OpenAIResponse(List<OpenAIChoice>? Choices, OpenAIError? Error, OpenAIUsage? Usage);
    private record OpenAIChoice(OpenAIMessageResponse? Message, [property: JsonPropertyName("finish_reason")] string? FinishReason);
    private record OpenAIMessageResponse(string? Content);
    private record OpenAIError(string? Message);
}