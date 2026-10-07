using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Collections.Concurrent;

namespace DeckKeys;

public enum KeyState { Active, Inactive, Absent }

/// <summary>
/// Draws a 144x144 key: a glyph in the middle and a status LED bar at the top that looks like a
/// real LED behind frosted glass (bright core, dark ends, slightly soft edges, no outer glow).
/// The LED can be fully lit, off, or partially lit from the left (volume meter).
/// Rendered images are cached as ready-to-send data URIs, so a repeated state costs a dictionary lookup.
/// </summary>
public static class Renderer
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
public static class Glyphs
{
    /// <summary>Glyphs the user can pick for a device key.</summary>
    public static readonly string[] DeviceIds = { "headphones", "speaker", "speakers", "monitor", "vr", "headset", "usb", "bluetooth", "none" };
    /// <summary>Glyphs for Home Assistant entity keys.</summary>
    public static readonly string[] HomeIds = { "plug", "lamp", "floor", "window", "vr", "fan", "power", "monitor", "none" };
    /// <summary>Everything, for the preview sheet.</summary>
    public static readonly string[] Ids = { "headphones", "speaker", "speakers", "monitor", "vr", "headset", "usb", "bluetooth", "mic", "mic-desk", "webcam", "vol-up", "vol-down", "vol-mute", "vol-set", "play", "pause", "play-pause", "plug", "lamp", "floor", "window", "fan", "power", "none" };
    static readonly Color Ink = Color.FromArgb(225, 225, 225);

    /// <summary>Folder with optional PNG glyphs (white on transparent); a file &lt;id&gt;.png overrides the drawn glyph.</summary>
    public static string? ImageDir { get; set; }
    static readonly ConcurrentDictionary<string, Bitmap?> ImageCache = new();

    static Bitmap? ImageFor(string id)
    {
        if (ImageDir is null) return null;
        return ImageCache.GetOrAdd(id, k =>
        {
            var path = Path.Combine(ImageDir, k + ".png");
            if (!File.Exists(path)) return null;
            try { using var fs = File.OpenRead(path); return new Bitmap(fs); }
            catch { return null; }
        });
    }

