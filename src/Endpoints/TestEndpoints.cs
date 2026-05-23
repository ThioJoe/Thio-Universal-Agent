using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Thio_Universal_Agent;
using Thio_Universal_Agent.AI_API.Gemini;
using Thio_Universal_Agent.Logic;

namespace Thio_Universal_Agent.Endpoints;

//TODO: Eventually lock these endpoints off so they are only accessible either with a special launch flag, or even a certain build configuration for debugging.
//      I don't want the low level commands like screenshot or chat to be programatically controlled remotely by default for security reasons

internal static class TestEndpoints
{

    // Might flesh this out later which is why it's its own function
    private static bool CheckTestingEnabled()
    {
        if (Globals.ENABLE_TESTING == true)
            return true;
        else
            return false;
    }

    private static readonly string TestingDisabledErrorMsg = "Testing endpoints are disabled. To enable, set Globals.ENABLE_TESTING to true.";

    private static readonly ConcurrentDictionary<string, TestConversationSession> _conversations = new();

    internal static void MapTestEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/test");

        // Thin HTTP shells for the browser-based test UI.
        // Production agent code calls these C# classes directly — never through these endpoints.
        group.MapGet("/screenshot", (IScreenProvider screenProvider) =>
        {
            if (!CheckTestingEnabled())
                return Results.Problem(TestingDisabledErrorMsg);

            try
            {
                byte[] imageBytes = screenProvider.CaptureScreen().Original;
                return Results.File(imageBytes, "image/jpeg");
            }
            catch (Exception ex)
            {
                return Results.Problem(ex.Message);
            }
        });

