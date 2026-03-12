using Microsoft.Maui.Graphics;
using Microsoft.Maui.Graphics.Skia;
using SkiaSharp;
using System.Globalization;

namespace Thio_Universal_Agent.Logic;

public sealed partial class CoordinatePrompter
{
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


    /// <summary>Returns a copy of the grid image with a crosshair marker drawn at the parsed coordinate.</summary>
    private static byte[] CreateAnnotatedImage(
        byte[] gridImageBytes,
        GridCoordinate coordinate,
        int imageWidth,
        int imageHeight,
        int cols = DefaultDivisions,
        int rows = DefaultDivisions)
    {
        using IImage gridImage = LoadImage(gridImageBytes);
        int w = (int)gridImage.Width;
        int h = (int)gridImage.Height;

        using var context = new SkiaBitmapExportContext(w, h, 1.0f);
        ICanvas canvas = context.Canvas;

        canvas.DrawImage(gridImage, 0, 0, w, h);

        float cellW = (float)imageWidth / cols;
        float cellH = (float)imageHeight / rows;
        float cx = RulerOffset + (float)coordinate.X * cellW;
        float cy = RulerOffset + (float)coordinate.Y * cellH;
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
        int imageWidth,
        int imageHeight,
        int cols,
        int rows,
        Color color,
        float? lineThickness)
    {
        float thickness = lineThickness ?? Math.Max(1f, imageWidth / 1200f);
        float labelSize = Math.Max(16f, imageWidth / 60f);

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
            float x = RulerOffset + c * cellW;

            canvas.DrawLine(x, RulerOffset, x, RulerOffset + imageHeight);

            string label = c.ToString(CultureInfo.InvariantCulture);
            float textW = labelSize * label.Length * 0.75f;
            float textH = labelSize * 1.3f;
            canvas.DrawString(label,
                x - textW / 2, RulerOffset - 15 - textH, textW, textH,
                HorizontalAlignment.Center, VerticalAlignment.Center);

            canvas.DrawLine(x, RulerOffset - 8, x, RulerOffset);
        }

        // Horizontal lines + Y axis labels
        for (int r = 0; r <= rows; r++)
        {
            float y = RulerOffset + r * cellH;

            canvas.DrawLine(RulerOffset, y, RulerOffset + imageWidth, y);

            string label = r.ToString(CultureInfo.InvariantCulture);
            float textW = labelSize * label.Length * 0.75f;
            float textH = labelSize * 1.3f;
            canvas.DrawString(label,
                RulerOffset - 25 - textW, y - textH / 2, textW, textH,
                HorizontalAlignment.Right, VerticalAlignment.Center);

            canvas.DrawLine(RulerOffset - 8, y, RulerOffset, y);
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
        float? lineThickness = null)
    {
        Color color = gridColor ?? Color.FromRgba(236, 72, 153, 102); // ~40% opacity pink
        int imageWidth = (int)source.Width;
        int imageHeight = (int)source.Height;
        int canvasWidth = imageWidth + RulerOffset + LabelBuffer;
        int canvasHeight = imageHeight + RulerOffset + LabelBuffer;

        using var context = new SkiaBitmapExportContext(canvasWidth, canvasHeight, 1.0f);
        ICanvas canvas = context.Canvas;

        // Black background
        canvas.FillColor = Colors.Black;
        canvas.FillRectangle(0, 0, canvasWidth, canvasHeight);

        // Draw the cropped view stretched to the full image area via clip + transform
        canvas.SaveState();
        canvas.ClipRectangle(RulerOffset, RulerOffset, imageWidth, imageHeight);
        canvas.Translate(RulerOffset, RulerOffset);
        canvas.Scale(imageWidth / (float)view.Width, imageHeight / (float)view.Height);
        canvas.Translate(-(float)view.X, -(float)view.Y);
        canvas.DrawImage(source, 0, 0, source.Width, source.Height);
        canvas.RestoreState();

        DrawRulerGrid(canvas, imageWidth, imageHeight, cols, rows, color, lineThickness);

        using var ms = new MemoryStream();
        context.WriteToStream(ms);
        return ms.ToArray();
    }
}