using System.Runtime.Versioning;

namespace Thio_Universal_Agent.OS_Windows;

/// <summary>
/// Provides Windows-specific system information for use in agent prompts.
/// </summary>
[SupportedOSPlatform("windows")]
public class WindowsSystemProvider : ISystemProvider
{
    /// <inheritdoc/>
    public string GetOSName()
    {
        Version v = Environment.OSVersion.Version;

        // Note, these aren't all necessarily supported, but have them anyway
        return (v.Major, v.Minor, v.Build) switch
        {
            (10, 0, >= 22000) => "Windows 11",
            (10, 0, _)        => "Windows 10",
            (6,  3, _)        => "Windows 8.1",
            (6,  2, _)        => "Windows 8",
            (6,  1, _)        => "Windows 7",
            (6,  0, _)        => "Windows Vista",
            (5,  2, _)        => "Windows Server 2003",
            (5,  1, _)        => "Windows XP",
            _                 => $"Windows (NT {v.Major}.{v.Minor})"
        };
    }
}
