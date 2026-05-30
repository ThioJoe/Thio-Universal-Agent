using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using Microsoft.ML.OnnxRuntimeGenAI;

namespace Thio_Universal_Agent.AI_API.Onnx;

/// <summary>
/// Local ONNX Runtime GenAI implementation of <see cref="IAiProvider"/>.
/// Expects a model folder containing <c>genai_config.json</c> and tokenizer assets.
/// </summary>
public sealed class OnnxProvider(AppConfig appConfig, ILogger<OnnxProvider> logger) : IAiProvider
{
    private const int ImageTokenPadding = 2048;

    private static readonly OgaHandle OgaRuntime = new();
    private static readonly ConcurrentDictionary<string, Lazy<CachedModel>> ModelCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public Task<AiResponse> SendPromptAsync(string prompt, CancellationToken cancellationToken = default, AiRequestOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prompt);

        return GenerateAsync(
            conversation: new AiConversation(),
            additionalMessage: new AiChatMessage { Role = AiChatRole.User, Text = prompt },
            options: options,
            cancellationToken: cancellationToken);
    }

    public Task<AiResponse> SendPromptWithImageAsync(string prompt, byte[] imageBytes, string mimeType = "image/png", CancellationToken cancellationToken = default, AiRequestOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prompt);
        ArgumentNullException.ThrowIfNull(imageBytes);

        return GenerateAsync(
            conversation: new AiConversation(),
            additionalMessage: new AiChatMessage { Role = AiChatRole.User, Text = prompt, ImageBytes = imageBytes, MimeType = mimeType },
            options: options,
            cancellationToken: cancellationToken);
    }

    public async Task<(AiConversation Conversation, AiResponse Response)> StartConversationAsync(string prompt, CancellationToken cancellationToken = default, AiRequestOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prompt);

        AiConversation conversation = new AiConversation();
        AiChatMessage userMessage = new AiChatMessage { Role = AiChatRole.User, Text = prompt };

        AiResponse response = await GenerateAsync(conversation, userMessage, options, cancellationToken).ConfigureAwait(false);
        if (response.Success)
        {
            conversation.AddMessage(userMessage);
            conversation.AddMessage(new AiChatMessage { Role = AiChatRole.Model, Text = response.Text });
        }

        return (conversation, response);
    }

    public Task<AiResponse> ContinueConversationAsync(AiConversation conversation, string prompt, CancellationToken cancellationToken = default, AiRequestOptions? options = null)
        => ContinueConversationCoreAsync(
            conversation,
            new AiChatMessage { Role = AiChatRole.User, Text = prompt },
            cancellationToken,
            options);

    public Task<AiResponse> ContinueConversationAsync(AiConversation conversation, byte[] imageBytes, string mimeType = "image/png", CancellationToken cancellationToken = default, AiRequestOptions? options = null)
        => ContinueConversationCoreAsync(
            conversation,
            new AiChatMessage { Role = AiChatRole.User, ImageBytes = imageBytes, MimeType = mimeType },
            cancellationToken,
            options);

    public Task<AiResponse> ContinueConversationAsync(AiConversation conversation, string prompt, byte[] imageBytes, string mimeType = "image/png", CancellationToken cancellationToken = default, AiRequestOptions? options = null)
        => ContinueConversationCoreAsync(
            conversation,
            new AiChatMessage { Role = AiChatRole.User, Text = prompt, ImageBytes = imageBytes, MimeType = mimeType },
            cancellationToken,
            options);

    private async Task<AiResponse> ContinueConversationCoreAsync(AiConversation conversation, AiChatMessage userMessage, CancellationToken cancellationToken, AiRequestOptions? options)
    {
        AiResponse response = await GenerateAsync(conversation, userMessage, options, cancellationToken).ConfigureAwait(false);
        if (response.Success)
        {
            conversation.AddMessage(userMessage);
            conversation.AddMessage(new AiChatMessage { Role = AiChatRole.Model, Text = response.Text });
        }

        return response;
    }

    private Task<AiResponse> GenerateAsync(AiConversation conversation, AiChatMessage additionalMessage, AiRequestOptions? options, CancellationToken cancellationToken)
        => Task.Run(() => GenerateCore(conversation, additionalMessage, options, cancellationToken), cancellationToken);

    private AiResponse GenerateCore(AiConversation conversation, AiChatMessage additionalMessage, AiRequestOptions? options, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        string configuredModelPath = appConfig.Onnx.Model?.Trim().Trim('"').Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(configuredModelPath))
            return Fail("Onnx:Model is not configured. Point it at a local ONNX Runtime GenAI model folder via the web UI.");

        string modelPath;
        try
        {
            modelPath = Path.GetFullPath(configuredModelPath);
        }
        catch (Exception ex)
        {
            return Fail($"Onnx:Model path is invalid. {ex.Message}");
        }

        if (!Directory.Exists(modelPath))
            return Fail($"ONNX model folder not found: {modelPath}");

        if (!File.Exists(Path.Combine(modelPath, "genai_config.json")))
            return Fail($"The ONNX model folder must contain genai_config.json: {modelPath}");

        try
        {
            CachedModel cachedModel = GetOrCreateModel(modelPath, appConfig.Onnx);
            string modelType = string.IsNullOrWhiteSpace(appConfig.Onnx.ModelTypeOverride)
                ? cachedModel.ModelType
                : appConfig.Onnx.ModelTypeOverride!.Trim();

            List<Dictionary<string, object?>> messages = BuildMessages(modelType, conversation.Messages, additionalMessage);
            string prompt = BuildPrompt(cachedModel, messages, additionalMessage, modelType);

            using Tokenizer tokenizer = new Tokenizer(cachedModel.Model);
            using TokenizerStream tokenizerStream = tokenizer.CreateStream();

            int maxOutputTokens = options?.MaxOutputTokens ?? appConfig.Onnx.MaxOutputTokens ?? 1024;
            bool hasImage = additionalMessage.ImageBytes is { Length: > 0 };

            if (hasImage)
            {
                using MultiModalProcessor processor = CreateProcessor(cachedModel);
                using GeneratorParams generatorParams = new GeneratorParams(cachedModel.Model);

                int estimatedPromptTokens = EstimatePromptTokens(tokenizer, prompt, hasImage);
                ConfigureSearchOptions(generatorParams, estimatedPromptTokens, maxOutputTokens);

                using Generator generator = new Generator(cachedModel.Model, generatorParams);
                using Images images = Images.Load(additionalMessage.ImageBytes!);
                using NamedTensors inputTensors = processor.ProcessImages(prompt, images);

                generator.SetInputs(inputTensors);
                int promptTokens = (int)generator.TokenCount();
                string responseText = GenerateText(generator, tokenizerStream, cancellationToken, out int completionTokens);
                int totalTokens = (int)generator.TokenCount();

                if (string.IsNullOrWhiteSpace(responseText))
                    return Fail("Local ONNX model returned an empty response.");

                return new AiResponse(
                    Success: true,
                    Text: responseText,
                    Usage: new TokenUsage(promptTokens, completionTokens, totalTokens));
            }

            using Sequences sequences = tokenizer.Encode(prompt);
            int estimatedTextPromptTokens = sequences.NumSequences > 0 ? sequences[0].Length : 0;

            using GeneratorParams textGeneratorParams = new GeneratorParams(cachedModel.Model);
            ConfigureSearchOptions(textGeneratorParams, estimatedTextPromptTokens, maxOutputTokens);

            using Generator textGenerator = new Generator(cachedModel.Model, textGeneratorParams);
            textGenerator.AppendTokenSequences(sequences);
            int textPromptTokens = (int)textGenerator.TokenCount();
            string textResponse = GenerateText(textGenerator, tokenizerStream, cancellationToken, out int textCompletionTokens);
            int textTotalTokens = (int)textGenerator.TokenCount();

            if (string.IsNullOrWhiteSpace(textResponse))
                return Fail("Local ONNX model returned an empty response.");

            return new AiResponse(
                Success: true,
                Text: textResponse,
                Usage: new TokenUsage(textPromptTokens, textCompletionTokens, textTotalTokens));
        }
        catch (DllNotFoundException ex)
        {
            logger.LogError(ex, "Local ONNX DLL failed to load. Likely missing Microsoft Visual C++ Redistributable.");
            return Fail($"ONNX Runtime native libraries missing. Please install the Microsoft Visual C++ Redistributable. Details: {ex.Message}");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Local ONNX generation failed for model folder {ModelPath}.", modelPath);
            return Fail(ex.Message);
        }
    }

    private CachedModel GetOrCreateModel(string modelPath, OnnxConfig config)
    {
        string provider = NormalizeExecutionProvider(config.ExecutionProvider) ?? "follow_config";
        string key = $"{modelPath}|{provider}|{config.DeviceId}";

        Lazy<CachedModel> lazy = ModelCache.GetOrAdd(
            key,
            _ => new Lazy<CachedModel>(
                () => LoadModel(modelPath, config),
                LazyThreadSafetyMode.ExecutionAndPublication));

        try
        {
            return lazy.Value;
        }
        catch
        {
            ModelCache.TryRemove(key, out _);
            throw;
        }
    }

    private CachedModel LoadModel(string modelPath, OnnxConfig config)
    {
        string? provider = NormalizeExecutionProvider(config.ExecutionProvider);

        if (provider is not null && !OnnxRuntimeCapabilities.IsProviderAvailable(provider, out string? availabilityDetail))
        {
            throw new InvalidOperationException(
                $"Execution provider '{provider}' is not reported by the installed ONNX Runtime. {availabilityDetail}");
        }

        using Config modelConfig = new Config(modelPath);

        if (provider is not null)
        {
            modelConfig.ClearProviders();

            if (!provider.Equals("cpu", StringComparison.OrdinalIgnoreCase))
            {
                modelConfig.AppendProvider(provider);
            }

            if (config.DeviceId is int deviceId)
            {
                modelConfig.SetProviderOption(provider, "device_id", deviceId.ToString());
            }
        }

        Model model;
        try
        {
            model = new Model(modelConfig);
        }
        catch (OnnxRuntimeGenAIException ex) when (provider is not null
            && ex.Message.Contains("Specified provider is not supported", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"The ONNX Runtime GenAI build rejected execution provider '{provider}'. {OnnxRuntimeCapabilities.GetProviderAvailabilityMessage(provider)}",
                ex);
        }

        string modelType = model.GetModelType();
        string chatTemplate = TryLoadChatTemplate(modelPath);

        return new CachedModel(modelPath, model, modelType, chatTemplate);
    }

    private static string TryLoadChatTemplate(string modelPath)
    {
        string templatePath = Path.Combine(modelPath, "chat_template.jinja");
        if (File.Exists(templatePath))
            return File.ReadAllText(templatePath, Encoding.UTF8);

        string tokenizerConfigPath = Path.Combine(modelPath, "tokenizer_config.json");
        if (!File.Exists(tokenizerConfigPath))
            return string.Empty;

        try
        {
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(tokenizerConfigPath, Encoding.UTF8));
            if (document.RootElement.TryGetProperty("chat_template", out JsonElement templateElement)
                && templateElement.ValueKind == JsonValueKind.String)
            {
                return templateElement.GetString() ?? string.Empty;
            }
        }
        catch
        {
            // Ignore malformed tokenizer config and fall back to model-type-specific prompt formatting.
        }

        return string.Empty;
    }

    private static string? NormalizeExecutionProvider(OnnxExecutionProvider provider)
        => provider switch
        {
            OnnxExecutionProvider.FollowConfig => null,
            OnnxExecutionProvider.CPU => "cpu",
            OnnxExecutionProvider.DML => "DML",
            OnnxExecutionProvider.CUDA => "CUDA",
            OnnxExecutionProvider.OpenVINO => "OpenVINO",
            OnnxExecutionProvider.QNN => "QNN",
            OnnxExecutionProvider.WebGPU => "WebGPU",
            OnnxExecutionProvider.NvTensorRtRtx => "NvTensorRtRtx",
            _ => null,
        };

    private static MultiModalProcessor CreateProcessor(CachedModel cachedModel)
    {
        try
        {
            return new MultiModalProcessor(cachedModel.Model);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"The ONNX model '{cachedModel.ModelType}' does not expose a multimodal processor. Use a vision-capable ONNX Runtime GenAI export for screenshot-driven agent tasks.",
                ex);
        }
    }

    private static List<Dictionary<string, object?>> BuildMessages(string modelType, IReadOnlyList<AiChatMessage> history, AiChatMessage additionalMessage)
    {
        List<Dictionary<string, object?>> messages = new(history.Count + 1);

        foreach (AiChatMessage message in history)
        {
            messages.Add(ToSerializableMessage(modelType, message, includeImage: false));
        }

        messages.Add(ToSerializableMessage(modelType, additionalMessage, includeImage: additionalMessage.ImageBytes is { Length: > 0 }));
        return messages;
    }

    private static Dictionary<string, object?> ToSerializableMessage(string modelType, AiChatMessage message, bool includeImage)
    {
        string role = message.Role == AiChatRole.Model ? "assistant" : "user";
        object content;

        if (message.Role == AiChatRole.Model)
        {
            content = message.Text ?? string.Empty;
        }
        else if (includeImage)
        {
            content = BuildUserContent(modelType, 1, message.Text ?? string.Empty);
        }
        else
        {
            content = message.Text ?? string.Empty;
        }

        return new Dictionary<string, object?>
        {
            ["role"] = role,
            ["content"] = content,
        };
    }

    private string BuildPrompt(CachedModel cachedModel, List<Dictionary<string, object?>> messages, AiChatMessage additionalMessage, string modelType)
    {
        if (string.IsNullOrWhiteSpace(cachedModel.ChatTemplate))
            return BuildFallbackPrompt(messages, additionalMessage, modelType);

        try
        {
            using Tokenizer tokenizer = new Tokenizer(cachedModel.Model);
            return tokenizer.ApplyChatTemplate(
                template_str: cachedModel.ChatTemplate,
                messages: JsonSerializer.Serialize(messages, JsonOptions),
                tools: string.Empty,
                add_generation_prompt: true);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Falling back to a plain-text prompt because ApplyChatTemplate failed for ONNX model type {ModelType}.", modelType);
            return BuildFallbackPrompt(messages, additionalMessage, modelType);
        }
    }

    private static string BuildFallbackPrompt(List<Dictionary<string, object?>> messages, AiChatMessage additionalMessage, string modelType)
    {
        string normalizedModelType = modelType.Trim().ToLowerInvariant();
        if (normalizedModelType.StartsWith("phi", StringComparison.Ordinal))
            return BuildPhiPrompt(messages);

        if (messages.Count == 1 && additionalMessage.ImageBytes is { Length: > 0 })
        {
            object singleMessageContent = BuildUserContent(modelType, 1, additionalMessage.Text ?? string.Empty);
            return singleMessageContent as string ?? JsonSerializer.Serialize(singleMessageContent, JsonOptions);
        }

        StringBuilder builder = new StringBuilder();
        foreach (Dictionary<string, object?> message in messages)
        {
            string role = Convert.ToString(message["role"]) ?? "user";
            object? content = message["content"];
            string contentText = content as string ?? JsonSerializer.Serialize(content, JsonOptions);
            builder.Append(role).Append(':').AppendLine();
            builder.AppendLine(contentText);
            builder.AppendLine();
        }

        builder.AppendLine("assistant:");
        return builder.ToString();
    }

    private static string BuildPhiPrompt(List<Dictionary<string, object?>> messages)
    {
        StringBuilder builder = new StringBuilder();

        foreach (Dictionary<string, object?> message in messages)
        {
            string role = Convert.ToString(message["role"]) ?? "user";
            object? content = message["content"];
            string contentText = content as string ?? JsonSerializer.Serialize(content, JsonOptions);

            builder.Append(role switch
            {
                "assistant" => "<|assistant|>\n",
                "system" => "<|system|>\n",
                _ => "<|user|>\n",
            });

            if (!string.IsNullOrEmpty(contentText))
                builder.Append(contentText);

            if (!contentText.EndsWith('\n'))
                builder.Append('\n');

            builder.Append("<|end|>\n");
        }

        builder.Append("<|assistant|>\n");
        return builder.ToString();
    }

    private static object BuildUserContent(string modelType, int imageCount, string prompt)
    {
        if (imageCount <= 0)
            return prompt;

        string normalizedModelType = modelType.Trim().ToLowerInvariant();

        if (normalizedModelType is "phi3v" or "phi4mm")
        {
            StringBuilder imageTags = new StringBuilder();
            for (int i = 0; i < imageCount; i++)
            {
                imageTags.Append("<|image_").Append(i + 1).AppendLine("|>");
            }

            return imageTags.Append(prompt).ToString();
        }

        if (normalizedModelType is "qwen2_5_vl" or "qwen3_vl" or "qwen3_5" or "qwen3_5_moe" or "fara")
        {
            StringBuilder imageTags = new StringBuilder();
            for (int i = 0; i < imageCount; i++)
            {
                imageTags.Append("<|vision_start|><|image_pad|><|vision_end|>");
            }

            return imageTags.Append(prompt).ToString();
        }

        List<Dictionary<string, string>> content = new(imageCount + 1);
        for (int i = 0; i < imageCount; i++)
        {
            content.Add(new Dictionary<string, string>
            {
                ["type"] = "image",
            });
        }

        content.Add(new Dictionary<string, string>
        {
            ["type"] = "text",
            ["text"] = prompt,
        });

        return content;
    }

    private void ConfigureSearchOptions(GeneratorParams generatorParams, int promptTokenEstimate, int maxOutputTokens)
    {
        generatorParams.SetSearchOption("batch_size", 1d);
        generatorParams.SetSearchOption("max_length", Math.Max(promptTokenEstimate + Math.Max(maxOutputTokens, 1), promptTokenEstimate + 1));
        generatorParams.SetSearchOption("do_sample", appConfig.Onnx.UseSampling);

        if (!appConfig.Onnx.UseSampling)
            return;

        if (appConfig.Onnx.Temperature is > 0)
            generatorParams.SetSearchOption("temperature", appConfig.Onnx.Temperature.Value);

        if (appConfig.Onnx.TopP is > 0)
            generatorParams.SetSearchOption("top_p", appConfig.Onnx.TopP.Value);

        if (appConfig.Onnx.TopK is > 0)
            generatorParams.SetSearchOption("top_k", appConfig.Onnx.TopK.Value);
    }

    private static int EstimatePromptTokens(Tokenizer tokenizer, string prompt, bool hasImage)
    {
        using Sequences sequences = tokenizer.Encode(prompt);
        int promptTokens = sequences.NumSequences > 0 ? sequences[0].Length : 0;
        return hasImage ? promptTokens + ImageTokenPadding : promptTokens;
    }

    private static string GenerateText(Generator generator, TokenizerStream tokenizerStream, CancellationToken cancellationToken, out int completionTokens)
    {
        StringBuilder builder = new StringBuilder();
        completionTokens = 0;

        while (!generator.IsDone())
        {
            cancellationToken.ThrowIfCancellationRequested();
            generator.GenerateNextToken();

            if (generator.IsDone())
                break;

            ReadOnlySpan<int> nextTokens = generator.GetNextTokens();
            completionTokens += nextTokens.Length;
            foreach (int token in nextTokens)
            {
                builder.Append(tokenizerStream.Decode(token));
            }
        }

        return builder.ToString().Trim();
    }

    private static AiResponse Fail(string message) => new(false, string.Empty, message);

    private sealed record CachedModel(string ModelPath, Model Model, string ModelType, string ChatTemplate);
}