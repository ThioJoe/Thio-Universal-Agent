using System.Collections.Concurrent;
using Thio_Universal_Agent.AI_API.Anthropic;
using Thio_Universal_Agent.AI_API.Gemini;
using Thio_Universal_Agent.AI_API.Onnx;
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
        RouteGroupBuilder group = app.MapGroup("/api/test");

        // Thin HTTP shells for the browser-based test UI.
        // Production agent code calls these C# classes directly — never through these endpoints.
        group.MapGet("/screenshot", (IScreenProvider screenProvider, AppConfig appConfig) =>
        {
            if (!CheckTestingEnabled(appConfig))
                return Results.Problem(TestingDisabledErrorMsg);

            try
            {
                byte[] imageBytes = screenProvider.CaptureScreen().Original;
                return Results.File(imageBytes, "image/png");
            }
            catch (DllNotFoundException ex)
            {
                return Results.Problem("ONNX Runtime native libraries missing. You likely need to install the Microsoft Visual C++ Redistributable. Details: " + ex.Message);
            }
            catch (Exception ex)
            {
                return Results.Problem(ex.Message);
            }
        });

        // Thin HTTP shell for the browser-based test UI.
        // Production agent code calls IAiProvider directly in C# — never through this endpoint.
        group.MapPost("/chat", async (TestChatRequest req, IHttpClientFactory httpClientFactory, AppConfig appConfig, ILoggerFactory loggerFactory, CancellationToken ct) =>
        {
            if (!CheckTestingEnabled(appConfig))
                return Results.Problem(TestingDisabledErrorMsg);

            try
            {
                bool hasImage = !string.IsNullOrWhiteSpace(req.ImageBase64);
                bool hasPrompt = !string.IsNullOrWhiteSpace(req.Prompt);
                bool isNewConversation = string.IsNullOrWhiteSpace(req.ConversationId);
                AiProviderType? resolvedProviderType = ResolveProviderType(req.Provider, appConfig);
                if (resolvedProviderType is null)
                    return Results.Problem("No active AI provider is configured. Select one in Configuration first.");

                string? resolvedModel = string.IsNullOrWhiteSpace(req.Model)
                    ? GetConfiguredModel(resolvedProviderType.Value, appConfig)
                    : req.Model;
                bool isKeyOverride = !string.IsNullOrWhiteSpace(req.ApiKey);
                string? resolvedApiKey = isKeyOverride ? req.ApiKey : null;

                if (isNewConversation)
                {
                    IAiProvider provider = CreateProvider(resolvedProviderType.Value, resolvedApiKey, resolvedModel, appConfig, httpClientFactory, loggerFactory);

                    // StartConversationAsync is text-only; first messages with an image are sent as a one-shot.
                    if (hasImage)
                    {
                        if (!hasPrompt)
                            return Results.Problem("Please include text with your first image message.");

                        AiResponse result = await provider.SendPromptWithImageAsync(
                            req.Prompt!, Convert.FromBase64String(req.ImageBase64!), req.ImageMimeType ?? "image/png", ct);

                        return result.Success
                            ? Results.Ok(new { result.Text, conversationId = (string?)null })
                            : Results.Problem(result.ErrorMessage);
                    }

                    if (!hasPrompt)
                        return Results.Problem("Prompt is required to start a conversation.");

                    (AiConversation? conversation, AiResponse? response) = await provider.StartConversationAsync(req.Prompt!, ct);
                    if (!response.Success)
                        return Results.Problem(response.ErrorMessage);

                    string newId = Guid.NewGuid().ToString("N");

                    _conversations[newId] = new TestConversationSession
                    {
                        Conversation = conversation,
                        OverrideApiKey = resolvedApiKey,
                        OverrideProviderType = resolvedProviderType.Value,
                        OverrideModel = resolvedModel,
                    };

                    return Results.Ok(new { response.Text, conversationId = newId });
                }
                else
                {
                    if (!_conversations.TryGetValue(req.ConversationId!, out TestConversationSession? session))
                        return Results.Problem("Conversation not found or expired. Please clear and start a new conversation.");
                    if (session.Conversation == null)
                        return Results.Problem("Invalid conversation state. Please clear and start a new conversation.");

                    if (req.Provider is { } requestedProvider && requestedProvider != session.OverrideProviderType)
                        return Results.Problem("Cannot switch AI providers mid-conversation. Please clear and start a new conversation.");
                    if (!string.IsNullOrWhiteSpace(req.Model) && !string.Equals(req.Model, session.OverrideModel, StringComparison.Ordinal))
                        return Results.Problem("Cannot switch models mid-conversation. Please clear and start a new conversation.");

                    // Guard against mid-conversation provider-mode switches.
                    if (session.IsApiKeyMode && !isKeyOverride)
                        return Results.Problem("Cannot switch from API-key override mode to server-key mode mid-conversation. Please clear and start a new conversation.");
                    if (!session.IsApiKeyMode && isKeyOverride)
                        return Results.Problem("Cannot switch from server-key mode to API-key override mid-conversation. Please clear and start a new conversation.");

                    IAiProvider provider = CreateProvider(session.OverrideProviderType, session.OverrideApiKey, session.OverrideModel, appConfig, httpClientFactory, loggerFactory);

                    AiResponse response;
                    if (hasImage && hasPrompt)
                    {
                        response = await provider.ContinueConversationAsync(
                            session.Conversation, req.Prompt!, Convert.FromBase64String(req.ImageBase64!), req.ImageMimeType ?? "image/png", ct
                        );
                    }
                    else if (hasImage)
                    {
                        response = await provider.ContinueConversationAsync(
                            session.Conversation, Convert.FromBase64String(req.ImageBase64!), req.ImageMimeType ?? "image/png", ct
                        );
                    }
                    else
                    {
                        response = await provider.ContinueConversationAsync(session.Conversation, req.Prompt!, ct);
                    }

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
        group.MapPost("/make-grid-image", async (TestCoordinatePromptRequest req, AppConfig appConfig, CancellationToken ct) =>
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

        group.MapPost("/window-markers/draw", (TestMarkerDrawRequest req, IScreenProvider screenProvider, AppConfig appConfig) =>
        {
            if (!CheckTestingEnabled(appConfig))
                return Results.Problem(TestingDisabledErrorMsg);

            try
            {
                if (req.Count is < 1 or > 24)
                    return Results.Problem("Count must be between 1 and 24.");

                if (req.DurationMs is < 0 or > int.MaxValue)
                    return Results.Problem("Duration cannot be negative.");

                if (req.MarkerOpacity is < 0 or > 255)
                    return Results.Problem("Marker opacity must be between 0 and 255.");

                if (req.FillOpacity is < 0 or > 255)
                    return Results.Problem("Fill opacity must be between 0 and 255.");

                MonitorInfo monitor = ResolveMarkerMonitor(screenProvider, req.MonitorIndex);
                TestMarkerKind scenario = ParseMarkerKind(req.MarkerKind);
                IReadOnlyList<TestMarkerDescriptor> markers = BuildMarkerDescriptors(scenario, monitor, req.Count, req.IncludeLabels);

                if (req.ClearExisting)
                    screenProvider.ClearMarkers();

                string? previousQueueLabel = screenProvider.CurrentQueueLabel;
                try
                {
                    for (int i = 0; i < markers.Count; i++)
                    {
                        TestMarkerDescriptor marker = markers[i];
                        screenProvider.CurrentQueueLabel = req.IncludeQueueNumbers ? marker.QueueLabel : null;

                        switch (marker.Kind)
                        {
                            case TestMarkerKind.ClickPoint:
                                screenProvider.DrawClickPointMarker(marker.X1, marker.Y1, req.DurationMs, req.MarkerOpacity, marker.Label);
                                break;
                            case TestMarkerKind.MouseMove:
                                screenProvider.DrawMouseMoveMarker(marker.X1, marker.Y1, req.DurationMs, req.MarkerOpacity, marker.Label);
                                break;
                            case TestMarkerKind.MouseMoveArrow:
                                screenProvider.DrawMouseMoveArrow(marker.X1, marker.Y1, req.DurationMs, req.MarkerOpacity);
                                break;
                            case TestMarkerKind.ClickDrag:
                                screenProvider.DrawClickDragMarker(marker.X1, marker.Y1, marker.X2 ?? marker.X1, marker.Y2 ?? marker.Y1, req.DurationMs, req.MarkerOpacity, marker.Label);
                                break;
                            case TestMarkerKind.BoundingBox:
                                screenProvider.DrawBoundingBox(marker.X1, marker.Y1, marker.X2 ?? marker.X1, marker.Y2 ?? marker.Y1, req.DurationMs, req.MarkerOpacity, req.FillOpacity, marker.Label);
                                break;
                            default:
                                throw new InvalidOperationException($"Unsupported marker kind: {marker.Kind}.");
                        }
                    }
                }
                finally
                {
                    screenProvider.CurrentQueueLabel = previousQueueLabel;
                }

                return Results.Ok(new
                {
                    selectedMonitor = monitor,
                    scenario = scenario.ToString(),
                    count = markers.Count,
                    markers = markers.Select(marker => new
                    {
                        kind = marker.Kind.ToString(),
                        label = marker.Label,
                        queueLabel = req.IncludeQueueNumbers ? marker.QueueLabel : null,
                        x1 = marker.X1,
                        y1 = marker.Y1,
                        x2 = marker.X2,
                        y2 = marker.Y2,
                    })
                });
            }
            catch (Exception ex)
            {
                return Results.Problem(ex.Message);
            }
        })
        .DisableAntiforgery();

        group.MapPost("/window-markers/clear", (IScreenProvider screenProvider, AppConfig appConfig) =>
        {
            if (!CheckTestingEnabled(appConfig))
                return Results.Problem(TestingDisabledErrorMsg);

            try
            {
                screenProvider.ClearMarkers();
                return Results.Ok(new { cleared = true });
            }
            catch (Exception ex)
            {
                return Results.Problem(ex.Message);
            }
        })
        .DisableAntiforgery();

        // Runs the full coordinate-prompt loop against a screenshot and returns every intermediate step for debugging.
        group.MapPost("/coordinate-prompt", async (
            TestCoordinatePromptRequest req,
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

                AiProviderType? providerType = ResolveProviderType(req.Provider, appConfig);
                if (providerType is null)
                    return Results.Problem("No active AI provider is configured. Select one in Configuration first.");

                string? resolvedModel = string.IsNullOrWhiteSpace(req.Model)
                    ? GetConfiguredModel(providerType.Value, appConfig)
                    : req.Model;

                IAiProvider provider = CreateProvider(providerType.Value, req.ApiKey, resolvedModel, appConfig, httpClientFactory, loggerFactory);
                CoordinatePrompter prompter = new CoordinatePrompter(provider, appConfig);

                Screenshot screenshot = req.Screenshot!; // Origin (0, 0) — client has no virtual-desktop context
                CoordinateMode? coordinateMode = Enum.TryParse<CoordinateMode>(req.Mode, ignoreCase: true, out CoordinateMode parsedMode)
                    ? parsedMode
                    : null;

                List<object> steps = new List<object>();

                (ScreenCoordinate result, TokenUsage usage) = await prompter.GetCoordinatesForItemAsync(
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

                return Results.Ok(new { Steps = steps, FinalScreenX = result.AbsoluteX, FinalScreenY = result.AbsoluteY, FinalNormX = result.NormalizedX, FinalNormY = result.NormalizedY, TokenUsage = usage });
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

        // Only populated for API-key override sessions; null means use the current config's key for this provider.
        public string? OverrideApiKey { get; init; }
        public AiProviderType OverrideProviderType { get; init; }
        public string? OverrideModel { get; init; }

        public bool IsApiKeyMode => OverrideApiKey is not null;
    }

    /// <summary>
    /// Creates a throwaway <see cref="IAiProvider"/> using the requested or configured provider settings.
    /// Model falls back to the corresponding entry in <paramref name="baseConfig"/> when the caller omits it.
    /// </summary>
    private static AiProviderType? ResolveProviderType(AiProviderType? requestedProvider, AppConfig appConfig)
        => requestedProvider ?? appConfig.General.ActiveProvider;

    private static string? GetConfiguredModel(AiProviderType providerType, AppConfig baseConfig)
        => providerType switch
        {
            AiProviderType.Gemini => baseConfig.Gemini.Model,
            AiProviderType.ChatGPT => baseConfig.OpenAI.Model,
            AiProviderType.OpenAICompatible => baseConfig.OpenAICompatible.Model,
            AiProviderType.Claude => baseConfig.Anthropic.Model,
            AiProviderType.Onnx => baseConfig.Onnx.Model,
            _ => throw new InvalidOperationException($"Unsupported AI provider: {providerType}.")
        };

    private static IAiProvider CreateProvider(
        AiProviderType providerType, string? apiKey, string? model,
        AppConfig baseConfig, IHttpClientFactory httpClientFactory, ILoggerFactory loggerFactory)
    {
        HttpClient httpClient = httpClientFactory.CreateClient();
        return providerType switch
        {
            AiProviderType.Gemini => new GeminiProvider(
                httpClient,
                new AppConfig
                {
                    Gemini = ConfigObjectCloner.Clone(baseConfig.Gemini, config =>
                    {
                        if (!string.IsNullOrWhiteSpace(apiKey)) config.ApiKey = apiKey;
                        config.Model = string.IsNullOrWhiteSpace(model) ? baseConfig.Gemini.Model : model!;
                    }),
                    General = ConfigObjectCloner.Clone(baseConfig.General, config => config.ActiveProvider = AiProviderType.Gemini),
                    Agent = baseConfig.Agent,
                },
                loggerFactory.CreateLogger<GeminiProvider>()),
            AiProviderType.ChatGPT => new OpenAIProvider(
                httpClient,
                new AppConfig
                {
                    OpenAI = ConfigObjectCloner.Clone(baseConfig.OpenAI, config =>
                    {
                        if (!string.IsNullOrWhiteSpace(apiKey)) config.ApiKey = apiKey;
                        config.Model = string.IsNullOrWhiteSpace(model) ? baseConfig.OpenAI.Model : model!;
                    }),
                    OpenAICompatible = ConfigObjectCloner.Clone(baseConfig.OpenAICompatible),
                    General = ConfigObjectCloner.Clone(baseConfig.General, config => config.ActiveProvider = AiProviderType.ChatGPT),
                    Agent = baseConfig.Agent,
                },
                loggerFactory.CreateLogger<OpenAIProvider>()),
            AiProviderType.OpenAICompatible => new OpenAIProvider(
                httpClient,
                new AppConfig
                {
                    OpenAI = ConfigObjectCloner.Clone(baseConfig.OpenAI),
                    OpenAICompatible = ConfigObjectCloner.Clone(baseConfig.OpenAICompatible, config =>
                    {
                        if (!string.IsNullOrWhiteSpace(apiKey)) config.ApiKey = apiKey;
                        config.Model = string.IsNullOrWhiteSpace(model) ? baseConfig.OpenAICompatible.Model : model!;
                    }),
                    General = ConfigObjectCloner.Clone(baseConfig.General, config => config.ActiveProvider = AiProviderType.OpenAICompatible),
                    Agent = baseConfig.Agent,
                },
                loggerFactory.CreateLogger<OpenAIProvider>()),
            AiProviderType.Claude => new AnthropicProvider(
                httpClient,
                new AppConfig
                {
                    Anthropic = ConfigObjectCloner.Clone(baseConfig.Anthropic, config =>
                    {
                        if (!string.IsNullOrWhiteSpace(apiKey)) config.ApiKey = apiKey;
                        config.Model = string.IsNullOrWhiteSpace(model) ? baseConfig.Anthropic.Model : model!;
                    }),
                    General = ConfigObjectCloner.Clone(baseConfig.General, config => config.ActiveProvider = AiProviderType.Claude),
                    Agent = baseConfig.Agent,
                },
                loggerFactory.CreateLogger<AnthropicProvider>()),
            AiProviderType.Onnx => new OnnxProvider(
                new AppConfig
                {
                    Onnx = ConfigObjectCloner.Clone(baseConfig.Onnx, config =>
                    {
                        config.Model = string.IsNullOrWhiteSpace(model) ? baseConfig.Onnx.Model : model!;
                    }),
                    General = ConfigObjectCloner.Clone(baseConfig.General, config => config.ActiveProvider = AiProviderType.Onnx),
                    Agent = baseConfig.Agent,
                },
                loggerFactory.CreateLogger<OnnxProvider>()),
            _ => throw new InvalidOperationException($"Unsupported AI provider: {providerType}.")
        };
    }

    private static MonitorInfo ResolveMarkerMonitor(IScreenProvider screenProvider, int? requestedIndex)
    {
        IReadOnlyList<MonitorInfo> monitors = screenProvider.GetMonitors();
        if (monitors.Count == 0)
            throw new InvalidOperationException("No monitors were reported by the current screen provider.");

        int targetIndex = requestedIndex ?? 0;
        MonitorInfo? selected = monitors.FirstOrDefault(m => m.Index == targetIndex);
        if (selected is null)
            throw new InvalidOperationException($"Monitor {targetIndex} was not found. Available monitor indices: {string.Join(", ", monitors.Select(m => m.Index))}.");

        return selected;
    }

    private static TestMarkerKind ParseMarkerKind(string? rawKind)
        => string.IsNullOrWhiteSpace(rawKind)
            ? TestMarkerKind.AllTypes
            : Enum.TryParse<TestMarkerKind>(rawKind, ignoreCase: true, out TestMarkerKind parsed)
                ? parsed
                : throw new InvalidOperationException($"Unknown marker kind '{rawKind}'.");

    private static IReadOnlyList<TestMarkerDescriptor> BuildMarkerDescriptors(TestMarkerKind scenario, MonitorInfo monitor, int count, bool includeLabels)
    {
        TestMarkerKind[] cycle = scenario == TestMarkerKind.AllTypes
            ? [TestMarkerKind.ClickPoint, TestMarkerKind.MouseMove, TestMarkerKind.MouseMoveArrow, TestMarkerKind.ClickDrag, TestMarkerKind.BoundingBox]
            : [scenario];

        int columns = Math.Max(1, (int)Math.Ceiling(Math.Sqrt(count)));
        int rows = Math.Max(1, (int)Math.Ceiling(count / (double)columns));
        int paddingX = Math.Clamp(monitor.Width / 12, 48, 180);
        int paddingY = Math.Clamp(monitor.Height / 10, 48, 140);
        double usableWidth = Math.Max(1, monitor.Width - (paddingX * 2));
        double usableHeight = Math.Max(1, monitor.Height - (paddingY * 2));
        double cellWidth = usableWidth / columns;
        double cellHeight = usableHeight / rows;

        List<TestMarkerDescriptor> descriptors = new(count);

        for (int index = 0; index < count; index++)
        {
            TestMarkerKind kind = cycle[index % cycle.Length];
            int column = index % columns;
            int row = index / columns;

            int centerX = monitor.X + paddingX + (int)Math.Round(cellWidth * (column + 0.5));
            int centerY = monitor.Y + paddingY + (int)Math.Round(cellHeight * (row + 0.5));

            int dragDx = Math.Clamp((int)Math.Round(cellWidth * 0.34), 70, 220);
            int dragDy = Math.Clamp((int)Math.Round(cellHeight * 0.28), 50, 170);
            int boxHalfWidth = Math.Clamp((int)Math.Round(cellWidth * 0.24), 55, 180);
            int boxHalfHeight = Math.Clamp((int)Math.Round(cellHeight * 0.18), 36, 120);
            int minX = monitor.X + 8;
            int maxX = monitor.X + monitor.Width - 8;
            int minY = monitor.Y + 8;
            int maxY = monitor.Y + monitor.Height - 8;
            string? label = includeLabels ? BuildMarkerLabel(kind, index + 1) : null;
            string queueLabel = (index + 1).ToString();

            TestMarkerDescriptor descriptor = kind switch
            {
                TestMarkerKind.ClickPoint => new TestMarkerDescriptor(kind,
                    ClampToRange(centerX, minX, maxX),
                    ClampToRange(centerY, minY, maxY),
                    null,
                    null,
                    label,
                    queueLabel),

                TestMarkerKind.MouseMove => new TestMarkerDescriptor(kind,
                    ClampToRange(centerX, minX, maxX),
                    ClampToRange(centerY, minY, maxY),
                    null,
                    null,
                    label,
                    queueLabel),

                TestMarkerKind.MouseMoveArrow => new TestMarkerDescriptor(kind,
                    ClampToRange(centerX, minX, maxX),
                    ClampToRange(centerY, minY, maxY),
                    null,
                    null,
                    label,
                    queueLabel),

                TestMarkerKind.ClickDrag => new TestMarkerDescriptor(kind,
                    ClampToRange(centerX - (dragDx / 2), minX, maxX),
                    ClampToRange(centerY - (dragDy / 2), minY, maxY),
                    ClampToRange(centerX + (dragDx / 2), minX, maxX),
                    ClampToRange(centerY + (dragDy / 2), minY, maxY),
                    label,
                    queueLabel),

                TestMarkerKind.BoundingBox => new TestMarkerDescriptor(kind,
                    ClampToRange(centerX - boxHalfWidth, minX, maxX),
                    ClampToRange(centerY - boxHalfHeight, minY, maxY),
                    ClampToRange(centerX + boxHalfWidth, minX, maxX),
                    ClampToRange(centerY + boxHalfHeight, minY, maxY),
                    label,
                    queueLabel),

                _ => throw new InvalidOperationException($"Unsupported marker kind: {kind}.")
            };

            descriptors.Add(descriptor);
        }

        return descriptors;
    }

    private static int ClampToRange(int value, int min, int max)
        => max < min ? min : Math.Clamp(value, min, max);

    private static string BuildMarkerLabel(TestMarkerKind kind, int order)
        => kind switch
        {
            TestMarkerKind.ClickPoint => $"Click {order}",
            TestMarkerKind.MouseMove => $"Move {order}",
            TestMarkerKind.MouseMoveArrow => $"Arrow {order}",
            TestMarkerKind.ClickDrag => $"Drag {order}",
            TestMarkerKind.BoundingBox => $"Box {order}",
            TestMarkerKind.AllTypes => $"Marker {order}",
            _ => $"Marker {order}"
        };
}

// Scoped to this file — it's a transport detail for the test endpoint, not a domain type.
file record TestChatRequest(string? Prompt, string? ApiKey, string? Model, AiProviderType? Provider, string? ImageBase64, string? ImageMimeType, string? ConversationId);
internal enum TestMarkerKind
{
    ClickPoint,
    MouseMove,
    MouseMoveArrow,
    ClickDrag,
    BoundingBox,
    AllTypes,
}

internal sealed record TestMarkerDescriptor(TestMarkerKind Kind, int X1, int Y1, int? X2, int? Y2, string? Label, string QueueLabel);

internal sealed record TestMarkerDrawRequest(
    string? MarkerKind = null,
    int? MonitorIndex = null,
    int Count = 6,
    int DurationMs = 4000,
    int MarkerOpacity = 255,
    int FillOpacity = 48,
    bool IncludeLabels = true,
    bool IncludeQueueNumbers = false,
    bool ClearExisting = true);

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