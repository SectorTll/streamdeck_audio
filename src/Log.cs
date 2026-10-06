namespace AudioKeys;

/// <summary>Tiny file logger: one line per entry, file next to the plugin in logs/.</summary>
static class Log
{
    static readonly object Gate = new();
    static string? _path;

    public static void Init(string pluginDir)
    {
        try
        {
            var dir = Path.Combine(pluginDir, "logs");
            Directory.CreateDirectory(dir);
            _path = Path.Combine(dir, "audiokeys.log");
            // keep the log small: rotate when it grows past 512 KB
            if (File.Exists(_path) && new FileInfo(_path).Length > 512 * 1024)
                File.Move(_path, Path.Combine(dir, "audiokeys.1.log"), overwrite: true);
        }
        catch { _path = null; }
    }

    public static void Info(string msg) => Write("inf", msg);
    public static void Warn(string msg) => Write("wrn", msg);
    public static void Error(string msg) => Write("err", msg);

    static void Write(string level, string msg)
    {
        if (_path is null) return;
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {level} {msg}{Environment.NewLine}";
        lock (Gate)
        {
            try { File.AppendAllText(_path, line); } catch { }
        }
    }
}
