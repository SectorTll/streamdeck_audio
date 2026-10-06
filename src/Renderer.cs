using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Collections.Concurrent;

namespace AudioKeys;

enum KeyState { Active, Inactive, Absent }

/// <summary>
/// Draws a 144x144 key: a glyph in the middle and a status LED bar at the top that looks like a
/// real LED behind frosted glass (bright core, dark ends, slightly soft edges, no outer glow).
/// The LED can be fully lit, off, or partially lit from the left (volume meter).
/// Rendered images are cached as ready-to-send data URIs, so a repeated state costs a dictionary lookup.
/// </summary>
static class Renderer
{
    public const int Size = 144;
    static readonly ConcurrentDictionary<string, string> Cache = new();
    public static readonly string Hidden = Encode(RenderBlank());

    /// <param name="fill">lit fraction of the LED bar, 0..1 (only when ledOn)</param>
    public static string Render(string glyph, Color led, bool ledOn, string? label = null, float fill = 1f)
    {
        fill = ledOn ? MathF.Round(Math.Clamp(fill, 0f, 1f), 2) : 0f;
        var key = $"{glyph}|{led.ToArgb():X8}|{(ledOn ? 1 : 0)}|{fill}|{label}";
        return Cache.GetOrAdd(key, _ => Encode(Draw(glyph, led, ledOn, label, fill)));
    }

    public static Color ParseColor(string? hex, Color fallback)
    {
        if (string.IsNullOrWhiteSpace(hex)) return fallback;
        try { return ColorTranslator.FromHtml(hex.Trim()); } catch { return fallback; }
    }

    static string Encode(Bitmap bmp)
    {
        using (bmp)
        using (var ms = new MemoryStream())
        {
            bmp.Save(ms, ImageFormat.Png);
            return "data:image/png;base64," + Convert.ToBase64String(ms.ToArray());
        }
    }

    static Bitmap RenderBlank()
    {
        var bmp = new Bitmap(Size, Size);
        using var g = Graphics.FromImage(bmp);
        g.Clear(Color.Black);
        return bmp;
    }

    static Bitmap Draw(string glyph, Color led, bool ledOn, string? label, float fill)
    {
        var bmp = new Bitmap(Size, Size);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.Clear(Color.Black);
        Glyphs.Draw(g, glyph, label is null ? 0 : -8);
        if (!ledOn || fill >= 1f) DrawLed(g, led, ledOn);
        else
        {
            DrawLed(g, led, false);                       // the whole bar, dark
            if (fill > 0f)
            {
                var litWidth = 92f * fill;
                g.SetClip(new RectangleF(26 - 4, 0, litWidth + 4, 40));
                DrawLed(g, led, true);                    // the lit part, clipped from the left
                g.ResetClip();
            }
        }
        if (label is not null) DrawLabel(g, label);
        return bmp;
    }

    // ---- LED bar -------------------------------------------------------------------------

    static void DrawLed(Graphics g, Color c, bool on)
    {
        const float lx = 26, ly = 11, lw = 92, lh = 11;

        // soft edge: three slightly larger, very transparent layers (≈2 px blur), no glow beyond that
        for (int k = 3; k >= 1; k--)
        {
            var layer = Mix(c, on ? 0.8 : 0.18, 36 - k * 9);
            using var b = new SolidBrush(layer);
            using var p = RoundRect(lx - k * 0.8f, ly - k * 0.8f, lw + k * 1.6f, lh + k * 1.6f, 6);
            g.FillPath(b, p);
        }

        var edge = Mix(c, on ? 0.10 : 0.05);
        var mid = Mix(c, on ? 1.0 : 0.20);
        var core = on ? Toward(c, 0.35) : Mix(c, 0.24);

        using var lg = new LinearGradientBrush(new RectangleF(lx, ly, lw, lh), edge, edge, LinearGradientMode.Horizontal);
        lg.InterpolationColors = new ColorBlend
        {
            Colors = new[] { edge, mid, core, core, mid, edge },
            Positions = new[] { 0f, 0.22f, 0.42f, 0.58f, 0.78f, 1f }
        };
        using var path = RoundRect(lx, ly, lw, lh, 5.5f);
        g.FillPath(lg, path);
    }

