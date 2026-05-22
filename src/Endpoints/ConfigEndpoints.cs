using System.Reflection;
using System.Text.Json;
using Thio_Universal_Agent;

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
        // ── Existing flat GET (kept for Agent.html compatibility) ──────────────

        app.MapGet("/api/config", (AppConfig appConfig) =>
        {
            var response = new AppConfigResponse(
                Agent: new AgentConfigDto(
                        SettleDelayMs:       appConfig.Agent.SettleDelayMs,
                        CoordinateMode:      appConfig.Agent.CoordinateMode.ToString(),
                        MonitorIndex:        appConfig.Agent.MonitorIndex,
                        EnableContextReset:  appConfig.Agent.EnableContextReset,
                        StripHistoryImages:  appConfig.Agent.StripHistoryImages
                    ),
                Gemini: new GeminiConfigDto(
                    Model:                    appConfig.Gemini.Model,
                    MediaResolution:          appConfig.Gemini.MediaResolution.ToString(),
                    Temperature:              (double?)appConfig.Gemini.Temperature,
                    TopP:                     (double?)appConfig.Gemini.TopP,
                    TopK:                     appConfig.Gemini.TopK,
                    CoordinateMaxOutputTokens: appConfig.Gemini.CoordinateMaxOutputTokens,
                    ThinkingBudget:           appConfig.Gemini.ThinkingBudget,
                    ThinkingLevel:            appConfig.Gemini.ThinkingLevel?.ToString()
                )
            );

            return Results.Ok(response);
        });

        // ── Schema endpoint ───────────────────────────────────────────────────

        app.MapGet("/api/config/schema", (AppConfig appConfig) =>
        {
            // Add a line here for each new provider — everything else is auto-reflected
            var sections = new object[]
            {
                BuildSection("gemini", "Gemini", appConfig.Gemini, isProvider: true),
                BuildSection("agent",  "Agent",  appConfig.Agent,  isProvider: false),
            };
            return Results.Ok(new { sections });
        });

        // ── Update endpoint ───────────────────────────────────────────────────

        app.MapPost("/api/config", (JsonElement body, AppConfig appConfig) =>
        {
            if (body.TryGetProperty("gemini", out var geminiEl)) ApplyUpdates(appConfig.Gemini, geminiEl);
            if (body.TryGetProperty("agent",  out var agentEl))  ApplyUpdates(appConfig.Agent,  agentEl);
            return Results.Ok();
        });
    }

    // ── Reflection helpers ────────────────────────────────────────────────────

    /// <summary>Builds a schema section object by reflecting over <see cref="ConfigFieldAttribute"/>-annotated properties.</summary>
    private static object BuildSection(string key, string label, object obj, bool isProvider)
    {
        var fields = new List<object>();

        foreach (var prop in obj.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            var attr = prop.GetCustomAttribute<ConfigFieldAttribute>();
            if (attr is null) continue;

            var propType   = prop.PropertyType;
            var underlying = Nullable.GetUnderlyingType(propType) ?? propType;
            bool nullable  = Nullable.GetUnderlyingType(propType) != null
                             || (propType == typeof(string)); // string is a reference type

            string fieldType;
            string[]? options = null;

            if (underlying == typeof(string))       fieldType = attr.IsPassword ? "password" : "string";
            else if (underlying == typeof(int))     fieldType = "int";
            else if (underlying == typeof(float))   fieldType = "float";
            else if (underlying == typeof(double))  fieldType = "float";
            else if (underlying == typeof(bool))    fieldType = "bool";
            else if (underlying.IsEnum)
            {
                fieldType = "enum";
                options   = Enum.GetNames(underlying);
            }
            else fieldType = "string";

            var raw   = prop.GetValue(obj);
            object? value = raw is Enum e ? e.ToString() : raw;

            fields.Add(new
            {
                key         = CamelCase.ConvertName(prop.Name),
                label       = attr.Label,
                type        = fieldType,
                description = attr.Description,
                nullable,
                value,
                options,
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

        foreach (var prop in updates.EnumerateObject())
        {
            var pi = target.GetType().GetProperty(
                prop.Name, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
            if (pi is null || !pi.CanWrite) continue;

            var propType   = pi.PropertyType;
            var underlying = Nullable.GetUnderlyingType(propType) ?? propType;

            try
            {
                if (prop.Value.ValueKind == JsonValueKind.Null)
                {
                    pi.SetValue(target, null);
                    continue;
                }

                object? converted = null;
                if      (underlying == typeof(string))  converted = prop.Value.GetString();
                else if (underlying == typeof(int))     converted = prop.Value.GetInt32();
                else if (underlying == typeof(float))   converted = (float)prop.Value.GetDouble();
                else if (underlying == typeof(double))  converted = prop.Value.GetDouble();
                else if (underlying == typeof(bool))    converted = prop.Value.GetBoolean();
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
    AgentConfigDto  Agent,
    GeminiConfigDto Gemini
);

internal sealed record AgentConfigDto(
    int     SettleDelayMs,
    string? CoordinateMode,
    int?    MonitorIndex,
    bool    EnableContextReset,
    bool    StripHistoryImages
);

internal sealed record GeminiConfigDto(
    string? Model,
    string? MediaResolution,
    double? Temperature,
    double? TopP,
    int?    TopK,
    int?    CoordinateMaxOutputTokens,
    int?    ThinkingBudget,
    string? ThinkingLevel
);
