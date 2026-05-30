using System.Reflection;
using System.Text.Json;
using Thio_Universal_Agent.AI_API.Onnx;
using Thio_Universal_Agent.Handlers;

namespace Thio_Universal_Agent.Endpoints;

/// <summary>
/// Exposes application configuration to the web UI.
/// <list type="bullet">
///   <item><description><c>GET  /api/config</c>        — flat snapshot (used by Agent.html)</description></item>
///   <item><description><c>GET  /api/config/schema</c> — rich metadata + values for the config page UI</description></item>
///   <item><description><c>POST /api/config</c>        — apply a <c>{ section: { key: value } }</c> payload to AppConfig</description></item>
/// </list>
/// </summary>
internal static class ConfigEndpoints
{
    private static readonly JsonNamingPolicy CamelCase = JsonNamingPolicy.CamelCase;

    internal static void MapConfigEndpoints(this WebApplication app)
    {
        // Endpoint for whether this entire build is human control only mode. Return null if not to not be confused with human mode only mode being off
        #if HUMAN_ONLY
        app.MapGet("/api/HumanOnlyBuild", () => true);
        #else
        app.MapGet("/api/HumanOnlyBuild", () => Results.Ok<bool?>(null));
        #endif

        // Flat get, might update at some point in the future
        app.MapGet("/api/config", (AppConfig appConfig) =>
        {
            AppConfigResponse response = new AppConfigResponse(
                General: new GeneralConfigDto(
                        ActiveProvider:     appConfig.General.ActiveProvider?.ToString(),
                        SettleDelayMs:      appConfig.General.SettleDelayMs,
                        QueueSettleDelayMs: appConfig.General.QueueSettleDelayMs,
                        EnableContextReset: appConfig.General.EnableContextReset,
                        StripHistoryImages: appConfig.General.StripHistoryImages,
                        EnableDebugMode:    appConfig.General.EnableDebugMode,
                        MaxQueueSize:       appConfig.General.MaxQueueSize,
                    #if HUMAN_ONLY
                        HumanControlOnlyMode: true
                    #else
                        HumanControlOnlyMode: appConfig.General.HumanControlOnlyMode
                    #endif
                    ),
                Agent: new AgentConfigDto(
                        CoordinateMode: appConfig.Agent.CoordinateMode.ToString(),
                        MonitorIndex:   appConfig.Agent.MonitorIndex
                    ),
                Gemini: new GeminiConfigDto(
                    Model:                     appConfig.Gemini.Model,
                    MediaResolution:           appConfig.Gemini.MediaResolution.ToString(),
                    Temperature:               appConfig.Gemini.Temperature,
                    TopP:                      appConfig.Gemini.TopP,
                    TopK:                      appConfig.Gemini.TopK,
                    CoordinateMaxOutputTokens: appConfig.Gemini.CoordinateMaxOutputTokens,
                    ThinkingBudget:            appConfig.Gemini.ThinkingBudget,
                    ThinkingLevel:             appConfig.Gemini.ThinkingLevel?.ToString(),
                    InputPricePerMillionTokens:        appConfig.Gemini.InputPricePerMillionTokens,
                    OutputPricePerMillionTokens:       appConfig.Gemini.OutputPricePerMillionTokens,
                    CachedInputPricePerMillionTokens:  appConfig.Gemini.CachedInputPricePerMillionTokens
                ),
                OpenAI: new OpenAIConfigDto(
                    Model:           appConfig.OpenAI.Model,
                    Temperature:     appConfig.OpenAI.Temperature,
                    MaxOutputTokens: appConfig.OpenAI.MaxOutputTokens,
                    InputPricePerMillionTokens:        appConfig.OpenAI.InputPricePerMillionTokens,
                    OutputPricePerMillionTokens:       appConfig.OpenAI.OutputPricePerMillionTokens,
                    CachedInputPricePerMillionTokens:  appConfig.OpenAI.CachedInputPricePerMillionTokens
                ),
                OpenAICompatible: new OpenAICompatibleConfigDto(
                    Model:           appConfig.OpenAICompatible.Model,
                    EndpointUrl:     appConfig.OpenAICompatible.EndpointUrl,
                    Temperature:     appConfig.OpenAICompatible.Temperature,
                    MaxOutputTokens: appConfig.OpenAICompatible.MaxOutputTokens,
                    InputPricePerMillionTokens:        appConfig.OpenAICompatible.InputPricePerMillionTokens,
                    OutputPricePerMillionTokens:       appConfig.OpenAICompatible.OutputPricePerMillionTokens,
                    CachedInputPricePerMillionTokens:  appConfig.OpenAICompatible.CachedInputPricePerMillionTokens
                ),
                Anthropic: new AnthropicConfigDto(
                    Model:           appConfig.Anthropic.Model,
                    Temperature:     appConfig.Anthropic.Temperature,
                    MaxOutputTokens: appConfig.Anthropic.MaxOutputTokens,
                    InputPricePerMillionTokens:        appConfig.Anthropic.InputPricePerMillionTokens,
                    OutputPricePerMillionTokens:       appConfig.Anthropic.OutputPricePerMillionTokens,
                    CachedInputPricePerMillionTokens:  appConfig.Anthropic.CachedInputPricePerMillionTokens
                ),
                Onnx: new OnnxConfigDto(
                    Model:           appConfig.Onnx.Model,
                    ExecutionProvider: appConfig.Onnx.ExecutionProvider.ToString(),
                    DeviceId:        appConfig.Onnx.DeviceId,
                    UseSampling:     appConfig.Onnx.UseSampling,
                    Temperature:     appConfig.Onnx.Temperature,
                    TopP:            appConfig.Onnx.TopP,
                    TopK:            appConfig.Onnx.TopK,
                    MaxOutputTokens: appConfig.Onnx.MaxOutputTokens,
                    ModelTypeOverride: appConfig.Onnx.ModelTypeOverride
                ),
                Hotkeys: new HotkeyConfigDto(
                    Enabled:            appConfig.Hotkeys.Enabled,
                    PauseResumeHotkey:  appConfig.Hotkeys.PauseResumeHotkey,
                    StopHotkey:         appConfig.Hotkeys.StopHotkey
                )
            );

            return Results.Ok(response);
        });

        app.MapGet("/api/config/onnx/capabilities", () => Results.Ok(OnnxRuntimeCapabilities.GetSnapshot()));

        // ── Schema endpoint ───────────────────────────────────────────────────

        app.MapGet("/api/config/schema", (AppConfig appConfig) =>
        {
            object[] sections = new object[]
            {
                BuildSection("general",   "General",   appConfig.General,   isProvider: false),
                BuildSection("gemini",    "Gemini",    appConfig.Gemini,    isProvider: true),
                BuildSection("openai",    "ChatGPT",    appConfig.OpenAI,    isProvider: true),
                BuildSection("openaiCompatible", "OpenAI-Compatible", appConfig.OpenAICompatible, isProvider: true),
                BuildSection("anthropic", "Claude", appConfig.Anthropic, isProvider: true),
                BuildSection("onnx", "Local ONNX", appConfig.Onnx, isProvider: true),
                BuildSection("agent",     "Agent",     appConfig.Agent,     isProvider: false),
                BuildSection("hotkeys",   "Hotkeys",   appConfig.Hotkeys,   isProvider: false),
            };
            return Results.Ok(new { sections });
        });

        // ── Update endpoint ───────────────────────────────────────────────────

        app.MapPost("/api/config", (JsonElement body, AppConfig appConfig, HotkeyService? hotkeyService) =>
        {
            if (body.TryGetProperty("general", out JsonElement generalEl)) ApplyUpdates(appConfig.General, generalEl);
            if (body.TryGetProperty("gemini", out JsonElement geminiEl)) ApplyUpdates(appConfig.Gemini, geminiEl);
            if (body.TryGetProperty("openai", out JsonElement openaiEl)) ApplyUpdates(appConfig.OpenAI, openaiEl);
            if (body.TryGetProperty("openaiCompatible", out JsonElement openaiCompatibleEl)) ApplyUpdates(appConfig.OpenAICompatible, openaiCompatibleEl);
            if (body.TryGetProperty("anthropic", out JsonElement anthropicEl)) ApplyUpdates(appConfig.Anthropic, anthropicEl);
            if (body.TryGetProperty("onnx", out JsonElement onnxEl)) ApplyUpdates(appConfig.Onnx, onnxEl);
            if (body.TryGetProperty("agent", out JsonElement agentEl)) ApplyUpdates(appConfig.Agent, agentEl);
            if (body.TryGetProperty("hotkeys", out JsonElement hotkeysEl))
            {
                ApplyUpdates(appConfig.Hotkeys, hotkeysEl);
                hotkeyService?.ReloadHotkeys();
            }
            return Results.Ok();
        });
    }

