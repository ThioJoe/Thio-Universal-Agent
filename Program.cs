// Program.cs
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using System;
using Thio_Universal_Agent;
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

app.Run();