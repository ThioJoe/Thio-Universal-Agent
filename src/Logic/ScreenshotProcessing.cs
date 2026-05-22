using Microsoft.Maui.Graphics;
using Microsoft.Maui.Graphics.Skia;
using SkiaSharp;
using System.Globalization;

namespace Thio_Universal_Agent.Logic;

public sealed partial class CoordinatePrompter
{
    public static (int width, int height) GetImageResolution(byte[] screenshotBytes)
    {
        ArgumentNullException.ThrowIfNull(screenshotBytes);

        using IImage source = LoadImage(screenshotBytes);
        return ((int)source.Width, (int)source.Height);
    }

    public static (int width, int height) GetImageResolution(IImage image)
    {
        ArgumentNullException.ThrowIfNull(image);

        return ((int)image.Width, (int)image.Height);
    }

    /// <summary>Creates a <see cref="ViewRegion"/> covering the full source image.</summary>
    private static ViewRegion CreateFullView(IImage source)
    {
        return new ViewRegion(0, 0, source.Width, source.Height);
    }


    /// <summary>
    /// Produces the full-image grid overlay PNG from raw screenshot bytes with no AI calls or zooming.
    /// Used by the test endpoint to preview what the first-iteration grid image looks like.
    /// </summary>
    public byte[] CreateFullGridOverlayImage(byte[] screenshotBytes)
    {
        ArgumentNullException.ThrowIfNull(screenshotBytes);
        using IImage source = LoadImage(screenshotBytes);
        ViewRegion view = CreateFullView(source);
        return CreateGridOverlayImage(source, view, _divisions, _divisions);
    }

    /// <summary>Decodes raw image bytes into an <see cref="IImage"/> for use with the canvas.</summary>
    private static IImage LoadImage(byte[] imageBytes)
    {
        return new SkiaImage(SKBitmap.Decode(imageBytes));
    }

    /// <summary>Computes the label font size used for axis labels.</summary>
    private static float ComputeLabelSize(int imageWidth)
        => Math.Max(16f, imageWidth / 60f);

    /// <summary>
    /// Computes rendered image area dimensions, scaling up proportionally
    /// so the longer side meets <paramref name="minResolution"/> when it would
    /// otherwise be smaller. Returns the original dimensions when already large enough.
    /// </summary>
    private static (int Width, int Height) ComputeRenderedSize(int viewWidth, int viewHeight, int minResolution)
    {
        int maxDim = Math.Max(viewWidth, viewHeight);
        if (maxDim >= minResolution)
            return (viewWidth, viewHeight);

        double scale = (double)minResolution / maxDim;
        return ((int)(viewWidth * scale), (int)(viewHeight * scale));
    }

    /// <summary>
    /// Computes the ruler margin needed so that axis labels, tick marks, and gaps
    /// all fit without clipping, for both axes.
    /// </summary>
    private static int ComputeRulerOffset(int imageWidth, int maxLabelValue)
    {
        float labelSize = ComputeLabelSize(imageWidth);
        float textH = labelSize * 1.3f;
        int maxDigits = maxLabelValue.ToString(CultureInfo.InvariantCulture).Length;
        float maxTextW = labelSize * maxDigits * 0.75f;
        float tickLen = Math.Max(8f, labelSize * 0.5f);
        float gap = Math.Max(4f, labelSize * 0.25f);
        float padding = Math.Max(4f, labelSize * 0.15f);

        float verticalNeed = textH + tickLen + gap + padding;
        float horizontalNeed = maxTextW + tickLen + gap + padding;
        return (int)Math.Ceiling(Math.Max(verticalNeed, horizontalNeed));
    }

    /// <summary>
    /// Computes the trailing buffer added after the image area so the last
    /// axis labels are not clipped on the right or bottom edge.
    /// </summary>
    private static int ComputeLabelBuffer(int imageWidth, int maxLabelValue)
    {
        float labelSize = ComputeLabelSize(imageWidth);
        float textH = labelSize * 1.3f;
        int maxDigits = maxLabelValue.ToString(CultureInfo.InvariantCulture).Length;
        float maxTextW = labelSize * maxDigits * 0.75f;
        float padding = Math.Max(4f, labelSize * 0.25f);
        return (int)Math.Ceiling(Math.Max(maxTextW, textH) / 2f + padding);
    }