        // Thin HTTP shell for the browser-based test UI.
        // Production agent code calls IAiProvider directly in C# — never through this endpoint.
        group.MapPost("/chat", async (TestChatRequest req, IAiProvider aiProvider, IHttpClientFactory httpClientFactory, IConfiguration config, CancellationToken ct) =>
        {
            if (!CheckTestingEnabled())
                return Results.Problem(TestingDisabledErrorMsg);

            try
            {
                bool hasImage = !string.IsNullOrWhiteSpace(req.ImageBase64);
                bool hasPrompt = !string.IsNullOrWhiteSpace(req.Prompt);
                bool isNewConversation = string.IsNullOrWhiteSpace(req.ConversationId);

                if (!string.IsNullOrWhiteSpace(req.ApiKey))
                {
                    // Key override path: lets the test page use any key without touching appsettings.json.
                    var model = string.IsNullOrWhiteSpace(req.Model) ? config["Gemini:Model"] ?? "gemini-2.0-flash" : req.Model;
                    var url = $"https://generativelanguage.googleapis.com/v1beta/models/{model}:generateContent?key={req.ApiKey}";

                    var userParts = new List<object>();
                    if (hasPrompt)
                        userParts.Add(new { text = req.Prompt });
                    if (hasImage)
                        userParts.Add(new { inlineData = new { mimeType = req.ImageMimeType ?? "image/jpeg", data = req.ImageBase64 } });

                    string conversationId;
                    TestConversationSession session;
                    if (isNewConversation)
                    {
                        conversationId = Guid.NewGuid().ToString("N");
                        session = new TestConversationSession(IsApiKeyMode: true);
                        _conversations[conversationId] = session;
                    }
                    else
                    {
                        conversationId = req.ConversationId!;
                        if (!_conversations.TryGetValue(conversationId, out var found))
                            return Results.Problem("Conversation not found or expired. Please clear and start a new conversation.");
                        if (!found.IsApiKeyMode)
                            return Results.Problem("Cannot switch from server-key mode to API-key override mid-conversation. Please clear and start a new conversation.");
                        session = found;
                    }

                    var newUserTurn = new { role = "user", parts = userParts.ToArray() };
                    var contents = new List<object>(session.RawHistory) { newUserTurn };

                    using var client = httpClientFactory.CreateClient();
                    using var httpResponse = await client.PostAsJsonAsync(url, new { contents }, ct);
                    var raw = await httpResponse.Content.ReadAsStringAsync(ct);

                    if (!httpResponse.IsSuccessStatusCode)
                        return Results.Problem($"HTTP {(int)httpResponse.StatusCode}: {raw}");

                    using var doc = JsonDocument.Parse(raw);
                    var text = doc.RootElement
                        .GetProperty("candidates")[0]
                        .GetProperty("content")
                        .GetProperty("parts")[0]
                        .GetProperty("text")
                        .GetString() ?? string.Empty;

                    session.RawHistory.Add(newUserTurn);
                    session.RawHistory.Add(new { role = "model", parts = new[] { new { text } } });

                    return Results.Ok(new { text, conversationId });
                }

                // IAiProvider path
                if (isNewConversation)
                {
                    // StartConversationAsync is text-only; first messages with an image are sent as a one-shot.
                    if (hasImage)
                    {
                        if (!hasPrompt)
                            return Results.Problem("Please include text with your first image message.");

                        var result = await aiProvider.SendPromptWithImageAsync(
                            req.Prompt!, Convert.FromBase64String(req.ImageBase64!), req.ImageMimeType ?? "image/jpeg", ct);
                        return result.Success
                            ? Results.Ok(new { result.Text, conversationId = (string?)null })
                            : Results.Problem(result.ErrorMessage);
                    }

                    if (!hasPrompt)
                        return Results.Problem("Prompt is required to start a conversation.");

                    var (conversation, response) = await aiProvider.StartConversationAsync(req.Prompt!, ct);
                    if (!response.Success)
                        return Results.Problem(response.ErrorMessage);

                    var newId = Guid.NewGuid().ToString("N");
                    _conversations[newId] = new TestConversationSession(IsApiKeyMode: false) { Conversation = conversation };
                    return Results.Ok(new { response.Text, conversationId = newId });
                }
                else
                {
                    if (!_conversations.TryGetValue(req.ConversationId!, out var session))
                        return Results.Problem("Conversation not found or expired. Please clear and start a new conversation.");
                    if (session.IsApiKeyMode)
                        return Results.Problem("Cannot switch from API-key override mode to server-key mode mid-conversation. Please clear and start a new conversation.");
                    if (session.Conversation == null)
                        return Results.Problem("Invalid conversation state. Please clear and start a new conversation.");

                    AiResponse response;
                    if (hasImage && hasPrompt)
                        response = await aiProvider.ContinueConversationAsync(
                            session.Conversation, req.Prompt!, Convert.FromBase64String(req.ImageBase64!), req.ImageMimeType ?? "image/jpeg", ct);
                    else if (hasImage)
                        response = await aiProvider.ContinueConversationAsync(
                            session.Conversation, Convert.FromBase64String(req.ImageBase64!), req.ImageMimeType ?? "image/jpeg", ct);
                    else
                        response = await aiProvider.ContinueConversationAsync(session.Conversation, req.Prompt!, ct);

                    return response.Success
                        ? Results.Ok(new { response.Text, conversationId = req.ConversationId })
                        : Results.Problem(response.ErrorMessage);
                }
            }
            catch (Exception ex)
            {
                return Results.Problem(ex.Message);
            }
        });

        // Discards the server-side conversation so the next message starts fresh.
        group.MapDelete("/chat/{conversationId}", (string conversationId) =>
        {
            _conversations.TryRemove(conversationId, out _);
            return Results.NoContent();
        });

        // Doens't send a request but just uses attached screenshot image to create and display what grid image would be generated
        group.MapPost("/make-grid-image", async (TestCoordinatePromptRequest req, CoordinatePrompter prompter, CancellationToken ct) =>
        {
            if (!CheckTestingEnabled())
                return Results.Problem(TestingDisabledErrorMsg);
            try
            {
                if (req.Screenshot is not { } screenshot)
                    return Results.Problem("Screenshot image is required.");

                byte[] gridImageBytes = prompter.CreateFullGridOverlayImage(screenshot.Original);
                return Results.File(gridImageBytes, "image/png");
            }
            catch (Exception ex)
            {
                return Results.Problem(ex.Message);
            }
        });

