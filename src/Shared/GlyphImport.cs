using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace DeckKeys;

/// <summary>
/// Turns an arbitrary icon (black on white, black on transparent, white on black…) into a plugin
/// glyph: light-grey ink on a transparent 144x144 canvas, content fitted into the glyph box below the LED.
/// </summary>
public static class GlyphImport
{
    const int Canvas = Renderer.Size;
    const float BoxSize = 96f, CenterX = 72f, CenterY = 86f;
    static readonly Color Ink = Color.FromArgb(225, 225, 225);

    public static void Convert(string sourcePath, string targetPath)
    {
        using var src = new Bitmap(sourcePath);
        int w = src.Width, h = src.Height;

        // Which pixels are ink? Mostly-transparent image: the opaque ones. Mostly-opaque image: the bright ones on a dark
        // background, or the dark ones on a light background (whichever is the minority).
        long transparent = 0, bright = 0, dark = 0;
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                var p = src.GetPixel(x, y);
                if (p.A < 32) { transparent++; continue; }
                if (p.R + p.G + p.B > 384) bright++; else dark++;
            }
        var total = (long)w * h;
        // "holes": a solid dark shape whose strokes are cut out (transparent) — ink is the transparent area inside the shape
        var mode = transparent > total / 2 ? "alpha"
                 : bright > dark ? "dark-ink"
                 : bright < total / 100 ? "holes"
                 : "bright-ink";

        // for "holes": the shape's own anti-aliased rim must not count as ink — stay well inside the opaque outline
        const int inset = 10;
        int oMinY = h, oMaxY = -1;
        if (mode == "holes")
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                    if (src.GetPixel(x, y).A >= 200) { if (y < oMinY) oMinY = y; if (y > oMaxY) oMaxY = y; break; }

        using var mask = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        int minX = w, minY = h, maxX = -1, maxY = -1;
        for (int y = 0; y < h; y++)
        {
            int rowFirst = -1, rowLast = -1;
            if (mode == "holes" && y >= oMinY + inset && y <= oMaxY - inset)
            {
                for (int x = 0; x < w; x++) if (src.GetPixel(x, y).A >= 200) { if (rowFirst < 0) rowFirst = x; rowLast = x; }
                rowFirst += inset; rowLast -= inset;
            }
            for (int x = 0; x < w; x++)
            {
                var p = src.GetPixel(x, y);
                double lum = (p.R + p.G + p.B) / (3.0 * 255), a = p.A / 255.0;
                double s = mode switch
                {
                    "alpha" => a,                                            // opaque = ink, colour ignored
                    "dark-ink" => a * (1 - lum),                             // black strokes on light background
                    "holes" => rowFirst >= 0 && x > rowFirst && x < rowLast ? 1 - a : 0,   // cut-outs inside the shape
                    _ => a * lum,                                            // white strokes on dark background
                };
                var ai = (int)Math.Round(255 * Math.Clamp(s, 0, 1));
                if (ai > 8) { if (x < minX) minX = x; if (x > maxX) maxX = x; if (y < minY) minY = y; if (y > maxY) maxY = y; }
                mask.SetPixel(x, y, Color.FromArgb(ai, Ink));
            }
        }
        if (maxX < 0) throw new InvalidOperationException("no ink found in " + sourcePath);

        var cw = maxX - minX + 1; var ch = maxY - minY + 1;
        var scale = Math.Min(BoxSize / cw, BoxSize / ch);
        var dw = cw * scale; var dh = ch * scale;

        using var dst = new Bitmap(Canvas, Canvas, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(dst))
        {
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.DrawImage(mask, new RectangleF(CenterX - dw / 2, CenterY - dh / 2, dw, dh), new RectangleF(minX, minY, cw, ch), GraphicsUnit.Pixel);
        }
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(targetPath))!);
        dst.Save(targetPath, ImageFormat.Png);
        Console.WriteLine($"{Path.GetFileName(sourcePath)} -> {targetPath}  mode={mode} content={cw}x{ch}");
    }
}