    static void DrawLabel(Graphics g, string text)
    {
        using var f = new Font("Segoe UI", 16, FontStyle.Bold, GraphicsUnit.Pixel);
        using var b = new SolidBrush(Color.FromArgb(205, 205, 205));
        var fmt = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Far, Trimming = StringTrimming.EllipsisCharacter };
        fmt.FormatFlags |= StringFormatFlags.NoWrap;
        g.DrawString(text, f, b, new RectangleF(4, Size - 28, Size - 8, 24), fmt);
    }

    static Color Mix(Color c, double f, int a = 255) =>
        Color.FromArgb(a, Clamp(c.R * f), Clamp(c.G * f), Clamp(c.B * f));

    static Color Toward(Color c, double t) =>
        Color.FromArgb(255, Clamp(c.R + (255 - c.R) * t), Clamp(c.G + (255 - c.G) * t), Clamp(c.B + (255 - c.B) * t));

    static int Clamp(double v) => (int)Math.Max(0, Math.Min(255, v));

    public static GraphicsPath RoundRect(float x, float y, float w, float h, float r)
    {
        var p = new GraphicsPath();
        p.AddArc(x, y, r * 2, r * 2, 180, 90);
        p.AddArc(x + w - r * 2, y, r * 2, r * 2, 270, 90);
        p.AddArc(x + w - r * 2, y + h - r * 2, r * 2, r * 2, 0, 90);
        p.AddArc(x, y + h - r * 2, r * 2, r * 2, 90, 90);
        p.CloseFigure();
        return p;
    }
}

/// <summary>Glyphs drawn with primitives so they scale to any colour/size and need no image files.</summary>
static class Glyphs
{
    /// <summary>Glyphs the user can pick for a device key.</summary>
    public static readonly string[] DeviceIds = { "headphones", "speaker", "speakers", "monitor", "vr", "headset", "usb", "bluetooth", "none" };
    /// <summary>Everything, for the preview sheet.</summary>
    public static readonly string[] Ids = { "headphones", "speaker", "speakers", "monitor", "vr", "headset", "usb", "bluetooth", "vol-up", "vol-down", "vol-mute", "vol-set", "play", "pause", "play-pause", "none" };
    static readonly Color Ink = Color.FromArgb(225, 225, 225);

    public static void Draw(Graphics g, string id, float dy)
    {
        g.TranslateTransform(0, dy);
        switch (id)
        {
            case "headphones": Headphones(g); break;
            case "speaker": Speaker(g, 72, 86); break;
            case "speakers": Speaker(g, 48, 86, 0.8f); Speaker(g, 96, 86, 0.8f); break;
            case "monitor": Monitor(g); break;
            case "vr": Vr(g); break;
            case "headset": Headphones(g); Mic(g); break;
            case "usb": Usb(g); break;
            case "bluetooth": Bluetooth(g); break;
            case "vol-up": SpeakerBody(g, 56, 86); Waves(g, 56, 86, 3); break;
            case "vol-down": SpeakerBody(g, 60, 86); Waves(g, 60, 86, 1); break;
            case "vol-mute": SpeakerBody(g, 56, 86); Cross(g, 96, 86); break;
            case "vol-set": SpeakerBody(g, 56, 86); Waves(g, 56, 86, 2); break;
            case "play": Play(g, 72, 86); break;
            case "pause": Pause(g, 72, 86); break;
            case "play-pause": Play(g, 54, 86, 0.8f); Pause(g, 98, 86, 0.8f); break;
            case "none": break;
            default: Headphones(g); break;
        }
        g.ResetTransform();
    }

