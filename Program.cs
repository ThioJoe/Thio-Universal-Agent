// Program.cs
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using System;
using System.Text.Json;
using Thio_Universal_Agent;
using Thio_Universal_Agent.AI_API;
using Thio_Universal_Agent.AI_API.Gemini;
using Thio_Universal_Agent.OS_Windows;

var builder = WebApplication.CreateBuilder(args);

// OS Strategy routing
if (OperatingSystem.IsWindows())
{
    builder.Services.AddSingleton<IScreenProvider, WindowsScreenProvider>();
}
else if (OperatingSystem.IsMacOS())
{
    // builder.Services.AddSingleton<IScreenProvider, MacScreenProvider>();
}
else if (OperatingSystem.IsLinux())
{
    // builder.Services.AddSingleton<IScreenProvider, LinuxScreenProvider>();
}
else
{
    throw new PlatformNotSupportedException("Unsupported operating system.");
}

// AI provider
builder.Services.AddHttpClient<IAiProvider, GeminiProvider>();

var app = builder.Build();

// Enable serving the index.html file
app.UseDefaultFiles();
app.UseStaticFiles();

// Endpoint to serve the screenshot
app.MapGet("/api/screenshot", (IScreenProvider screenProvider) =>
{
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

// Test endpoint — accepts an optional API key override so the test page works
// with any key without modifying appsettings.json.
app.MapPost("/api/test/chat", async (TestChatRequest req, IAiProvider aiProvider, IHttpClientFactory httpClientFactory, IConfiguration config, CancellationToken ct) =>
{
    try
    {
        if (!string.IsNullOrWhiteSpace(req.ApiKey))
        {
            // Key override: call Gemini directly, bypassing the DI-registered provider.
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

        // No key override — use the configured provider.
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

app.Run();

record TestChatRequest(string Prompt, string? ApiKey, string? Model);