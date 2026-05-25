// src/AI_API/Anthropic/AnthropicProvider.cs
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Thio_Universal_Agent.AI_API.Anthropic;

/// <summary>
/// Anthropic REST API implementation of <see cref="IAiProvider"/>.
/// Automatically handles the alternating User/Assistant role requirement.
/// </summary>
public sealed class AnthropicProvider(HttpClient httpClient, AppConfig appConfig, ILogger<AnthropicProvider> logger) : IAiProvider
{
    private const string BaseUrl = "https://api.anthropic.com/v1/messages";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly string? _apiKey = appConfig.Anthropic.ApiKey;
    private readonly string _model = appConfig.Anthropic.Model;

    public Task<AiResponse> SendPromptAsync(string prompt, CancellationToken cancellationToken = default, AiRequestOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prompt);
        AnthropicRequest request = new AnthropicRequest(
            Model: _model,
            Messages: [
                new AnthropicMessage(
                    "user",
                    Content: [
                        new AnthropicContentPart(
                            Type: "text",
                            Text: prompt,
                            Source: null
                        )
                    ]
                )
            ],
            Temperature: appConfig.Anthropic.Temperature,
            MaxTokens: options?.MaxOutputTokens ?? appConfig.Anthropic.MaxOutputTokens ?? 4096
        );
        return SendRequestAsync(request, cancellationToken);
    }

    public Task<AiResponse> SendPromptWithImageAsync(string prompt, byte[] imageBytes, string mimeType = "image/jpeg", CancellationToken cancellationToken = default, AiRequestOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prompt);
        ArgumentNullException.ThrowIfNull(imageBytes);

        AnthropicRequest request = new AnthropicRequest(
            Model: _model,
            Messages: [
                new AnthropicMessage(
                    Role: "user", 
                    Content: [
                        new AnthropicContentPart(
                            Type: "image", 
                            Text: null, 
                            Source: new AnthropicImageSource(
                                Type: "base64",
                                MediaType: mimeType,
                                Data: Convert.ToBase64String(imageBytes)
                            )
                        ),
                        new AnthropicContentPart(
                            Type: "text",
                            Text: prompt, 
                            Source: null
                        )
                    ]
                )
            ],
            Temperature: appConfig.Anthropic.Temperature, 
            MaxTokens: options?.MaxOutputTokens ?? appConfig.Anthropic.MaxOutputTokens ?? 4096
        );

        return SendRequestAsync(request, cancellationToken);
    }

    public async Task<(AiConversation Conversation, AiResponse Response)> StartConversationAsync(string prompt, CancellationToken cancellationToken = default, AiRequestOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prompt);

        AiConversation conversation = new AiConversation();
        AiChatMessage userMessage = new AiChatMessage { Role = AiChatRole.User, Text = prompt };

        AnthropicRequest request = BuildRequest(
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
        var userMessage = new AiChatMessage { Role = AiChatRole.User, Text = prompt };
        return ContinueConversationCoreAsync(
            conversation: conversation,
            userMessage: userMessage,
            cancellationToken: cancellationToken,
            options: options
        );
    }

    public Task<AiResponse> ContinueConversationAsync(AiConversation conversation, byte[] imageBytes, string mimeType = "image/jpeg", CancellationToken cancellationToken = default, AiRequestOptions? options = null)
    {
        var userMessage = new AiChatMessage { Role = AiChatRole.User, ImageBytes = imageBytes, MimeType = mimeType };
        return ContinueConversationCoreAsync(
            conversation: conversation,
            userMessage: userMessage,
            cancellationToken: cancellationToken,
            options: options
        );
    }

    public Task<AiResponse> ContinueConversationAsync(AiConversation conversation, string prompt, byte[] imageBytes, string mimeType = "image/jpeg", CancellationToken cancellationToken = default, AiRequestOptions? options = null)
    {
        var userMessage = new AiChatMessage { Role = AiChatRole.User, Text = prompt, ImageBytes = imageBytes, MimeType = mimeType };
        return ContinueConversationCoreAsync(
            conversation: conversation,
            userMessage: userMessage,
            cancellationToken: cancellationToken,
            options: options
        );
    }

    private async Task<AiResponse> ContinueConversationCoreAsync(AiConversation conversation, AiChatMessage userMessage, CancellationToken cancellationToken, AiRequestOptions? options)
    {
        AnthropicRequest request = BuildRequest(
            conversation: conversation, 
            additionalMessage: userMessage, 
            options: options
        );

        AiResponse response = await SendRequestAsync(request: request, cancellationToken: cancellationToken).ConfigureAwait(false);

        if (response.Success)
        {
            conversation.AddMessage(message: userMessage);
            conversation.AddMessage(message: new AiChatMessage { Role = AiChatRole.Model, Text = response.Text });
        }

        return response;
    }

    private async Task<AiResponse> SendRequestAsync(AnthropicRequest request, CancellationToken cancellationToken)
    {
        string? apiKey = _apiKey ?? appConfig.Anthropic.ApiKey;
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new InvalidOperationException("Anthropic:ApiKey is not configured. Provide an API key via the web UI.");

        if (logger.IsEnabled(LogLevel.Debug))
            logger.LogDebug("Sending prompt to Anthropic model {Model}.", _model);

        using HttpRequestMessage httpRequest = new HttpRequestMessage(HttpMethod.Post, BaseUrl);
        httpRequest.Headers.Add("x-api-key", apiKey);
        httpRequest.Headers.Add("anthropic-version", "2023-06-01");
        httpRequest.Content = JsonContent.Create(request, options: JsonOptions);

        using HttpResponseMessage response = await httpClient.SendAsync(httpRequest, cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            string errorBody = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            logger.LogError("Anthropic API returned {StatusCode}. Body: {ErrorBody}", (int)response.StatusCode, errorBody);

            return new AiResponse(
                Success: false,
                Text: string.Empty, ErrorMessage: $"HTTP {(int)response.StatusCode}: {errorBody}"
            );
        }

        AnthropicResponse? anthropicResponse = await response.Content.ReadFromJsonAsync<AnthropicResponse>(JsonOptions, cancellationToken).ConfigureAwait(false);
        string? text = anthropicResponse?.Content?.FirstOrDefault(c => c.Type == "text")?.Text;

        if (string.IsNullOrWhiteSpace(text))
        {
            return new AiResponse(
                Success: false,
                Text: string.Empty,
                ErrorMessage: "Anthropic returned an empty response."
            );
        }

        return new AiResponse(
            Success: true, 
            Text: text
        );
    }

    private AnthropicRequest BuildRequest(AiConversation conversation, AiChatMessage additionalMessage, AiRequestOptions? options)
    {
        bool stripHistoryImages = appConfig.General.StripHistoryImages;
        List<AnthropicMessage> messages = new List<AnthropicMessage>(conversation.Messages.Count + 1);

        foreach (AiChatMessage message in conversation.Messages)
            messages.Add(ToAnthropicMessage(message: message, stripImages: stripHistoryImages));

        messages.Add(ToAnthropicMessage(message: additionalMessage, stripImages: false));

        return new AnthropicRequest(
            Model: _model,
            Messages: messages,
            Temperature: appConfig.Anthropic.Temperature,
            MaxTokens: options?.MaxOutputTokens ?? appConfig.Anthropic.MaxOutputTokens ?? 4096
        );
    }

    private static AnthropicMessage ToAnthropicMessage(AiChatMessage message, bool stripImages)
    {
        string role = message.Role == AiChatRole.User ? "user" : "assistant";
        List<AnthropicContentPart> content = new List<AnthropicContentPart>();

        if (!stripImages && message.ImageBytes is not null)
        {
            content.Add(new AnthropicContentPart(
                Type: "image",
                Text: null,
                Source: new AnthropicImageSource(
                    Type: "base64",
                    MediaType: message.MimeType ?? "image/jpeg",
                    Data: Convert.ToBase64String(message.ImageBytes)
                )
            ));
        }

        if (message.Text is not null)
            content.Add(new AnthropicContentPart(Type: "text", Text: message.Text, Source: null));

        return new AnthropicMessage(Role: role, Content: content);
    }

    // --- Private DTOs ---

    private record AnthropicRequest(string Model, List<AnthropicMessage> Messages, float? Temperature, [property: JsonPropertyName("max_tokens")] int MaxTokens);
    private record AnthropicMessage(string Role, List<AnthropicContentPart> Content);
    private record AnthropicContentPart(string Type, string? Text, AnthropicImageSource? Source);
    private record AnthropicImageSource(string Type, [property: JsonPropertyName("media_type")] string MediaType, string Data);
    private record AnthropicResponse(List<AnthropicContentResponse>? Content, AnthropicError? Error, [property: JsonPropertyName("stop_reason")] string? StopReason);
    private record AnthropicContentResponse(string Type, string? Text);
    private record AnthropicError(string? Message);
}