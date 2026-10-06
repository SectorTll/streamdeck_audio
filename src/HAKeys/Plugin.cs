using System.Drawing;
using System.Text.Json.Nodes;
using DeckKeys;

namespace HAKeys;

static class Actions
{
    public const string Entity = "com.deniss.hakeys.entity";
}

sealed class KeyInstance
{
    public required string Context;
    public Settings Settings = new(null);
    public string? LastImage;
}

/// <summary>Entity keys: LED shows on / off / unavailable, press calls a service. One HA connection shared by all keys.</summary>
sealed class Plugin
{
    static readonly Color Green = Color.FromArgb(60, 230, 80);
    static readonly Color Yellow = Color.FromArgb(255, 210, 0);
    static readonly Color Grey = Color.FromArgb(120, 120, 120);
    const string DefaultUrl = "http://10.1.1.118:8123";
    static readonly string[] ToggleDomains = { "switch", "light", "fan", "input_boolean", "automation", "humidifier", "siren", "media_player", "cover", "lock", "climate" };

    readonly StreamDeck _sd;
    readonly HaClient _ha;
    readonly Dictionary<string, KeyInstance> _keys = new();
    readonly object _gate = new();
    CancellationTokenSource? _debounce;

    public Plugin(StreamDeck sd, HaClient ha)
    {
        _sd = sd; _ha = ha;
        _ha.Changed += _ => ScheduleRefresh();
        _sd.Event += OnEvent;
    }

    public Task Start() => _sd.GetGlobalSettings();

    async Task OnEvent(string ev, JsonNode msg)
    {
        var context = msg["context"]?.GetValue<string>();
        switch (ev)
        {
            case "didReceiveGlobalSettings":
            {
                var g = new Settings(msg["payload"]?["settings"]);
                if (g.Str("haUrl") is null)
                {
                    // first run: pre-fill the server address so only the token has to be typed
                    var init = (JsonObject)g.Raw.DeepClone();
                    init["haUrl"] = DefaultUrl;
                    await _sd.SetGlobalSettings(init);
                }
                _ha.Configure(g.Str("haUrl", DefaultUrl), g.Str("haToken"));
                break;
            }
            case "willAppear":
                if (context is null) return;
                {
                    var key = new KeyInstance { Context = context, Settings = new Settings(msg["payload"]?["settings"]) };
                    lock (_gate) _keys[context] = key;
                    Log.Info($"key {context[..8]}: '{key.Settings.Str("entityId") ?? "(none)"}' appeared");
                    await RenderKey(key, force: true);
                }
                break;
            case "willDisappear":
                if (context is null) return;
                lock (_gate) _keys.Remove(context);
                break;
            case "didReceiveSettings":
                if (context is null) return;
                {
                    var key = Get(context);
                    if (key is null) return;
                    key.Settings = new Settings(msg["payload"]?["settings"]);
                    await RenderKey(key, force: false);
                }
                break;
            case "keyDown":
                if (context is null) return;
                await OnKeyDown(context, new Settings(msg["payload"]?["settings"]));
                break;
            case "sendToPlugin":
                if (context is null) return;
                await OnSendToPlugin(context, msg["payload"] as JsonObject);
                break;
            case "systemDidWakeUp":
                _ha.Kick();
                break;
        }
    }

    KeyInstance? Get(string context)
    {
        lock (_gate) return _keys.TryGetValue(context, out var k) ? k : null;
    }

    async Task OnKeyDown(string context, Settings s)
    {
        var entityId = s.Str("entityId");
        Log.Info($"keyDown {entityId ?? "(none)"} ({context[..8]})");
        if (entityId is null) { await _sd.ShowAlert(context); return; }
        var domain = entityId[..entityId.IndexOf('.')];
        var service = s.Str("service", "toggle");
        try
        {
            await _ha.CallService(domain, service, entityId);
            Log.Info($"{domain}.{service} -> {entityId}");
        }
        catch (Exception ex)
        {
            Log.Error($"{domain}.{service} {entityId}: {ex.Message}");
            await _sd.ShowAlert(context);
        }
    }

    async Task OnSendToPlugin(string context, JsonObject? payload)
    {
        var ev = payload?["event"]?.GetValue<string>();
        if (ev != "getEntities") return;
        var key = Get(context);
        var selected = key?.Settings.Str("entityId");
        var items = new JsonArray();
        if (!_ha.Connected)
            items.Add(new JsonObject { ["value"] = selected ?? "", ["label"] = $"(not connected: {_ha.LastError ?? "…"})" });
        var list = _ha.States.Where(e => ToggleDomains.Contains(e.EntityId[..e.EntityId.IndexOf('.')])).OrderBy(e => e.Name).ToList();
        foreach (var e in list)
            items.Add(new JsonObject { ["value"] = e.EntityId, ["label"] = $"{e.Name}  ({e.EntityId})" });
        if (selected is not null && !list.Any(e => e.EntityId == selected))
            items.Add(new JsonObject { ["value"] = selected, ["label"] = selected + " (not found)" });
        await _sd.SendToPropertyInspector(context, new JsonObject { ["event"] = "getEntities", ["items"] = items });
    }

    /// <summary>HA can emit bursts (several entities at once); wait 60 ms and render once.</summary>
    void ScheduleRefresh()
    {
        CancellationTokenSource cts;
        lock (_gate)
        {
            _debounce?.Cancel();
            _debounce = cts = new CancellationTokenSource();
        }
        _ = Task.Run(async () =>
        {
            try { await Task.Delay(60, cts.Token); }
            catch (OperationCanceledException) { return; }
            List<KeyInstance> keys;
            lock (_gate) keys = _keys.Values.ToList();
            foreach (var k in keys)
            {
                try { await RenderKey(k, force: false); }
                catch (Exception ex) { Log.Error($"render: {ex.Message}"); }
            }
        });
    }

    async Task RenderKey(KeyInstance key, bool force)
    {
        var s = key.Settings;
        var glyph = s.Str("glyph", "plug");
        var entityId = s.Str("entityId");
        string image;
        if (entityId is null)
            image = Renderer.Render(glyph, Grey, false, "no entity");
        else if (!_ha.Connected)
            image = Renderer.Render(glyph, s.Color("colorUnavailable", Yellow), true, "HA offline");
        else
        {
            var e = _ha.Get(entityId);
            if (e is null || e.IsUnavailable)
                image = Renderer.Render(glyph, s.Color("colorUnavailable", Yellow), true, s.Bool("showState", false) ? (e?.State ?? "missing") : null);
            else if (e.IsOn)
                image = Renderer.Render(glyph, s.Color("colorOn", Green), true, s.Bool("showState", false) ? e.State : null);
            else
                image = Renderer.Render(glyph, s.Color("colorOff", Green), false, s.Bool("showState", false) ? e.State : null);
        }
        if (!force && ReferenceEquals(image, key.LastImage)) return;
        key.LastImage = image;
        await _sd.SetImage(key.Context, image);
    }
}
