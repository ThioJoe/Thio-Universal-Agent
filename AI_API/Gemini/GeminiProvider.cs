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
public sealed class GeminiProvider : IAiProvider
{
    private const string BaseUrl = "https://generativelanguage.googleapis.com/v1beta/models";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly HttpClient _httpClient;
    private readonly ILogger<GeminiProvider> _logger;
    private readonly string _apiKey;
    private readonly string _model;

    public GeminiProvider(HttpClient httpClient, IConfiguration configuration, ILogger<GeminiProvider> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
        _apiKey = configuration["Gemini:ApiKey"] ?? throw new InvalidOperationException("Gemini:ApiKey is not configured.");
        _model = configuration["Gemini:Model"] ?? "gemini-2.0-flash";
    }

    public Task<AiResponse> SendPromptAsync(string prompt, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prompt);

        var request = new GeminiRequest([new GeminiContent([new GeminiPart(prompt, null)])]);
        return SendRequestAsync(request, cancellationToken);
    }

    public Task<AiResponse> SendPromptWithImageAsync(string prompt, byte[] imageBytes, string mimeType = "image/jpeg", CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prompt);
        ArgumentNullException.ThrowIfNull(imageBytes);

        GeminiRequest request = new(
        [
            new GeminiContent(
            [
                new GeminiPart(prompt, null),
                new GeminiPart(null, new GeminiInlineData(mimeType, Convert.ToBase64String(imageBytes)))
            ])
        ]);

        return SendRequestAsync(request, cancellationToken);
    }

    private async Task<AiResponse> SendRequestAsync(GeminiRequest request, CancellationToken cancellationToken)
    {
        var url = $"{BaseUrl}/{_model}:generateContent?key={_apiKey}";

        _logger.LogDebug("Sending prompt to Gemini model {Model}.", _model);

        using var response = await _httpClient.PostAsJsonAsync(url, request, JsonOptions, cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            _logger.LogError("Gemini API returned {StatusCode}. Body: {ErrorBody}", (int)response.StatusCode, errorBody);
            return new AiResponse(false, string.Empty, $"HTTP {(int)response.StatusCode}: {errorBody}");
        }

        var geminiResponse = await response.Content
            .ReadFromJsonAsync<GeminiResponse>(JsonOptions, cancellationToken)
            .ConfigureAwait(false);

        if (geminiResponse?.Candidates is not { Count: > 0 })
        {
            var blockReason = geminiResponse?.PromptFeedback?.BlockReason ?? "No candidates returned.";
            _logger.LogWarning("Gemini returned no candidates. Reason: {BlockReason}", blockReason);
            return new AiResponse(false, string.Empty, $"Blocked: {blockReason}");
        }

        var text = geminiResponse.Candidates[0].Content.Parts
            .Where(p => p.Text is not null)
            .Select(p => p.Text!)
            .FirstOrDefault() ?? string.Empty;

        _logger.LogDebug("Received response from Gemini model {Model}.", _model);
        return new AiResponse(true, text);
    }

    // --- Private request DTOs ---

    private record GeminiRequest(List<GeminiContent> Contents);
    private record GeminiContent(List<GeminiPart> Parts);
    private record GeminiPart(string? Text, GeminiInlineData? InlineData);
    private record GeminiInlineData(string MimeType, string Data);

    // --- Private response DTOs ---

    private record GeminiResponse(List<GeminiCandidate>? Candidates, GeminiPromptFeedback? PromptFeedback);
    private record GeminiCandidate(GeminiContent Content, string FinishReason);
    private record GeminiPromptFeedback(string? BlockReason);
}