    public static void Draw(Graphics g, string id, float dy)
    {
        g.TranslateTransform(0, dy);
        if (ImageFor(id) is { } img)
        {
            // files are prepared at 144x144 with the glyph already placed; draw 1:1
            lock (img) g.DrawImage(img, 0, 0, Renderer.Size, Renderer.Size);
            g.ResetTransform();
            return;
        }
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
            case "mic": Mic2(g); break;
            case "mic-desk": MicDesk(g); break;
            case "webcam": Webcam(g); break;
            case "vol-up": SpeakerBody(g, 56, 86); Waves(g, 56, 86, 3); break;
            case "vol-down": SpeakerBody(g, 60, 86); Waves(g, 60, 86, 1); break;
            case "vol-mute": SpeakerBody(g, 56, 86); Cross(g, 96, 86); break;
            case "vol-set": SpeakerBody(g, 56, 86); Waves(g, 56, 86, 2); break;
            case "play": Play(g, 72, 86); break;
            case "pause": Pause(g, 72, 86); break;
            case "play-pause": Play(g, 54, 86, 0.8f); Pause(g, 98, 86, 0.8f); break;
            case "plug": Plug(g); break;
            case "lamp": Lamp(g); break;
            case "floor": Floor(g); break;
            case "window": Window(g); break;
            case "fan": Fan(g); break;
            case "power": Power(g); break;
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

    // ---- microphone glyphs ---------------------------------------------------------------

    /// <summary>Classic handheld/studio microphone: capsule, cradle arc, stand.</summary>
    static void Mic2(Graphics g)
    {
        using var pen = Stroke(7);
        using var ink = new SolidBrush(Ink);
        using (var capsule = Renderer.RoundRect(58, 40, 28, 52, 14)) g.FillPath(ink, capsule);
        g.DrawArc(pen, 46, 56, 52, 52, 0, 180);     // cradle
        g.DrawLine(pen, 72, 108, 72, 122);           // stem
        g.DrawLine(pen, 56, 122, 88, 122);           // base
    }

    /// <summary>Desk / USB condenser mic on a short stand with a side-address grille.</summary>
    static void MicDesk(Graphics g)
    {
        using var pen = Stroke(7);
        using var thin = Stroke(4);
        using var ink = new SolidBrush(Ink);
        using var black = new SolidBrush(Color.Black);
        using (var body = Renderer.RoundRect(50, 40, 44, 60, 12)) g.FillPath(ink, body);
        foreach (var y in new float[] { 54, 66, 78 }) { using var p = new Pen(Color.Black, 3); g.DrawLine(p, 58, y, 86, y); }   // grille
        g.DrawLine(pen, 72, 100, 72, 114);           // stem
        g.DrawArc(pen, 46, 104, 52, 24, 0, 180);     // tripod-ish base
    }

    /// <summary>Webcam: a lens on a small clip.</summary>
    static void Webcam(Graphics g)
    {
        using var pen = Stroke(7);
        using var ink = new SolidBrush(Ink);
        using var black = new SolidBrush(Color.Black);
        g.FillEllipse(ink, 42, 44, 60, 60);
        g.FillEllipse(black, 58, 60, 28, 28);
        g.FillEllipse(ink, 66, 68, 10, 10);
        g.DrawLine(pen, 72, 104, 72, 114);
        g.DrawLine(pen, 54, 118, 90, 118);
    }

    // ---- home glyphs ---------------------------------------------------------------------

    static void Plug(Graphics g)
    {
        using var pen = Stroke(7);
        using var ink = new SolidBrush(Ink);
        g.DrawLine(pen, 58, 42, 58, 66);   // prongs
        g.DrawLine(pen, 86, 42, 86, 66);
        using (var body = Renderer.RoundRect(42, 64, 60, 34, 10)) g.FillPath(ink, body);   // plug body
        g.FillRectangle(ink, 56, 96, 32, 10);                                              // neck
        g.DrawLine(pen, 72, 104, 72, 124);                                                 // cord
    }

    /// <summary>Small desk lamp: round base, bent arm, cone shade aimed down-left with a fan of light.</summary>
    static void Lamp(Graphics g)
    {
        using var pen = Stroke(7);
        using var thin = Stroke(4);
        using var ink = new SolidBrush(Ink);
        using (var b = Renderer.RoundRect(66, 112, 50, 10, 5)) g.FillPath(ink, b);          // base
        using (var arm = new GraphicsPath())
        {
            arm.AddBezier(94, 112, 104, 84, 98, 62, 72, 52);                                 // arm up to the shade
            g.DrawPath(pen, arm);
        }
        g.TranslateTransform(62, 58);
        g.RotateTransform(-38);
        var cone = new[] { new PointF(-11, -14), new PointF(11, -14), new PointF(24, 14), new PointF(-24, 14) };
        g.FillPolygon(ink, cone);                                                            // shade (cone, opening down)
        g.DrawLine(thin, -14, 20, -20, 40);                                                  // light fan
        g.DrawLine(thin, 0, 20, 0, 42);
        g.DrawLine(thin, 14, 20, 20, 40);
        g.ResetTransform();
    }

    /// <summary>LED strip along the baseboard: wall/floor corner, a bright strip at the bottom, light rising up the wall.</summary>
    static void Floor(Graphics g)
    {
        using var pen = Stroke(7);
        using var ink = new SolidBrush(Ink);
        g.DrawLine(pen, 26, 40, 26, 114);                                                    // wall edge
        g.DrawLine(pen, 26, 114, 120, 114);                                                  // floor
        using (var strip = Renderer.RoundRect(34, 100, 80, 9, 4)) g.FillPath(ink, strip);    // the strip
        for (int i = 0; i < 7; i++)                                                          // light rising up the wall
        {
            float x = 40 + i * 11.5f;
            float h = (i % 2 == 0) ? 22 : 14;
            using var ray = new Pen(Color.FromArgb(i % 2 == 0 ? 225 : 150, Ink), 3.5f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            g.DrawLine(ray, x, 92, x, 92 - h);
        }
    }

    /// <summary>Small floor lamp standing next to a window.</summary>
    static void Window(Graphics g)
    {
        using var pen = Stroke(6);
        using var ink = new SolidBrush(Ink);
        using (var frame = Renderer.RoundRect(30, 40, 50, 60, 3)) g.DrawPath(pen, frame);    // window
        g.DrawLine(pen, 55, 40, 55, 100);
        g.DrawLine(pen, 30, 70, 80, 70);
        g.DrawLine(pen, 104, 70, 104, 118);                                                  // lamp pole
        using (var b = Renderer.RoundRect(90, 116, 28, 8, 4)) g.FillPath(ink, b);            // base
        var shade = new[] { new PointF(92, 44), new PointF(116, 44), new PointF(122, 70), new PointF(86, 70) };
        g.FillPolygon(ink, shade);                                                           // shade
    }

    static void Fan(Graphics g)
    {
        using var ink = new SolidBrush(Ink);
        using var black = new SolidBrush(Color.Black);
        for (int i = 0; i < 3; i++)
        {
            g.TranslateTransform(72, 82);
            g.RotateTransform(i * 120);
            using var blade = new GraphicsPath();
            blade.AddBezier(0, -8, 18, -44, 46, -40, 38, -6);
            blade.AddBezier(38, -6, 24, 4, 10, 6, 0, -8);
            g.FillPath(ink, blade);
            g.RotateTransform(-i * 120);
            g.TranslateTransform(-72, -82);
        }
        g.FillEllipse(ink, 60, 70, 24, 24);
        g.FillEllipse(black, 68, 78, 8, 8);
    }

    static void Power(Graphics g)
    {
        using var pen = Stroke(8);
        g.DrawArc(pen, 38, 48, 68, 68, -60, 300);
        g.DrawLine(pen, 72, 40, 72, 80);
    }

    static void Bluetooth(Graphics g)
    {
        using var pen = Stroke(7);
        var pts = new[] { new PointF(50, 64), new PointF(94, 104), new PointF(72, 124), new PointF(72, 40), new PointF(94, 60), new PointF(50, 100) };
        g.DrawLines(pen, pts);
    }
}