    /// <summary>
    /// Returns a copy of <paramref name="imageBytes"/> with a crosshair marker drawn directly at
    /// the given absolute pixel position. Used for Direct / DirectAutoNormalize modes where there
    /// is no grid overlay and the coordinate is already an un-normalised pixel location.
    /// </summary>
    private static byte[] CreateAnnotatedImageDirect(byte[] imageBytes, double pixelX, double pixelY)
    {
        using IImage image = LoadImage(imageBytes);
        int w = (int)image.Width;
        int h = (int)image.Height;

        using var context = new SkiaBitmapExportContext(w, h, 1.0f);
        ICanvas canvas = context.Canvas;

        canvas.DrawImage(image, 0, 0, w, h);

        float cx = (float)pixelX;
        float cy = (float)pixelY;
        float radius = Math.Max(10f, w / 150f);
        float crossLen = radius * 2.5f;

        canvas.StrokeColor = Colors.Red;
        canvas.StrokeSize = Math.Max(3f, w / 500f);
        canvas.Antialias = true;
        canvas.DrawCircle(cx, cy, radius);
        canvas.DrawLine(cx - crossLen, cy, cx + crossLen, cy);
        canvas.DrawLine(cx, cy - crossLen, cx, cy + crossLen);

        using var ms = new MemoryStream();
        context.WriteToStream(ms);
        return ms.ToArray();
    }


    /// <summary>
    /// Returns a copy of <paramref name="imageBytes"/> with two crosshair markers drawn directly at the
    /// given absolute pixel positions: a green crosshair at the start and a red crosshair at the end,
    /// connected by an arrow line. Used to visualise drag actions.
    /// </summary>
    internal static byte[] CreateAnnotatedImageDrag(byte[] imageBytes, double startX, double startY, double endX, double endY)
    {
        using IImage image = LoadImage(imageBytes);
        int w = (int)image.Width;
        int h = (int)image.Height;

        using var context = new SkiaBitmapExportContext(w, h, 1.0f);
        ICanvas canvas = context.Canvas;

        canvas.DrawImage(image, 0, 0, w, h);

        float strokeSize = Math.Max(3f, w / 500f);
        float radius = Math.Max(10f, w / 150f);
        float crossLen = radius * 2.5f;

        canvas.Antialias = true;
        canvas.StrokeSize = strokeSize;

        // Arrow line from start to end
        canvas.StrokeColor = Color.FromArgb("#CC888888");
        canvas.DrawLine((float)startX, (float)startY, (float)endX, (float)endY);

        // Arrowhead at end
        double angle = Math.Atan2(endY - startY, endX - startX);
        float arrowLen = radius * 1.8f;
        float arrowSpread = 0.45f;
        canvas.StrokeColor = Color.FromArgb("#CCF0C040");
        canvas.DrawLine(
            (float)endX, (float)endY,
            (float)(endX - arrowLen * Math.Cos(angle - arrowSpread)),
            (float)(endY - arrowLen * Math.Sin(angle - arrowSpread)));
        canvas.DrawLine(
            (float)endX, (float)endY,
            (float)(endX - arrowLen * Math.Cos(angle + arrowSpread)),
            (float)(endY - arrowLen * Math.Sin(angle + arrowSpread)));

        // Green crosshair — drag start
        canvas.StrokeColor = Colors.Lime;
        canvas.DrawCircle((float)startX, (float)startY, radius);
        canvas.DrawLine((float)startX - crossLen, (float)startY, (float)startX + crossLen, (float)startY);
        canvas.DrawLine((float)startX, (float)startY - crossLen, (float)startX, (float)startY + crossLen);

        // Red crosshair — drag end
        canvas.StrokeColor = Colors.Red;
        canvas.DrawCircle((float)endX, (float)endY, radius);
        canvas.DrawLine((float)endX - crossLen, (float)endY, (float)endX + crossLen, (float)endY);
        canvas.DrawLine((float)endX, (float)endY - crossLen, (float)endX, (float)endY + crossLen);

        using var ms = new MemoryStream();
        context.WriteToStream(ms);
        return ms.ToArray();
    }


