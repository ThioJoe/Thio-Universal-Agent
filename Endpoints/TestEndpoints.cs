using System.Net.Http.Json;
using System.Text.Json;
using Thio_Universal_Agent;
using Thio_Universal_Agent.AI_API;

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
                byte[] imageBytes = screenProvider.CaptureScreen();
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
                if (!string.IsNullOrWhiteSpace(req.ApiKey))
                {
                    // Key override path: lets the test page use any key without touching appsettings.json.
                    var model = string.IsNullOrWhiteSpace(req.Model) ? config["Gemini:Model"] ?? "gemini-2.0-flash" : req.Model;
                    var url = $"https://generativelanguage.googleapis.com/v1beta/models/{model}:generateContent?key={req.ApiKey}";
                    var body = new { contents = new[] { new { parts = new[] { new { text = req.Prompt } } } } };

                    using var client = httpClientFactory.CreateClient();
                    using var httpResponse = await client.PostAsJsonAsync(url, body, ct);
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

                    return Results.Ok(new { text });
                }

                var result = await aiProvider.SendPromptAsync(req.Prompt, ct);
                return result.Success
                    ? Results.Ok(new { result.Text })
                    : Results.Problem(result.ErrorMessage);
            }
            catch (Exception ex)
            {
                return Results.Problem(ex.Message);
            }
        });
    }
}

// Scoped to this file — it's a transport detail for the test endpoint, not a domain type.
file record TestChatRequest(string Prompt, string? ApiKey, string? Model);