    static Pen Stroke(float w) => new(Ink, w) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };

    static void Headphones(Graphics g)
    {
        using var pen = Stroke(7);
        using var ink = new SolidBrush(Ink);
        using var black = new SolidBrush(Color.Black);
        g.DrawArc(pen, 34, 46, 76, 76, 190, 160);
        using (var l = Renderer.RoundRect(30, 84, 20, 32, 6)) g.FillPath(ink, l);
        using (var r = Renderer.RoundRect(94, 84, 20, 32, 6)) g.FillPath(ink, r);
        using (var l = Renderer.RoundRect(36, 90, 8, 20, 3)) g.FillPath(black, l);
        using (var r = Renderer.RoundRect(100, 90, 8, 20, 3)) g.FillPath(black, r);
    }

    static void Mic(Graphics g)
    {
        using var pen = Stroke(6);
        g.DrawArc(pen, 94, 100, 26, 30, 60, 90);   // boom from right cup
        g.DrawLine(pen, 72, 124, 86, 124);          // boom tip
    }

    /// <summary>Speaker body (box + cone) centred around (cx, cy); the cone's right edge is at cx+12.</summary>
    static void SpeakerBody(Graphics g, float cx, float cy, float s = 1f)
    {
        using var ink = new SolidBrush(Ink);
        var w = 20 * s; var h = 26 * s;
        using (var body = Renderer.RoundRect(cx - 26 * s, cy - h / 2, w, h, 3 * s)) g.FillPath(ink, body);
        var cone = new[] { new PointF(cx - 6 * s, cy - h / 2), new PointF(cx + 12 * s, cy - 28 * s), new PointF(cx + 12 * s, cy + 28 * s), new PointF(cx - 6 * s, cy + h / 2) };
        g.FillPolygon(ink, cone);
    }

    static void Waves(Graphics g, float cx, float cy, int count, float s = 1f)
    {
        using var pen = Stroke(7 * s);
        if (count >= 1) g.DrawArc(pen, cx + 10 * s, cy - 22 * s, 32 * s, 44 * s, -55, 110);
        if (count >= 2) g.DrawArc(pen, cx + 14 * s, cy - 36 * s, 50 * s, 72 * s, -50, 100);
        if (count >= 3) g.DrawArc(pen, cx + 18 * s, cy - 50 * s, 68 * s, 100 * s, -45, 90);
    }

    static void Speaker(Graphics g, float cx, float cy, float s = 1f)
    {
        SpeakerBody(g, cx, cy, s);
        Waves(g, cx, cy, s >= 1f ? 2 : 1, s);
    }

    static void Cross(Graphics g, float cx, float cy)
    {
        using var pen = Stroke(8);
        g.DrawLine(pen, cx - 14, cy - 14, cx + 14, cy + 14);
        g.DrawLine(pen, cx + 14, cy - 14, cx - 14, cy + 14);
    }

    static void Play(Graphics g, float cx, float cy, float s = 1f)
    {
        using var ink = new SolidBrush(Ink);
        g.FillPolygon(ink, new[] { new PointF(cx - 22 * s, cy - 30 * s), new PointF(cx + 28 * s, cy), new PointF(cx - 22 * s, cy + 30 * s) });
    }

    static void Pause(Graphics g, float cx, float cy, float s = 1f)
    {
        using var ink = new SolidBrush(Ink);
        using (var l = Renderer.RoundRect(cx - 24 * s, cy - 30 * s, 18 * s, 60 * s, 4 * s)) g.FillPath(ink, l);
        using (var r = Renderer.RoundRect(cx + 6 * s, cy - 30 * s, 18 * s, 60 * s, 4 * s)) g.FillPath(ink, r);
    }

    static void Monitor(Graphics g)
    {
        using var pen = Stroke(7);
        using var frame = Renderer.RoundRect(30, 48, 84, 56, 6);
        g.DrawPath(pen, frame);
        g.DrawLine(pen, 72, 104, 72, 118);
        g.DrawLine(pen, 52, 120, 92, 120);
    }

    static void Vr(Graphics g)
    {
        using var pen = Stroke(7);
        using var ink = new SolidBrush(Ink);
        using var black = new SolidBrush(Color.Black);
        using (var body = Renderer.RoundRect(26, 60, 92, 50, 14)) g.FillPath(ink, body);
        g.FillEllipse(black, 40, 72, 24, 24);
        g.FillEllipse(black, 80, 72, 24, 24);
        g.FillEllipse(black, 60, 98, 24, 24); // nose cut-out at the bottom edge
        g.DrawLine(pen, 30, 70, 20, 90);
        g.DrawLine(pen, 114, 70, 124, 90);
    }

    static void Usb(Graphics g)
    {
        using var pen = Stroke(6);
        using var ink = new SolidBrush(Ink);
        g.DrawLine(pen, 72, 50, 72, 118);                 // trunk
        g.FillPolygon(ink, new[] { new PointF(72, 40), new PointF(62, 56), new PointF(82, 56) });
        g.DrawLine(pen, 72, 86, 50, 72); g.FillEllipse(ink, 40, 62, 16, 16);
        g.DrawLine(pen, 72, 100, 94, 84); g.FillRectangle(ink, 92, 70, 16, 16);
        g.FillEllipse(ink, 61, 108, 22, 22);
    }

    static void Bluetooth(Graphics g)
    {
        using var pen = Stroke(7);
        var pts = new[] { new PointF(50, 64), new PointF(94, 104), new PointF(72, 124), new PointF(72, 40), new PointF(94, 60), new PointF(50, 100) };
        g.DrawLines(pen, pts);
    }
}
