using Microsoft.Extensions.Configuration;

namespace Thio_Universal_Agent.Endpoints;

/// <summary>
/// Exposes a read-only snapshot of the server configuration (appsettings.json) to the web UI.
/// Sensitive values such as API keys are intentionally excluded.
/// </summary>
internal static class ConfigEndpoints
{
    internal static void MapConfigEndpoints(this WebApplication app)
    {
        app.MapGet("/api/config", (IConfiguration cfg) =>
        {
            var response = new AppConfigResponse(
                Agent: new AgentConfigDto(
                        SettleDelayMs:       cfg.GetValue<int>("Agent:SettleDelayMs"),
                        CoordinateMode:      cfg["Agent:CoordinateMode"],
                        MonitorIndex:        cfg.GetValue<int?>("Agent:MonitorIndex"),
                        EnableContextReset:  cfg.GetValue<bool?>("Agent:EnableContextReset") ?? true,
                        StripHistoryImages:  cfg.GetValue<bool?>("Agent:StripHistoryImages") ?? true
                    ),
                Gemini: new GeminiConfigDto(
                    Model:                    cfg["Gemini:Model"],
                    MediaResolution:          cfg["Gemini:MediaResolution"],
                    Temperature:              cfg.GetValue<double?>("Gemini:Temperature"),
                    TopP:                     cfg.GetValue<double?>("Gemini:TopP"),
                    TopK:                     cfg.GetValue<int?>("Gemini:TopK"),
                    CoordinateMaxOutputTokens: cfg.GetValue<int?>("Gemini:CoordinateMaxOutputTokens"),
                    ThinkingBudget:           cfg.GetValue<int?>("Gemini:ThinkingBudget"),
                    ThinkingLevel:            cfg["Gemini:ThinkingLevel"]
                )
            );

            return Results.Ok(response);
        });
    }
}

// ---- DTOs ---------------------------------------------------------------

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