    // ── Reflection helpers ────────────────────────────────────────────────────

    /// <summary>Builds a schema section object by reflecting over <see cref="ConfigFieldAttribute"/>-annotated properties.</summary>
    private static object BuildSection(string key, string label, object obj, bool isProvider)
    {
        List<object> fields = new List<object>();

        foreach (PropertyInfo prop in obj.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            ConfigFieldAttribute? attr = prop.GetCustomAttribute<ConfigFieldAttribute>();
            if (attr is null) continue;

            Type propType   = prop.PropertyType;
            Type underlying = Nullable.GetUnderlyingType(propType) ?? propType;
            bool nullable  = Nullable.GetUnderlyingType(propType) != null
                             || (propType == typeof(string)); // string is a reference type

            string fieldType;
            string[]? options = null;

            if (underlying == typeof(string)) fieldType = attr.IsPassword ? "password" : attr.IsPromptTemplate ? "prompt-template" : "string";
            else if (underlying == typeof(int)) fieldType = "int";
            else if (underlying == typeof(float)) fieldType = "float";
            else if (underlying == typeof(double)) fieldType = "float";
            else if (underlying == typeof(bool)) fieldType = "bool";
            else if (underlying.IsEnum)
            {
                fieldType = "enum";
                options = Enum.GetNames(underlying);
            }
            else
            {
                fieldType = "string";
            }

            object? raw   = prop.GetValue(obj);
            object? value = raw is Enum e ? e.ToString() : raw;

            string? defaultTemplate = fieldType == "prompt-template"
                ? Handlers.AgentPromptBuilder.DefaultSystemPromptTemplate
                : null;

            fields.Add(new
            {
                key = CamelCase.ConvertName(prop.Name),
                label = attr.Label,
                type = fieldType,
                description = attr.Description,
                nullable,
                value,
                options,
                defaultTemplate,
            });
        }

