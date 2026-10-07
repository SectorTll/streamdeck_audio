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
/// </summary>
static class ConfigStore
{
    static readonly string Path = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DeckKeys", "hakeys.cfg");
    static readonly byte[] Entropy = Encoding.UTF8.GetBytes("com.deniss.hakeys");

    public sealed record Config(string? Url, string? Token);

    public static Config? Load()
    {
        try
        {
            if (!File.Exists(Path)) return null;
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
