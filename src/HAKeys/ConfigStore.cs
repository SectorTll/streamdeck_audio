using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DeckKeys;

namespace HAKeys;

/// <summary>
/// The plugin's own copy of the connection settings, encrypted with DPAPI for the current user.
/// Stream Deck 7.6 keeps plugin global settings in the Windows credential store and, after a reboot,
/// has been seen failing to read them back ("Failed to parse account settings from credentials"),
/// in which case the plugin never receives url/token. This file is the fallback and the source of truth.
/// It lives inside the plugin folder (config\hakeys.cfg) so it survives whatever happens to other locations.
/// </summary>
static class ConfigStore
{
    static string Path = System.IO.Path.Combine(AppContext.BaseDirectory, "..", "config", "hakeys.cfg");
    public static void Init(string pluginDir) => Path = System.IO.Path.GetFullPath(System.IO.Path.Combine(pluginDir, "config", "hakeys.cfg"));
    static readonly byte[] Entropy = Encoding.UTF8.GetBytes("com.deniss.hakeys");

    public sealed record Config(string? Url, string? Token);

    public static Config? Load()
    {
        try
        {
            if (!File.Exists(Path)) { Log.Warn($"config file not found: {Path}"); return null; }
            var plain = ProtectedData.Unprotect(File.ReadAllBytes(Path), Entropy, DataProtectionScope.CurrentUser);
            var cfg = JsonSerializer.Deserialize<Config>(plain);
            Log.Info($"config file: url={(cfg?.Url ?? "-")}, token={(string.IsNullOrEmpty(cfg?.Token) ? "no" : "yes")}");
            return cfg;
        }
        catch (Exception ex) { Log.Warn($"config load: {ex.Message}"); return null; }
    }

    public static void Save(string? url, string? token)
    {
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
            var plain = JsonSerializer.SerializeToUtf8Bytes(new Config(url, token));
            File.WriteAllBytes(Path, ProtectedData.Protect(plain, Entropy, DataProtectionScope.CurrentUser));
            Log.Info("config file saved");
        }
        catch (Exception ex) { Log.Warn($"config save: {ex.Message}"); }
    }
}
