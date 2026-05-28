namespace Thio_Universal_Agent;

/// <summary>
/// Describes a single physical display monitor.
/// </summary>
/// <param name="Index">Zero-based index in the order returned by the platform enumeration.</param>
/// <param name="X">Left edge of the monitor in virtual-desktop coordinates.</param>
/// <param name="Y">Top edge of the monitor in virtual-desktop coordinates.</param>
/// <param name="Width">Width in physical pixels.</param>
/// <param name="Height">Height in physical pixels.</param>
/// <param name="IsPrimary">Whether this is the primary monitor.</param>
public sealed record MonitorInfo(int Index, int X, int Y, int Width, int Height, bool IsPrimary);

public interface IScreenProvider
{
    /// <summary>
    /// Captures the screen area determined by the current configuration and returns a
    /// <see cref="Screenshot"/> containing the raw bytes, virtual-desktop origin, and
    /// physical pixel dimensions — all derived from a single <c>GetCaptureRect</c> call
    /// so the image and its coordinate offset are guaranteed to correspond to the same monitor.
    /// When <c>Agent:MonitorIndex</c> is set, only that monitor is captured;
    /// otherwise the full virtual screen (all monitors) is captured.
    /// </summary>
    Screenshot CaptureScreen();

    /// <summary>
    /// Returns the top-left corner of the captured area in virtual-desktop coordinates.
    /// For a full virtual-screen capture this may be negative on multi-monitor setups.
    /// For a single-monitor capture this is the monitor's position in the virtual desktop.
    /// <para>
    /// Prefer using the <c>OriginX</c>/<c>OriginY</c> values returned by
    /// <see cref="CaptureScreen"/> so the origin is always derived from the same
    /// <c>GetCaptureRect</c> call as the screenshot it accompanies.
    /// </para>
    /// </summary>
    (int X, int Y) GetVirtualScreenOrigin() => (0, 0);

    /// <summary>
    /// Enumerates all connected monitors. The default implementation returns an empty list;
    /// platform-specific providers should override this.
    /// </summary>
    IReadOnlyList<MonitorInfo> GetMonitors() => [];

    /// <summary>
    /// Draws a marker at the specified click coordinates.
    /// </summary>
    /// <param name="x">The x-coordinate of the click point.</param>
    /// <param name="y">The y-coordinate of the click point.</param>
    /// <param name="durationMs">The duration in milliseconds for which the click point should be displayed. Default is 1000ms. 0 for until cleared manually.</param>
    /// <param name="markerOpacity">The opacity of the click marker, from 0 (fully transparent) to 255 (fully opaque). Default is 255.</param>
    void DrawClickPoint(int x, int y, int durationMs, int markerOpacity=255);

    /// <summary>
    /// Clears all drawn click points.
    /// </summary>
    void ClearClickPoints();
}
