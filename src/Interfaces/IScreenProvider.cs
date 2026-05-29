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
    /// <param name="label">Optional text label drawn near the marker. Pass <c>null</c> (default) to show no label.</param>
    void DrawClickPointMarker(int x, int y, int durationMs, int markerOpacity = 255, string? label = null);

    /// <summary>
    /// Draws a visual marker representing a click-and-drag operation from a starting point to an ending point.
    /// </summary>
    /// <param name="x_start">The horizontal coordinate of the drag starting point.</param>
    /// <param name="y_start">The vertical coordinate of the drag starting point.</param>
    /// <param name="x_end">The horizontal coordinate of the drag ending point.</param>
    /// <param name="y_end">The vertical coordinate of the drag ending point.</param>
    /// <param name="durationMs">The duration for which the marker is displayed, in milliseconds.</param>
    /// <param name="markerOpacity">The opacity of the marker, ranging from 0 (transparent) to 255 (opaque).</param>
    /// <param name="label">Optional text label drawn near the marker. Pass <c>null</c> (default) to show no label.</param>
    void DrawClickDragMarker(int x_start, int y_start, int x_end, int y_end, int durationMs, int markerOpacity = 255, string? label = null);

    /// <summary>
    /// Draws a bounding box with the specified coordinates, duration, and opacity settings. Can be used to highlight text box element etc.
    /// </summary>
    /// <param name="x1">The left X coordinate of the bounding box.</param>
    /// <param name="y1">The top Y coordinate of the bounding box.</param>
    /// <param name="x2">The right X coordinate of the bounding box.</param>
    /// <param name="y2">The bottom Y coordinate of the bounding box.</param>
    /// <param name="durationMs">The duration in milliseconds to display the bounding box.</param>
    /// <param name="borderOpacity">The opacity of the border (0-255). Default is 255 (fully opaque).</param>
    /// <param name="fillOpacity">The opacity of the fill (0-255). Default is 0 (transparent).</param>
    /// <param name="label">Optional text label drawn near the marker. Pass <c>null</c> (default) to show no label.</param>
    void DrawBoundingBox(int x1, int y1, int x2, int y2, int durationMs, int borderOpacity = 255, int fillOpacity = 0, string? label = null);

    /// <summary>
    /// Displays a single-point move marker (open crosshair, no arrow) at the given coordinates.
    /// Intended for human-control mode where there is no meaningful start position.
    /// </summary>
    void DrawMouseMoveMarker(int x, int y, int durationMs, int markerOpacity = 255, string? label = null);

    /// <summary>
    /// Displays a single-point move arrow marker at the given coordinates.
    /// Intended for autonomous mode to visualise where the cursor just moved.
    /// </summary>
    void DrawMouseMoveArrow(int x, int y, int durationMs, int markerOpacity = 255);

    /// <summary>
    /// Clears all drawn click points.
    /// </summary>
    void ClearMarkers();

    /// <summary>
    /// When set, each <c>Draw*</c> call stamps this value onto the marker's queue-order label so the
    /// human operator can see the execution order of a queued batch. Set to <c>null</c> to suppress numbers.
    /// </summary>
    string? CurrentQueueLabel { get => null; set { } }
}
