// Program.cs
using Microsoft.Extensions.FileProviders;
using Thio_Universal_Agent;
using Thio_Universal_Agent.AI_API.Gemini;
using Thio_Universal_Agent.Endpoints;
using Thio_Universal_Agent.Handlers;
using Thio_Universal_Agent.OS_Windows;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

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
builder.Services.AddHttpClient<IAiProvider, GeminiProvider>();
builder.Services.AddTransient<CoordinatePrompter>();
builder.Services.AddTransient<AgentActionExecutor>();
builder.Services.AddTransient<AgentLoop>();
builder.Services.AddSingleton<AgentSessionManager>();

var app = builder.Build();

AgentPromptBuilder.SystemProvider = app.Services.GetService<ISystemProvider>();

var embeddedProvider = new EmbeddedFileProvider(typeof(Program).Assembly, "Thio_Universal_Agent.wwwroot");
app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = embeddedProvider });
app.UseStaticFiles(new StaticFileOptions { FileProvider = embeddedProvider });

app.Lifetime.ApplicationStarted.Register(() =>
{
    string url = app.Urls.FirstOrDefault() ?? "http://localhost:5112";
    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
});

app.MapTestEndpoints();
app.MapAgentEndpoints();
app.MapConfigEndpoints();

app.Run();