    /// <summary>Returns a copy of the grid image with a crosshair marker drawn at the parsed coordinate.</summary>
    private static byte[] CreateAnnotatedImage(
        byte[] gridImageBytes,
        GridCoordinate coordinate,
        int imageWidth,
        int imageHeight,
        int cols = DefaultDivisions,
        int rows = DefaultDivisions)
    {
        int rulerOffset = ComputeRulerOffset(imageWidth, Math.Max(cols, rows));

        using IImage gridImage = LoadImage(gridImageBytes);
        int w = (int)gridImage.Width;
        int h = (int)gridImage.Height;

        using var context = new SkiaBitmapExportContext(w, h, 1.0f);
        ICanvas canvas = context.Canvas;

        canvas.DrawImage(gridImage, 0, 0, w, h);

        float cellW = (float)imageWidth / cols;
        float cellH = (float)imageHeight / rows;
        float cx = rulerOffset + (float)coordinate.X * cellW;
        float cy = rulerOffset + (float)coordinate.Y * cellH;
        float radius = Math.Max(10f, imageWidth / 150f);
        float crossLen = radius * 2.5f;

        canvas.StrokeColor = Colors.Red;
        canvas.StrokeSize = Math.Max(3f, imageWidth / 500f);
        canvas.Antialias = true;
        canvas.DrawCircle(cx, cy, radius);
        canvas.DrawLine(cx - crossLen, cy, cx + crossLen, cy);
        canvas.DrawLine(cx, cy - crossLen, cx, cy + crossLen);

        using var ms = new MemoryStream();
        context.WriteToStream(ms);
        return ms.ToArray();
    }


    /// <summary>Draws the grid lines, axis labels and tick marks onto the canvas.</summary>
    private static void DrawRulerGrid(
        ICanvas canvas,
        int rulerOffset,
        int imageWidth,
        int imageHeight,
        int cols,
        int rows,
        Color color,
        float? lineThickness)
    {
        float thickness = lineThickness ?? Math.Max(1f, imageWidth / 1200f);
        float labelSize = ComputeLabelSize(imageWidth);
        float tickLen = Math.Max(8f, labelSize * 0.5f);
        float labelGap = Math.Max(4f, labelSize * 0.25f);

        IFont labelFont = new Font("Consolas", FontWeights.Bold);

        canvas.StrokeColor = color;
        canvas.StrokeSize = thickness;
        canvas.Antialias = true;
        canvas.FontColor = Colors.White;
        canvas.FillColor = Colors.White;
        canvas.FontSize = labelSize;
        canvas.Font = labelFont;

        float cellW = (float)imageWidth / cols;
        float cellH = (float)imageHeight / rows;

        // Vertical lines + X axis labels
        for (int c = 0; c <= cols; c++)
        {
            float x = rulerOffset + c * cellW;

            canvas.DrawLine(x, rulerOffset, x, rulerOffset + imageHeight);

            string label = c.ToString(CultureInfo.InvariantCulture);
            float textW = labelSize * label.Length * 0.75f;
            float textH = labelSize * 1.3f;
            canvas.DrawString(label,
                x - textW / 2, rulerOffset - tickLen - labelGap - textH, textW, textH,
                HorizontalAlignment.Center, VerticalAlignment.Center);

            canvas.DrawLine(x, rulerOffset - tickLen, x, rulerOffset);
        }

        // Horizontal lines + Y axis labels
        for (int r = 0; r <= rows; r++)
        {
            float y = rulerOffset + r * cellH;

            canvas.DrawLine(rulerOffset, y, rulerOffset + imageWidth, y);

            string label = r.ToString(CultureInfo.InvariantCulture);
            float textW = labelSize * label.Length * 0.75f;
            float textH = labelSize * 1.3f;
            canvas.DrawString(label,
                rulerOffset - tickLen - labelGap - textW, y - textH / 2, textW, textH,
                HorizontalAlignment.Right, VerticalAlignment.Center);

            canvas.DrawLine(rulerOffset - tickLen, y, rulerOffset, y);
        }
    }

