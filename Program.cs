// Program.cs
using Thio_Universal_Agent;
using Thio_Universal_Agent.AI_API;
using Thio_Universal_Agent.AI_API.Gemini;
using Thio_Universal_Agent.Endpoints;
using Thio_Universal_Agent.Logic;
using Thio_Universal_Agent.OS_Windows;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

// OS Strategy routing
if (OperatingSystem.IsWindows())
{
    builder.Services.AddSingleton<IScreenProvider, WindowsScreenProvider>();
    builder.Services.AddSingleton<IInputProvider, WindowsInputProvider>();
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

builder.Services.AddHttpClient<IAiProvider, GeminiProvider>();
builder.Services.AddTransient<CoordinatePrompter>();
builder.Services.AddTransient<AgentActionExecutor>();
builder.Services.AddTransient<AgentLoop>();
builder.Services.AddSingleton<AgentSessionManager>();

var app = builder.Build();

app.UseDefaultFiles();
app.UseStaticFiles();

app.MapTestEndpoints();
app.MapAgentEndpoints();
app.MapConfigEndpoints();

app.Run();
