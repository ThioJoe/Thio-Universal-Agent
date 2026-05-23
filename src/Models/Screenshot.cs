using SkiaSharp;

namespace Thio_Universal_Agent;

/// <summary>
/// Bundles a screen capture with everything that travels with it: the raw bytes,
/// the processed (grid-overlaid) bytes sent to the AI, the virtual-desktop origin
/// of the captured area, the physical pixel dimensions, and an optional annotated
/// image with crosshair(s) drawn at the resolved click location.
/// </summary>
public sealed class Screenshot
{
    /// <summary>Raw captured bytes before any overlay or processing is applied.</summary>
    public byte[] Original { get; }

    /// <summary>
    /// Bytes sent to the AI. Starts as <see cref="Original"/> and is replaced
    /// with a grid-overlaid version once processed.
    /// </summary>
    public byte[] Processed { get; set; }

    /// <summary>Virtual-desktop X coordinate of the top-left of the captured area.</summary>
    public int OriginX { get; }

    /// <summary>Virtual-desktop Y coordinate of the top-left of the captured area.</summary>
    public int OriginY { get; }

    /// <summary>Width of the captured area in physical pixels.</summary>
    public int Width { get; }

    /// <summary>Height of the captured area in physical pixels.</summary>
    public int Height { get; }

    /// <summary>
    /// Annotated version of <see cref="Processed"/> with crosshair(s) drawn at the
    /// resolved click location. <see langword="null"/> until set by the executor after
    /// coordinate resolution.
    /// </summary>
    public byte[]? Annotated { get; set; }

    /// <param name="original">Raw captured bytes (pre-overlay).</param>
    /// <param name="originX">Virtual-desktop X origin of the captured area.</param>
    /// <param name="originY">Virtual-desktop Y origin of the captured area.</param>
    /// <param name="width">Width in physical pixels.</param>
    /// <param name="height">Height in physical pixels.</param>
    public Screenshot(byte[] original, int originX, int originY, int width, int height)
    {
        Original  = original;
        Processed = original; // replaced with grid-overlaid version once processed
        OriginX   = originX;
        OriginY   = originY;
        Width     = width;
        Height    = height;
    }

    /// <summary>
    /// Convenience constructor for cases where only raw bytes are available and no
    /// virtual-desktop origin is known (e.g. a screenshot uploaded by a client).
    /// Origin defaults to (0, 0) and dimensions are read from the image data.
    /// </summary>
    /// <param name="bytes">Raw image bytes (PNG, JPEG, etc.).</param>
    /// <param name="originX">Virtual-desktop X origin (defaults to 0).</param>
    /// <param name="originY">Virtual-desktop Y origin (defaults to 0).</param>
    public Screenshot(byte[] bytes, int originX, int originY)
    {
        using SKBitmap bmp = SKBitmap.Decode(bytes);
        Original  = bytes;
        Processed = bytes;
        OriginX   = originX;
        OriginY   = originY;
        Width     = bmp.Width;
        Height    = bmp.Height;
    }

    /// <summary>
    /// Translates absolute virtual-desktop coordinates to image-local pixel coordinates
    /// (i.e. coordinates within the captured bitmap).
    /// </summary>
    public (int X, int Y) ToImageRelative(int absX, int absY) => (absX - OriginX, absY - OriginY);

    /// <summary>
    /// Translates image-local pixel coordinates to absolute virtual-desktop coordinates
    /// suitable for OS input APIs.
    /// </summary>
    public (int X, int Y) ToAbsolute(int imgX, int imgY) => (imgX + OriginX, imgY + OriginY);
}
