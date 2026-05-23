namespace Thio_Universal_Agent;

/// <summary>
/// Represents a single resolved screen coordinate, exposing the same point in every
/// relevant coordinate space so callers never have to re-derive them.
/// </summary>
/// <param name="AbsoluteX">
/// Absolute virtual-desktop X coordinate, ready for OS input APIs.
/// On multi-monitor setups this may be negative (e.g. a monitor left of the primary).
/// </param>
/// <param name="AbsoluteY">Absolute virtual-desktop Y coordinate.</param>
/// <param name="Screenshot">
/// The <see cref="Thio_Universal_Agent.Screenshot"/> the coordinate was resolved against.
/// Used to derive all relative and normalised values as expressions.
/// </param>
/// <param name="Monitor">
/// The <see cref="MonitorInfo"/> of the monitor that contains this coordinate,
/// or <see langword="null"/> if monitor information is unavailable.
/// </param>
public sealed class ScreenCoordinate(int AbsoluteX, int AbsoluteY, Screenshot Screenshot, MonitorInfo? Monitor = null)
{
    // ── Absolute (virtual-desktop) ────────────────────────────────────────────

    /// <summary>Absolute virtual-desktop X coordinate.</summary>
    public int AbsoluteX { get; } = AbsoluteX;

    /// <summary>Absolute virtual-desktop Y coordinate.</summary>
    public int AbsoluteY { get; } = AbsoluteY;

    // ── Screenshot-relative (image pixel) ────────────────────────────────────

    /// <summary>
    /// X coordinate relative to the top-left of the captured screenshot bitmap.
    /// Equivalent to <c>AbsoluteX - Screenshot.OriginX</c>.
    /// </summary>
    public int ImageX => AbsoluteX - Screenshot.OriginX;

    /// <summary>
    /// Y coordinate relative to the top-left of the captured screenshot bitmap.
    /// Equivalent to <c>AbsoluteY - Screenshot.OriginY</c>.
    /// </summary>
    public int ImageY => AbsoluteY - Screenshot.OriginY;

    // ── Monitor-relative ─────────────────────────────────────────────────────

    /// <summary>
    /// X coordinate relative to the top-left corner of <see cref="Monitor"/>.
    /// <see langword="null"/> when <see cref="Monitor"/> is not set.
    /// </summary>
    public int? MonitorX => Monitor is null ? null : AbsoluteX - Monitor.X;

    /// <summary>
    /// Y coordinate relative to the top-left corner of <see cref="Monitor"/>.
    /// <see langword="null"/> when <see cref="Monitor"/> is not set.
    /// </summary>
    public int? MonitorY => Monitor is null ? null : AbsoluteY - Monitor.Y;

    // ── Normalised (0 – 1000 grid, matching the AI coordinate space) ──────────

    /// <summary>
    /// X position normalised to a 0–1000 scale across the width of the captured screenshot.
    /// Matches the coordinate space used by the AI and <see cref="CoordinatePrompter"/>.
    /// </summary>
    public double NormalizedX => Screenshot.Width  == 0 ? 0 : (double)ImageX / Screenshot.Width  * 1000;

    /// <summary>
    /// Y position normalised to a 0–1000 scale across the height of the captured screenshot.
    /// Matches the coordinate space used by the AI and <see cref="CoordinatePrompter"/>.
    /// </summary>
    public double NormalizedY => Screenshot.Height == 0 ? 0 : (double)ImageY / Screenshot.Height * 1000;

    // ── Monitor info ─────────────────────────────────────────────────────────

    /// <summary>The monitor that contains this coordinate, or <see langword="null"/> if unknown.</summary>
    public MonitorInfo? Monitor { get; } = Monitor;

    /// <summary>Zero-based index of <see cref="Monitor"/>, or <see langword="null"/> if unknown.</summary>
    public int? MonitorIndex => Monitor?.Index;

    // ── Screenshot reference ──────────────────────────────────────────────────

    /// <summary>The screenshot this coordinate was resolved against.</summary>
    public Screenshot Screenshot { get; } = Screenshot;

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <inheritdoc/>
    public override string ToString() =>
        Monitor is null
            ? $"Abs=({AbsoluteX}, {AbsoluteY})  Img=({ImageX}, {ImageY})  Norm=({NormalizedX:F0}, {NormalizedY:F0})"
            : $"Abs=({AbsoluteX}, {AbsoluteY})  Img=({ImageX}, {ImageY})  Norm=({NormalizedX:F0}, {NormalizedY:F0})  Monitor[{Monitor.Index}]=({MonitorX}, {MonitorY})";
}
