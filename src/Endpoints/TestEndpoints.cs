using System.Collections.Concurrent;
using Thio_Universal_Agent.AI_API.Anthropic;
using Thio_Universal_Agent.AI_API.Gemini;
using Thio_Universal_Agent.AI_API.OpenAI;

namespace Thio_Universal_Agent.Endpoints;

//TODO: Eventually lock these endpoints off so they are only accessible either with a special launch flag, or even a certain build configuration for debugging.
//      I don't want the low level commands like screenshot or chat to be programatically controlled remotely by default for security reasons

internal static class TestEndpoints
{

    private static bool CheckTestingEnabled(AppConfig appConfig) => appConfig.General.EnableDebugMode;

    private static readonly string TestingDisabledErrorMsg = "Testing endpoints are disabled. To enable, set EnableDebugMode to true in the General config section.";

    private static readonly ConcurrentDictionary<string, TestConversationSession> _conversations = new();

    internal static void MapTestEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/test");

        // Thin HTTP shells for the browser-based test UI.
        // Production agent code calls these C# classes directly — never through these endpoints.
        group.MapGet("/screenshot", (IScreenProvider screenProvider, AppConfig appConfig) =>
        {
            if (!CheckTestingEnabled(appConfig))
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
        group.MapPost("/chat", async (TestChatRequest req, IAiProvider aiProvider, IHttpClientFactory httpClientFactory, AppConfig appConfig, ILoggerFactory loggerFactory, CancellationToken ct) =>
        {
            if (!CheckTestingEnabled(appConfig))
                return Results.Problem(TestingDisabledErrorMsg);

            try
            {
                bool hasImage = !string.IsNullOrWhiteSpace(req.ImageBase64);
                bool hasPrompt = !string.IsNullOrWhiteSpace(req.Prompt);
                bool isNewConversation = string.IsNullOrWhiteSpace(req.ConversationId);
                bool isKeyOverride = !string.IsNullOrWhiteSpace(req.ApiKey);

                if (isNewConversation)
                {
                    // Resolve provider: key-override creates a throwaway instance; otherwise use the injected singleton.
                    IAiProvider provider = isKeyOverride
                        ? CreateOverrideProvider(req.Provider ?? appConfig.General.ActiveProvider, req.ApiKey!, req.Model, appConfig, httpClientFactory, loggerFactory)
                        : aiProvider;
                    AiProviderType resolvedProviderType = isKeyOverride
                        ? (req.Provider ?? appConfig.General.ActiveProvider)
                        : appConfig.General.ActiveProvider;

                    // StartConversationAsync is text-only; first messages with an image are sent as a one-shot.
                    if (hasImage)
                    {
                        if (!hasPrompt)
                            return Results.Problem("Please include text with your first image message.");

                        var result = await provider.SendPromptWithImageAsync(
                            req.Prompt!, Convert.FromBase64String(req.ImageBase64!), req.ImageMimeType ?? "image/jpeg", ct);
                        return result.Success
                            ? Results.Ok(new { result.Text, conversationId = (string?)null })
                            : Results.Problem(result.ErrorMessage);
                    }

                    if (!hasPrompt)
                        return Results.Problem("Prompt is required to start a conversation.");

                    var (conversation, response) = await provider.StartConversationAsync(req.Prompt!, ct);
                    if (!response.Success)
                        return Results.Problem(response.ErrorMessage);

                    var newId = Guid.NewGuid().ToString("N");
                    _conversations[newId] = isKeyOverride
                        ? new TestConversationSession { Conversation = conversation, OverrideApiKey = req.ApiKey, OverrideProviderType = resolvedProviderType, OverrideModel = req.Model }
                        : new TestConversationSession { Conversation = conversation };
                    return Results.Ok(new { response.Text, conversationId = newId });
                }
                else
                {
                    if (!_conversations.TryGetValue(req.ConversationId!, out var session))
                        return Results.Problem("Conversation not found or expired. Please clear and start a new conversation.");
                    if (session.Conversation == null)
                        return Results.Problem("Invalid conversation state. Please clear and start a new conversation.");

                    // Guard against mid-conversation provider-mode switches.
                    if (session.IsApiKeyMode && !isKeyOverride)
                        return Results.Problem("Cannot switch from API-key override mode to server-key mode mid-conversation. Please clear and start a new conversation.");
                    if (!session.IsApiKeyMode && isKeyOverride)
                        return Results.Problem("Cannot switch from server-key mode to API-key override mid-conversation. Please clear and start a new conversation.");

                    // For key-override sessions, recreate the provider from the credentials stored at conversation start.
                    IAiProvider provider = session.IsApiKeyMode
                        ? CreateOverrideProvider(session.OverrideProviderType, session.OverrideApiKey!, session.OverrideModel, appConfig, httpClientFactory, loggerFactory)
                        : aiProvider;

                    AiResponse response;
                    if (hasImage && hasPrompt)
                        response = await provider.ContinueConversationAsync(
                            session.Conversation, req.Prompt!, Convert.FromBase64String(req.ImageBase64!), req.ImageMimeType ?? "image/jpeg", ct);
                    else if (hasImage)
                        response = await provider.ContinueConversationAsync(
                            session.Conversation, Convert.FromBase64String(req.ImageBase64!), req.ImageMimeType ?? "image/jpeg", ct);
                    else
                        response = await provider.ContinueConversationAsync(session.Conversation, req.Prompt!, ct);

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
        group.MapPost("/make-grid-image", async (TestCoordinatePromptRequest req, CoordinatePrompter prompter, AppConfig appConfig, CancellationToken ct) =>
        {
            if (!CheckTestingEnabled(appConfig))
                return Results.Problem(TestingDisabledErrorMsg);
            try
            {
                if (req.Screenshot is not { } screenshot)
                    return Results.Problem("Screenshot image is required.");

                byte[] gridImageBytes = CoordinatePrompter.CreateFullGridOverlayImage(screenshot.Original, appConfig);
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
            if (!CheckTestingEnabled(appConfig))
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
                    var providerType = req.Provider ?? appConfig.General.ActiveProvider;
                    var overrideConfig = new AppConfig { General = appConfig.General, Agent = appConfig.Agent };
                    IAiProvider provider = CreateOverrideProvider(providerType, req.ApiKey!, req.Model, appConfig, httpClientFactory, loggerFactory);
                    prompter = new CoordinatePrompter(provider, appConfig);
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

    private sealed class TestConversationSession
    {
        public AiConversation? Conversation { get; set; }

        // Only populated for key-override sessions; null means "use injected IAiProvider".
        public string? OverrideApiKey { get; init; }
        public AiProviderType OverrideProviderType { get; init; }
        public string? OverrideModel { get; init; }

        public bool IsApiKeyMode => OverrideApiKey is not null;
    }

    /// <summary>
    /// Creates a throwaway <see cref="IAiProvider"/> using an API-key override supplied by the test client.
    /// The provider type is determined by <paramref name="providerType"/>; model falls back to the
    /// corresponding entry in <paramref name="baseConfig"/> when the caller omits it.
    /// </summary>
    private static IAiProvider CreateOverrideProvider(
        AiProviderType providerType, string apiKey, string? model,
        AppConfig baseConfig, IHttpClientFactory httpClientFactory, ILoggerFactory loggerFactory)
    {
        var httpClient = httpClientFactory.CreateClient();
        return providerType switch
        {
            AiProviderType.ChatGPT => new OpenAIProvider(
                httpClient,
                new AppConfig { OpenAI = new OpenAIConfig { ApiKey = apiKey, Model = string.IsNullOrWhiteSpace(model) ? baseConfig.OpenAI.Model : model! }, General = baseConfig.General, Agent = baseConfig.Agent },
                loggerFactory.CreateLogger<OpenAIProvider>()),
            AiProviderType.Claude => new AnthropicProvider(
                httpClient,
                new AppConfig { Anthropic = new AnthropicConfig { ApiKey = apiKey, Model = string.IsNullOrWhiteSpace(model) ? baseConfig.Anthropic.Model : model! }, General = baseConfig.General, Agent = baseConfig.Agent },
                loggerFactory.CreateLogger<AnthropicProvider>()),
            _ => new GeminiProvider(
                httpClient,
                new AppConfig { Gemini = new GeminiConfig { ApiKey = apiKey, Model = string.IsNullOrWhiteSpace(model) ? baseConfig.Gemini.Model : model! }, General = baseConfig.General, Agent = baseConfig.Agent },
                loggerFactory.CreateLogger<GeminiProvider>()),
        };
    }
}

// Scoped to this file — it's a transport detail for the test endpoint, not a domain type.
file record TestChatRequest(string? Prompt, string? ApiKey, string? Model, AiProviderType? Provider, string? ImageBase64, string? ImageMimeType, string? ConversationId);
file record TestCoordinatePromptRequest(string? ScreenshotBase64, string? ItemToIdentify, string? ApiKey, string? Model, AiProviderType? Provider, string? Mode, int OriginX = 0, int OriginY = 0)
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
