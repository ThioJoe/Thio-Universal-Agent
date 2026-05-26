namespace Thio_Universal_Agent;

/// <summary>
/// Provides platform-specific system information for use in agent prompts and diagnostics.
/// </summary>
public interface ISystemProvider
{
    /// <summary>
    /// Returns the human-readable OS name, e.g. "Windows 11".
    /// </summary>
    string GetOSName();

    /// <summary>
    /// Returns the raw OS description and build string from the .NET runtime,
    /// e.g. "Microsoft Windows 10.0.26200".
    /// </summary>
    string GetOSDescription() =>
        System.Runtime.InteropServices.RuntimeInformation.OSDescription;

    /// <summary>
    /// Returns the CPU architecture, e.g. "X64".
    /// </summary>
    string GetArchitecture() =>
        System.Runtime.InteropServices.RuntimeInformation.OSArchitecture.ToString();

    bool TaskFinishedNotifier(string notificationText, string notificationTitle = "");
}