        // Runs the full coordinate-prompt loop against a screenshot and returns every intermediate step for debugging.
        group.MapPost("/coordinate-prompt", async (
            TestCoordinatePromptRequest req,
            CoordinatePrompter defaultPrompter,
            IHttpClientFactory httpClientFactory,
            AppConfig appConfig,
            ILoggerFactory loggerFactory,
            CancellationToken ct) =>
        {
            if (!CheckTestingEnabled())
                return Results.Problem(TestingDisabledErrorMsg);

            try
            {
                if (req.Screenshot is null)
                    return Results.Problem("Screenshot image is required.");

                if (string.IsNullOrWhiteSpace(req.ItemToIdentify))
                    return Results.Problem("Item description is required.");

                CoordinatePrompter prompter;
                if (!string.IsNullOrWhiteSpace(req.ApiKey))
                {
                    // TODO: Change the way default model is handled here instead of hard coding
                    var overrideConfig = new AppConfig
                    {
                        Gemini = new GeminiConfig
                        {
                            ApiKey = req.ApiKey,
                            Model  = string.IsNullOrWhiteSpace(req.Model) ? appConfig.Gemini.Model : req.Model
                        }
                    };
                    var httpClient = httpClientFactory.CreateClient();
                    var logger = loggerFactory.CreateLogger<GeminiProvider>();
                    IAiProvider provider = new GeminiProvider(httpClient, overrideConfig, logger);
                    prompter = new CoordinatePrompter(provider, overrideConfig);
                }
                else
                {
                    prompter = defaultPrompter;
                }

                Screenshot screenshot = req.Screenshot!; // Origin (0, 0) — client has no virtual-desktop context
                CoordinateMode? coordinateMode = Enum.TryParse<CoordinateMode>(req.Mode, ignoreCase: true, out var parsedMode)
                    ? parsedMode
                    : null;

                var steps = new List<object>();

                ScreenCoordinate result = await prompter.GetCoordinatesForItemAsync(
                    screenshot,
                    req.ItemToIdentify,
                    mode: coordinateMode,
                    onStepCompleted: step =>
                    {
                        steps.Add(new
                        {
                            step.StepNumber,
                            GridImageBase64 = Convert.ToBase64String(step.GridImage),
                            step.AiResponseText,
                            step.ParsedX,
                            step.ParsedY,
                            AnnotatedImageBase64 = Convert.ToBase64String(step.AnnotatedImage)
                        });
                        return Task.CompletedTask;
                    },
                    cancellationToken: ct);

                return Results.Ok(new { Steps = steps, FinalScreenX = result.AbsoluteX, FinalScreenY = result.AbsoluteY, FinalNormX = result.NormalizedX, FinalNormY = result.NormalizedY });
            }
            catch (Exception ex)
            {
                return Results.Problem(ex.Message);
            }
        })
        .DisableAntiforgery();
    }

    private sealed class TestConversationSession(bool IsApiKeyMode)
    {
        public bool IsApiKeyMode { get; } = IsApiKeyMode;
        public AiConversation? Conversation { get; set; }
        public List<object> RawHistory { get; } = [];
    }
}

// Scoped to this file — it's a transport detail for the test endpoint, not a domain type.
file record TestChatRequest(string? Prompt, string? ApiKey, string? Model, string? ImageBase64, string? ImageMimeType, string? ConversationId);
file record TestCoordinatePromptRequest(string? ScreenshotBase64, string? ItemToIdentify, string? ApiKey, string? Model, string? Mode, int OriginX = 0, int OriginY = 0)
{
    /// <summary>
    /// Constructs a <see cref="Screenshot"/> from <see cref="ScreenshotBase64"/> and the
    /// virtual-desktop origin supplied by the client.
    /// Returns <see langword="null"/> when <see cref="ScreenshotBase64"/> is null.
    /// </summary>
    public Screenshot? Screenshot => ScreenshotBase64 is not null
        ? new Screenshot(Convert.FromBase64String(ScreenshotBase64), OriginX, OriginY)
        : null;
}
