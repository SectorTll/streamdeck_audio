using System.Drawing;
using System.Text.Json.Nodes;

namespace DeckKeys;

/// <summary>Typed view over the settings JSON Stream Deck stores per key (edited by the property inspector).</summary>
public sealed class Settings
{
    readonly JsonObject _o;
    public Settings(JsonNode? n) => _o = n as JsonObject ?? new JsonObject();
    public JsonObject Raw => _o;

    public string? Str(string k) => _o[k] is JsonValue v && v.TryGetValue<string>(out var s) && s.Length > 0 ? s : null;
    public string Str(string k, string d) => Str(k) ?? d;
    public bool Bool(string k, bool d)
    {
        if (_o[k] is not JsonValue v) return d;
        if (v.TryGetValue<bool>(out var b)) return b;
        if (v.TryGetValue<string>(out var s)) return s is "true" or "1" or "on";
        return d;
    }
    public int Int(string k, int d)
    {
        if (_o[k] is not JsonValue v) return d;
        if (v.TryGetValue<int>(out var i)) return i;
        if (v.TryGetValue<double>(out var f)) return (int)Math.Round(f);
        if (v.TryGetValue<string>(out var s) && int.TryParse(s, out var p)) return p;
        return d;
    }
    public Color Color(string k, Color d) => Renderer.ParseColor(Str(k), d);
}
