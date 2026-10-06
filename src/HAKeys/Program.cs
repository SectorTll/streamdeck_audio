using System.Drawing;
using System.Drawing.Imaging;
using DeckKeys;

namespace HAKeys;

static class Program
{
    static async Task<int> Main(string[] args)
    {
        var pluginDir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, ".."));
        Log.Init(pluginDir, "hakeys");
        Glyphs.ImageDir = Path.Combine(pluginDir, "imgs", "glyphs");

        if (args.Length >= 2 && args[0] == "--icons") { WriteIcons(args[1]); return 0; }
        // `HAKeys.exe --import-glyph <source.png> <imgs/glyphs/id.png>`: any black-on-white / white-on-black / transparent icon -> plugin glyph
        if (args.Length >= 3 && args[0] == "--import-glyph") { GlyphImport.Convert(args[1], args[2]); return 0; }
        // `HAKeys.exe --selftest <url> <token>`: connect, list a few entities, exit.
        if (args.Length >= 3 && args[0] == "--selftest") return await SelfTest(args[1], args[2]);

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
            await using var ha = new HaClient();
            await using var sd = new StreamDeck(port, uuid, registerEvent);
            await sd.ConnectAsync(CancellationToken.None);
            var plugin = new Plugin(sd, ha);
            await plugin.Start();
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

    static async Task<int> SelfTest(string url, string token)
    {
        void Say(string s) { Console.WriteLine(s); Log.Info("selftest: " + s); }
        await using var ha = new HaClient();
        var connected = new TaskCompletionSource();
        ha.Changed += _ => { if (ha.Connected) connected.TrySetResult(); };
        ha.Configure(url, token);
        if (await Task.WhenAny(connected.Task, Task.Delay(8000)) != connected.Task)
        {
            Say($"FAIL not connected: {ha.LastError}");
            return 1;
        }
        Say($"ok   connected, {ha.States.Count} entities");
        foreach (var e in ha.States.Where(e => e.EntityId.StartsWith("switch.plug_")).OrderBy(e => e.EntityId))
            Say($"     {e.EntityId} = {e.State} ({e.Name})");
        Say("SELFTEST PASSED");
        return 0;
    }

    static void WriteIcons(string dir)
    {
        Directory.CreateDirectory(dir);
        var green = Color.FromArgb(60, 230, 80);
        Icon(dir, "plugin", "plug", green);
        Icon(dir, "category", "plug", green, 28);
        Icon(dir, "action-entity", "plug", green);

        static void Icon(string dir, string name, string glyph, Color led, int size = 72)
        {
            var uri = Renderer.Render(glyph, led, true);
            using var src = new Bitmap(new MemoryStream(Convert.FromBase64String(uri[(uri.IndexOf(',') + 1)..])));
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
