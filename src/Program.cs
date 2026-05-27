// Program.cs
using Microsoft.Extensions.FileProviders;
using System.Diagnostics;
using System.IO.Pipes;
using Thio_Universal_Agent;
using Thio_Universal_Agent.AI_API.Anthropic;
using Thio_Universal_Agent.AI_API.Gemini;
using Thio_Universal_Agent.AI_API.OpenAI;
using Thio_Universal_Agent.Endpoints;
using Thio_Universal_Agent.Handlers;
using Thio_Universal_Agent.OS_Windows;

// Enforce single instance, open the main instance's URL if already open
const string appMutexName = "ThioUniversalAgent_SingleInstance_Mutex";
const string pipeName = "ThioUniversalAgent_URL_Pipe";

// 1. Mutex check to see if we are the second instance
using Mutex mutex = new Mutex(true, appMutexName, out bool createdNew);

if (!createdNew)
{
    // 2. We are the second instance. Connect to the pipe, get the URL, and exit.
    try
    {
        using NamedPipeClientStream client = new NamedPipeClientStream(".", pipeName, PipeDirection.In);
        client.Connect(2000); // 2-second timeout

        using StreamReader reader = new StreamReader(client);
        string? existingUrl = reader.ReadLine();

        if (!string.IsNullOrWhiteSpace(existingUrl))
        {
            Process.Start(new ProcessStartInfo(existingUrl) { UseShellExecute = true });
        }
    }
    catch { /* Pipe was busy or broken, just exit safely */ }

    return; // Exit second instance immediately
}

// --------------------------------------

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);
int availablePort = RuntimeHandlers.FindAvailablePort();
builder.WebHost.UseUrls($"http://localhost:{availablePort}");

// 3. Register our managed IPC server to run in the background
builder.Services.AddHostedService(sp => new RuntimeHandlers.SingleInstanceIpcService(availablePort));

// OS Strategy routing
if (OperatingSystem.IsWindows())
{
    builder.Services.AddSingleton<IScreenProvider, WindowsScreenProvider>();
    builder.Services.AddSingleton<IInputProvider, WindowsInputProvider>();
    builder.Services.AddSingleton<ISystemProvider, WindowsSystemProvider>();
    builder.Services.AddSingleton<IHotkeyProvider, WindowsHotkeyProvider>();
    builder.Services.AddSingleton<HotkeyService>();
    builder.Services.AddHostedService(sp => sp.GetRequiredService<HotkeyService>());
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

builder.Services.AddSingleton<AppConfig>();
builder.Services.AddSingleton<ISecretProvider, SecretsHandler>();
builder.Services.AddSingleton<VaultSession>();

// Register all API providers
builder.Services.AddHttpClient<GeminiProvider>();
builder.Services.AddHttpClient<OpenAIProvider>();
builder.Services.AddHttpClient<AnthropicProvider>();

// Dynamic factory to resolve the active provider at runtime
builder.Services.AddTransient<IAiProvider>(sp =>
{
    AppConfig config = sp.GetRequiredService<AppConfig>();
    return config.General.ActiveProvider switch
    {
        AiProviderType.ChatGPT => sp.GetRequiredService<OpenAIProvider>(),
        AiProviderType.Claude => sp.GetRequiredService<AnthropicProvider>(),
        _ => sp.GetRequiredService<GeminiProvider>()
    };
});

builder.Services.AddTransient<CoordinatePrompter>();
builder.Services.AddTransient<AgentActionExecutor>();
builder.Services.AddTransient<AgentLoop>();
builder.Services.AddSingleton<AgentSessionManager>();

WebApplication app = builder.Build();

AgentPromptBuilder.SystemProvider = app.Services.GetService<ISystemProvider>();

EmbeddedFileProvider embeddedProvider = new EmbeddedFileProvider(typeof(Program).Assembly, "Thio_Universal_Agent.wwwroot");
app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = embeddedProvider });
app.UseStaticFiles(new StaticFileOptions { FileProvider = embeddedProvider });

app.Lifetime.ApplicationStarted.Register(() =>
{
    string url = app.Urls.FirstOrDefault() ?? $"http://localhost:{availablePort}";
    Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });

    // Grab the root logger and print a prominent banner
    Console.WriteLine(new string('=', 55));
    Console.WriteLine("THIO UNIVERSAL AGENT READY");
    Console.WriteLine($"Web Interface: {url}");
    Console.WriteLine(new string('=', 55));
});

app.MapTestEndpoints();
app.MapAgentEndpoints();
app.MapConfigEndpoints();
app.MapSecretsEndpoints();

app.Run();