    /// <summary>
    /// Creates a PNG image of the source cropped to <paramref name="view"/> with a grid
    /// overlay and axis labels drawn on top.
    /// </summary>
    private static byte[] CreateGridOverlayImage(
        IImage source,
        ViewRegion view,
        int cols = DefaultDivisions,
        int rows = DefaultDivisions,
        Color? gridColor = null,
        float? lineThickness = null,
        int minResolution = 0)
    {
        Color color = gridColor ?? Color.FromRgba(236, 72, 153, 102); // ~40% opacity pink
        (int imageWidth, int imageHeight) = minResolution > 0
            ? ComputeRenderedSize((int)view.Width, (int)view.Height, minResolution)
            : ((int)view.Width, (int)view.Height);
        int maxLabelValue = Math.Max(cols, rows);
        int rulerOffset = ComputeRulerOffset(imageWidth, maxLabelValue);
        int labelBuffer = ComputeLabelBuffer(imageWidth, maxLabelValue);
        int canvasWidth = imageWidth + rulerOffset + labelBuffer;
        int canvasHeight = imageHeight + rulerOffset + labelBuffer;

        using var context = new SkiaBitmapExportContext(canvasWidth, canvasHeight, 1.0f);
        ICanvas canvas = context.Canvas;

        // Black background
        canvas.FillColor = Colors.Black;
        canvas.FillRectangle(0, 0, canvasWidth, canvasHeight);

        // Draw the cropped view stretched to the full image area via clip + transform
        canvas.SaveState();
        canvas.ClipRectangle(rulerOffset, rulerOffset, imageWidth, imageHeight);
        canvas.Translate(rulerOffset, rulerOffset);
        canvas.Scale(imageWidth / (float)view.Width, imageHeight / (float)view.Height);
        canvas.Translate(-(float)view.X, -(float)view.Y);
        canvas.DrawImage(source, 0, 0, source.Width, source.Height);
        canvas.RestoreState();

        DrawRulerGrid(canvas, rulerOffset, imageWidth, imageHeight, cols, rows, color, lineThickness);

        using var ms = new MemoryStream();
        context.WriteToStream(ms);
        return ms.ToArray();
    }

    /// <summary>
    /// Determines whether another zoom iteration is warranted based on the AI's 
    /// estimated precision in pixels compared to a confidence threshold.
    /// </summary>
    private static bool ShouldContinueZooming(
        ViewRegion currentView,
        int cols = DefaultDivisions,
        int rows = DefaultDivisions,
        double confidencePixels = DefaultConfidencePixels,
        double aiEstimatePrecision = DefaultAIEstimatePrecision)
    {
        double cellPixelW = currentView.Width / cols;
        double cellPixelH = currentView.Height / rows;

        // Calculate the actual pixel margin of error based on the AI's fractional accuracy
        double errorMarginX = cellPixelW * aiEstimatePrecision;
        double errorMarginY = cellPixelH * aiEstimatePrecision;

        // Continue zooming only if the margin of error is still larger than our confidence threshold
        return errorMarginX > confidencePixels || errorMarginY > confidencePixels;
    }

    /// <summary>Zooms the current view to a sub-region defined by grid-cell start and span.</summary>
    private static ViewRegion ZoomToRegion(
        ViewRegion currentView,
        double startX,
        double startY,
        double spanX,
        double spanY,
        int cols,
        int rows)
    {
        double cellPixelW = currentView.Width / cols;
        double cellPixelH = currentView.Height / rows;

        return new ViewRegion(
            currentView.X + startX * cellPixelW,
            currentView.Y + startY * cellPixelH,
            spanX * cellPixelW,
            spanY * cellPixelH);
    }

    /// <summary>
    /// Calculates a contextual zoom region around the given coordinate and returns
    /// the zoomed <see cref="ViewRegion"/> for the next iteration.
    /// The zoom window is forced to a square so that wide source images (e.g. dual
    /// monitors) get a consistent buffer zone in both axes after the first zoom.
    /// </summary>
    private static ViewRegion CalculateZoomRegion(
        ViewRegion currentView,
        GridCoordinate coordinate,
        double sourceWidth,
        double sourceHeight,
        int cols = DefaultDivisions,
        int rows = DefaultDivisions)
    {
        double cellPixelW = currentView.Width / cols;
        double cellPixelH = currentView.Height / rows;

        // Center of the zoom in original image pixel coordinates
        double centerX = currentView.X + coordinate.X * cellPixelW;
        double centerY = currentView.Y + coordinate.Y * cellPixelH;

        // Square side: ±1 cell buffer using the larger cell dimension,
        // clamped so it never exceeds the source image in either axis.
        double sideLength = Math.Min(
            2.0 * Math.Max(cellPixelW, cellPixelH),
            Math.Min(sourceWidth, sourceHeight));

        double halfSide = sideLength / 2.0;
        double x = centerX - halfSide;
        double y = centerY - halfSide;

        // Shift to stay within source bounds
        if (x < 0) x = 0;
        if (y < 0) y = 0;
        if (x + sideLength > sourceWidth)  x = sourceWidth - sideLength;
        if (y + sideLength > sourceHeight) y = sourceHeight - sideLength;

        return new ViewRegion(x, y, sideLength, sideLength);
    }
}