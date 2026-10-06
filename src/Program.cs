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
            using var volume = new VolumeMonitor(audio);
            var media = new MediaMonitor();
            await media.StartAsync();
            await using var sd = new StreamDeck(port, uuid, registerEvent);
            await sd.ConnectAsync(CancellationToken.None);
            _ = new Plugin(sd, audio, volume, media);
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
        var glyphs = Glyphs.Ids;
        using var sheet = new Bitmap(pad + glyphs.Length * (S + pad), pad + 4 * (S + pad));
        using var g = Graphics.FromImage(sheet);
        g.Clear(Color.FromArgb(40, 40, 40));
        var green = Color.FromArgb(60, 230, 80); var yellow = Color.FromArgb(255, 210, 0);
        for (int gi = 0; gi < glyphs.Length; gi++)
        {
            var rows = new[]
            {
                Renderer.Render(glyphs[gi], green, true),
                Renderer.Render(glyphs[gi], green, false),
                Renderer.Render(glyphs[gi], yellow, true, glyphs[gi]),
                Renderer.Render(glyphs[gi], green, true, "45%", 0.45f),
            };
            for (int si = 0; si < rows.Length; si++)
            {
                using var key = new Bitmap(new MemoryStream(Convert.FromBase64String(rows[si][(rows[si].IndexOf(',') + 1)..])));
                g.DrawImage(key, pad + gi * (S + pad), pad + si * (S + pad));
            }
        }
        sheet.Save(file, ImageFormat.Png);
    }

    static void WriteIcons(string dir)
    {
        Directory.CreateDirectory(dir);
        var green = Color.FromArgb(60, 230, 80);
        Icon(dir, "plugin", "headphones", green, true, 1f);
        Icon(dir, "category", "headphones", green, true, 1f, 28);
        Icon(dir, "action-output", "headphones", green, true, 1f);
        Icon(dir, "action-vol-up", "vol-up", green, true, 0.7f);
        Icon(dir, "action-vol-down", "vol-down", green, true, 0.3f);
        Icon(dir, "action-vol-set", "vol-set", green, true, 0.5f);
        Icon(dir, "action-mute", "vol-mute", Color.FromArgb(255, 59, 48), true, 1f);
        Icon(dir, "action-play-pause", "play-pause", green, true, 1f);

        static void Icon(string dir, string name, string glyph, Color led, bool on, float fill, int size = 72)
        {
            var uri = Renderer.Render(glyph, led, on, null, fill);
            var bytes = Convert.FromBase64String(uri[(uri.IndexOf(',') + 1)..]);
            using var src = new Bitmap(new MemoryStream(bytes));
            Save(Path.Combine(dir, name + "@2x.png"), src, size * 2);
            Save(Path.Combine(dir, name + ".png"), src, size);
        }

        static void Save(string path, Bitmap src, int size)
        {
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
