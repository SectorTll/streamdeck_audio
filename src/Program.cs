using System.Drawing;
using System.Drawing.Imaging;

namespace AudioKeys;

static class Program
{
    [MTAThread]
    static async Task<int> Main(string[] args)
    {
        var pluginDir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, ".."));
        Log.Init(pluginDir);

        // `AudioKeys.exe --icons <dir>` writes the manifest icons using the same renderer, then exits.
        if (args.Length >= 2 && args[0] == "--icons") { WriteIcons(args[1]); return 0; }
        // `AudioKeys.exe --preview <file.png>` renders every glyph in the three states into one sheet (for eyeballing).
        if (args.Length >= 2 && args[0] == "--preview") { WritePreview(args[1]); return 0; }

        int port = 0; string? uuid = null, registerEvent = null;
        for (int i = 0; i + 1 < args.Length; i += 2)
        {
            switch (args[i])
            {
                case "-port": port = int.Parse(args[i + 1]); break;
                case "-pluginUUID": uuid = args[i + 1]; break;
                case "-registerEvent": registerEvent = args[i + 1]; break;
            }
        }
        if (port == 0 || uuid is null || registerEvent is null)
        {
            Log.Error("missing -port/-pluginUUID/-registerEvent (started outside Stream Deck?)");
            return 2;
        }

        Log.Info($"start v{typeof(Program).Assembly.GetName().Version} pid {Environment.ProcessId}");
        try
        {
            using var audio = new CoreAudio();
            await using var sd = new StreamDeck(port, uuid, registerEvent);
            await sd.ConnectAsync(CancellationToken.None);
            _ = new Plugin(sd, audio);
            await sd.RunAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            Log.Error($"fatal: {ex}");
            return 1;
        }
        Log.Info("exit");
        return 0;
    }

    static void WritePreview(string file)
    {
        const int S = Renderer.Size, pad = 10;
        var states = new (Color c, bool on)[] { (Color.FromArgb(60, 230, 80), true), (Color.FromArgb(60, 230, 80), false), (Color.FromArgb(255, 210, 0), true) };
        var glyphs = Glyphs.Ids;
        using var sheet = new Bitmap(pad + glyphs.Length * (S + pad), pad + states.Length * (S + pad));
        using var g = Graphics.FromImage(sheet);
        g.Clear(Color.FromArgb(40, 40, 40));
        for (int gi = 0; gi < glyphs.Length; gi++)
            for (int si = 0; si < states.Length; si++)
            {
                var uri = Renderer.Render(glyphs[gi], states[si].c, states[si].on, si == 2 ? glyphs[gi] : null);
                using var key = new Bitmap(new MemoryStream(Convert.FromBase64String(uri[(uri.IndexOf(',') + 1)..])));
                g.DrawImage(key, pad + gi * (S + pad), pad + si * (S + pad));
            }
        sheet.Save(file, ImageFormat.Png);
    }

    static void WriteIcons(string dir)
    {
        Directory.CreateDirectory(dir);
        var green = Color.FromArgb(60, 230, 80);
        Save(Path.Combine(dir, "action@2x.png"), 144, green);
        Save(Path.Combine(dir, "action.png"), 72, green);
        Save(Path.Combine(dir, "plugin@2x.png"), 144, green);
        Save(Path.Combine(dir, "plugin.png"), 72, green);
        Save(Path.Combine(dir, "category@2x.png"), 56, green);
        Save(Path.Combine(dir, "category.png"), 28, green);

        static void Save(string path, int size, Color led)
        {
            var uri = Renderer.Render("headphones", led, true);
            var bytes = Convert.FromBase64String(uri[(uri.IndexOf(',') + 1)..]);
            using var src = new Bitmap(new MemoryStream(bytes));
            using var dst = new Bitmap(size, size);
            using (var g = Graphics.FromImage(dst))
            {
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                g.DrawImage(src, 0, 0, size, size);
            }
            dst.Save(path, ImageFormat.Png);
        }
    }
}