        return new { key, label, isProvider, fields };
    }

    /// <summary>
    /// Applies a JSON object of <c>camelCaseKey → value</c> updates to <paramref name="target"/>
    /// using reflection, with automatic type coercion.
    /// </summary>
    private static void ApplyUpdates(object target, JsonElement updates)
    {
        if (updates.ValueKind != JsonValueKind.Object) return;

        foreach (JsonProperty prop in updates.EnumerateObject())
        {
            PropertyInfo? pi = target.GetType().GetProperty(
                prop.Name, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
            if (pi is null || !pi.CanWrite) continue;

            Type propType   = pi.PropertyType;
            Type underlying = Nullable.GetUnderlyingType(propType) ?? propType;

            try
            {
                if (prop.Value.ValueKind == JsonValueKind.Null)
                {
                    pi.SetValue(target, null);
                    continue;
                }

                object? converted = null;
                if (underlying == typeof(string)) converted = prop.Value.GetString();
                else if (underlying == typeof(int)) converted = prop.Value.GetInt32();
                else if (underlying == typeof(float)) converted = (float)prop.Value.GetDouble();
                else if (underlying == typeof(double)) converted = prop.Value.GetDouble();
                else if (underlying == typeof(bool)) converted = prop.Value.GetBoolean();
                else if (underlying.IsEnum && prop.Value.ValueKind == JsonValueKind.String)
                    converted = Enum.Parse(underlying, prop.Value.GetString()!, ignoreCase: true);

                if (converted is not null || Nullable.GetUnderlyingType(propType) != null)
                    pi.SetValue(target, converted);
            }
            catch { /* skip individual invalid values */ }
        }
    }
}

// ── DTOs (existing flat GET) ──────────────────────────────────────────────────

internal sealed record AppConfigResponse(
    GeneralConfigDto General,
    AgentConfigDto Agent,
    GeminiConfigDto Gemini,
    OpenAIConfigDto OpenAI,
    OpenAICompatibleConfigDto OpenAICompatible,
    AnthropicConfigDto Anthropic,
    OnnxConfigDto Onnx,
    HotkeyConfigDto Hotkeys
);

internal sealed record GeneralConfigDto(
    string? ActiveProvider,
    int SettleDelayMs,
    int QueueSettleDelayMs,
    bool EnableContextReset,
    bool StripHistoryImages,
    bool EnableDebugMode,
    int MaxQueueSize,
    bool HumanControlOnlyMode
);

internal sealed record AgentConfigDto(
    string? CoordinateMode,
    int? MonitorIndex
);

internal sealed record GeminiConfigDto(
    string? Model,
    string? MediaResolution,
    double? Temperature,
    double? TopP,
    int? TopK,
    int? CoordinateMaxOutputTokens,
    int? ThinkingBudget,
    string? ThinkingLevel,
    double? InputPricePerMillionTokens,
    double? OutputPricePerMillionTokens,
    double? CachedInputPricePerMillionTokens
);

internal sealed record HotkeyConfigDto(
    bool Enabled,
    string PauseResumeHotkey,
    string StopHotkey
);

internal sealed record OpenAIConfigDto(
    string? Model,
    double? Temperature,
    int? MaxOutputTokens,
    double? InputPricePerMillionTokens,
    double? OutputPricePerMillionTokens,
    double? CachedInputPricePerMillionTokens
);

internal sealed record OpenAICompatibleConfigDto(
    string? Model,
    string? EndpointUrl,
    double? Temperature,
    int? MaxOutputTokens,
    double? InputPricePerMillionTokens,
    double? OutputPricePerMillionTokens,
    double? CachedInputPricePerMillionTokens
);

internal sealed record AnthropicConfigDto(
    string? Model,
    double? Temperature,
    int? MaxOutputTokens,
    double? InputPricePerMillionTokens,
    double? OutputPricePerMillionTokens,
    double? CachedInputPricePerMillionTokens
);

internal sealed record OnnxConfigDto(
    string? Model,
    string? ExecutionProvider,
    int? DeviceId,
    bool UseSampling,
    double? Temperature,
    double? TopP,
    int? TopK,
    int? MaxOutputTokens,
    string? ModelTypeOverride
);
