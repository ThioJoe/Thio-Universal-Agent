#pragma warning disable IDE0130 // Namespace does not match folder structure
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
    public double NormalizedX => Screenshot.Width  == 0 ? 0 : (double)ImageX / Screenshot.Width  * Screenshot.DefaultNormalized;

    /// <summary>
    /// Y position normalised to a 0–1000 scale across the height of the captured screenshot.
    /// Matches the coordinate space used by the AI and <see cref="CoordinatePrompter"/>.
    /// </summary>
    public double NormalizedY => Screenshot.Height == 0 ? 0 : (double)ImageY / Screenshot.Height * Screenshot.DefaultNormalized;

    // ── Monitor info ─────────────────────────────────────────────────────────

    /// <summary>The monitor that contains this coordinate, or <see langword="null"/> if unknown.</summary>
    public MonitorInfo? Monitor { get; } = Monitor;

    /// <summary>Zero-based index of <see cref="Monitor"/>, or <see langword="null"/> if unknown.</summary>
    public int? MonitorIndex => Monitor?.Index;

    // ── Screenshot reference ──────────────────────────────────────────────────

    /// <summary>The screenshot this coordinate was resolved against.</summary>
    public Screenshot Screenshot { get; } = Screenshot;

    // ── Factories ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Creates a <see cref="ScreenCoordinate"/> from image-local pixel coordinates
    /// (i.e. pixel offsets within the captured bitmap, as returned by the AI or zoom pipeline).
    /// Rounds to the nearest integer and adds the screenshot origin.
    /// </summary>
    public static ScreenCoordinate FromImagePixels(double imgX, double imgY, Screenshot screenshot, MonitorInfo? monitor = null)
    {
        int absX = (int)Math.Round(imgX) + screenshot.OriginX;
        int absY = (int)Math.Round(imgY) + screenshot.OriginY;
        return new ScreenCoordinate(absX, absY, screenshot, monitor);
    }

    /// <summary>
    /// Creates a <see cref="ScreenCoordinate"/> from coordinates that are normalised to an
    /// arbitrary <paramref name="normalizedWidth"/> × <paramref name="normalizedHeight"/> space
    /// (typically 1000×1000 as used by the AI grid).
    /// Un-normalises to image pixels relative to <see cref="Screenshot.Width"/>/<see cref="Screenshot.Height"/>,
    /// then delegates to <see cref="FromImagePixels"/>.
    /// </summary>
    public static ScreenCoordinate FromNormalized(
        double normX, double normY,
        int normalizedWidth, int normalizedHeight,
        Screenshot screenshot, MonitorInfo? monitor = null)
    {
        double imgX = Math.Round(normX / normalizedWidth  * screenshot.Width);
        double imgY = Math.Round(normY / normalizedHeight * screenshot.Height);
        return FromImagePixels(imgX, imgY, screenshot, monitor);
    }

    /// <summary>
    /// Parses an AI-supplied <c>"X,Y"</c> string where the values are coordinates normalised
    /// to a 1000×1000 grid (the format used by <c>ExactCoords</c> actions and the
    /// <c>CoordinatePrompter</c> direct mode).
    /// Throws <see cref="FormatException"/> on bad input.
    /// </summary>
    public static ScreenCoordinate FromNormalizedCoordsString(string coordStr, Screenshot screenshot, MonitorInfo? monitor = null)
    {
        (int nx, int ny) = ParseCoordsString(coordStr);
        return FromNormalized(nx, ny, Screenshot.DefaultNormalized, Screenshot.DefaultNormalized, screenshot, monitor);
    }

    /// <summary>
    /// Parses a raw <c>"X,Y"</c> string where values are already image-local pixel coordinates
    /// (no normalisation step). Useful when coordinates come from sources that report true pixels.
    /// Throws <see cref="FormatException"/> on bad input.
    /// </summary>
    public static ScreenCoordinate FromRawPixelCoordsString(string coordStr, Screenshot screenshot, MonitorInfo? monitor = null)
    {
        (int imgX, int imgY) = ParseCoordsString(coordStr);
        return FromImagePixels(imgX, imgY, screenshot, monitor);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <inheritdoc/>
    public override string ToString() =>
        Monitor is null
            ? $"Abs=({AbsoluteX}, {AbsoluteY})  Img=({ImageX}, {ImageY})  Norm=({NormalizedX:F0}, {NormalizedY:F0})"
            : $"Abs=({AbsoluteX}, {AbsoluteY})  Img=({ImageX}, {ImageY})  Norm=({NormalizedX:F0}, {NormalizedY:F0})  Monitor[{Monitor.Index}]=({MonitorX}, {MonitorY})";

    // Shared parser: splits "X,Y" and returns two ints, throws FormatException on failure.
    private static (int x, int y) ParseCoordsString(string coordStr)
    {
        string[] parts = coordStr.Split(',');
        if (parts.Length == 2
            && int.TryParse(parts[0].Trim(), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out int x)
            && int.TryParse(parts[1].Trim(), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out int y))
        {
            return (x, y);
        }
        throw new FormatException($"Invalid coordinate format: \"{coordStr}\". Expected \"X,Y\" with integer values.");
    }
}
