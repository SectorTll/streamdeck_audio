using System.Drawing;
using System.Text.Json.Nodes;

namespace AudioKeys;

/// <summary>Per-key settings as stored by Stream Deck (camelCase keys, edited by the property inspector).</summary>
sealed class KeySettings
{
    public string? DeviceId;
    public string? DeviceName;
    public string Glyph = "headphones";
    public string ColorActive = "#3ce650";
    public string ColorInactive = "#3ce650";
    public string ColorAbsent = "#ffd200";
    public string AbsentMode = "led";          // "led" | "hidden"
    public bool SetCommunications = true;
    public bool ShowLabel = false;

    public static KeySettings From(JsonNode? n)
    {
        var s = new KeySettings();
        if (n is not JsonObject o) return s;
        s.DeviceId = Str(o, "deviceId");
        s.DeviceName = Str(o, "deviceName");
        s.Glyph = Str(o, "glyph") ?? s.Glyph;
        s.ColorActive = Str(o, "colorActive") ?? s.ColorActive;
        s.ColorInactive = Str(o, "colorInactive") ?? s.ColorInactive;
        s.ColorAbsent = Str(o, "colorAbsent") ?? s.ColorAbsent;
        s.AbsentMode = Str(o, "absentMode") ?? s.AbsentMode;
        s.SetCommunications = Bool(o, "setCommunications") ?? s.SetCommunications;
        s.ShowLabel = Bool(o, "showLabel") ?? s.ShowLabel;
        return s;
    }

    static string? Str(JsonObject o, string k) => o[k] is JsonValue v && v.TryGetValue<string>(out var s) && s.Length > 0 ? s : null;
    static bool? Bool(JsonObject o, string k)
    {
        if (o[k] is not JsonValue v) return null;
        if (v.TryGetValue<bool>(out var b)) return b;
        if (v.TryGetValue<string>(out var s)) return s is "true" or "1" or "on";
        return null;
    }
}

sealed class KeyInstance
{
    public required string Context;
    public KeySettings Settings = new();
    public string? LastImage;
}

/// <summary>All plugin behaviour: tracks visible keys, re-renders on audio events, handles presses and the property inspector.</summary>
sealed class Plugin
{
    const string ActionOutputDevice = "com.deniss.audiokeys.output-device";

    readonly StreamDeck _sd;
    readonly CoreAudio _audio;
    readonly Dictionary<string, KeyInstance> _keys = new();
    readonly object _gate = new();
    AudioSnapshot _snapshot;
    CancellationTokenSource? _debounce;

    public Plugin(StreamDeck sd, CoreAudio audio)
    {
        _sd = sd; _audio = audio;
        _snapshot = _audio.Snapshot();
        Log.Info($"render endpoints: {_snapshot.Devices.Count} total, {_snapshot.Devices.Count(d => d.IsActive)} active; default = {_snapshot.ById(_snapshot.DefaultMultimedia)?.Name ?? "?"}");
        _audio.Changed += OnAudioChanged;
        _sd.Event += OnEvent;
    }

    // ---- Stream Deck events ---------------------------------------------------------------

    async Task OnEvent(string ev, JsonNode msg)
    {
        var context = msg["context"]?.GetValue<string>();
        var action = msg["action"]?.GetValue<string>();
        switch (ev)
        {
            case "willAppear":
                if (context is null || action != ActionOutputDevice) return;
                {
                    var key = new KeyInstance { Context = context, Settings = KeySettings.From(msg["payload"]?["settings"]) };
                    lock (_gate) _keys[context] = key;
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
                    KeyInstance? key;
                    lock (_gate) _keys.TryGetValue(context, out key);
                    if (key is null) return;
                    key.Settings = KeySettings.From(msg["payload"]?["settings"]);
                    await ResolveName(key, msg["payload"]?["settings"] as JsonObject);
                    await RenderKey(key, force: false);
                }
                break;

            case "keyDown":
                if (context is null) return;
                await OnKeyDown(context, KeySettings.From(msg["payload"]?["settings"]));
                break;

            case "sendToPlugin":
                if (context is null) return;
                await OnSendToPlugin(context, msg["payload"] as JsonObject);
                break;

            case "propertyInspectorDidAppear":
                _snapshot = _audio.Snapshot();   // fresh list for the dropdown
                break;

            case "systemDidWakeUp":
                OnAudioChanged();
                break;
        }
    }

    async Task OnKeyDown(string context, KeySettings s)
    {
        var snap = _snapshot = _audio.Snapshot();
        var dev = snap.ById(s.DeviceId) ?? snap.ByName(s.DeviceName);
        if (dev is null || !dev.IsActive)
        {
            Log.Info($"press: device not available ({s.DeviceName ?? s.DeviceId})");
            await _sd.ShowAlert(context);
            return;
        }
        try
        {
            _audio.SetDefault(dev.Id, s.SetCommunications);
            Log.Info($"press: default -> {dev.Name}" + (s.SetCommunications ? " (+communications)" : ""));
        }
        catch (Exception ex)
        {
            Log.Error($"set default failed: {ex.Message}");
            await _sd.ShowAlert(context);
        }
        // the notification callback will re-render; a direct refresh keeps the key snappy if it does not fire
        ScheduleRefresh();
    }

    async Task OnSendToPlugin(string context, JsonObject? payload)
    {
        var ev = payload?["event"]?.GetValue<string>();
        if (ev == "getDevices")
        {
            var snap = _snapshot = _audio.Snapshot();
            KeyInstance? key;
            lock (_gate) _keys.TryGetValue(context, out key);
            var selected = key?.Settings.DeviceId;

            var items = new JsonArray();
            foreach (var d in snap.Devices.Where(d => d.IsActive).OrderBy(d => d.Name))
                items.Add(new JsonObject { ["value"] = d.Id, ["label"] = d.Name });
            // a device that is configured but currently absent still has to be selectable/visible
            if (selected is not null && snap.ById(selected) is { IsActive: false } absent)
                items.Add(new JsonObject { ["value"] = absent.Id, ["label"] = absent.Name + " (not connected)" });
            else if (selected is not null && snap.ById(selected) is null && key?.Settings.DeviceName is { } nm)
                items.Add(new JsonObject { ["value"] = selected, ["label"] = nm + " (not found)" });

            await _sd.SendToPropertyInspector(context, new JsonObject { ["event"] = "getDevices", ["items"] = items });
        }
        else if (ev == "getGlyphs")
        {
            var items = new JsonArray();
            foreach (var g in Glyphs.Ids) items.Add(new JsonObject { ["value"] = g, ["label"] = g });
            await _sd.SendToPropertyInspector(context, new JsonObject { ["event"] = "getGlyphs", ["items"] = items });
        }
    }

    /// <summary>When the user picks a device in the inspector only deviceId arrives; store its name so we can re-bind after Windows re-enumerates.</summary>
    async Task ResolveName(KeyInstance key, JsonObject? raw)
    {
        var dev = _snapshot.ById(key.Settings.DeviceId);
        if (dev is null || dev.Name == key.Settings.DeviceName) return;
        key.Settings.DeviceName = dev.Name;
        var updated = raw is null ? new JsonObject() : (JsonObject)raw.DeepClone();
        updated["deviceName"] = dev.Name;
        await _sd.SetSettings(key.Context, updated);
    }

    // ---- audio events → coalesced refresh -----------------------------------------------

    void OnAudioChanged() => ScheduleRefresh();

    /// <summary>Bluetooth and USB devices fire several events in a row; wait 60 ms and render once.</summary>
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
            try { await RefreshAll(); }
            catch (Exception ex) { Log.Error($"refresh: {ex}"); }
        });
    }

    async Task RefreshAll()
    {
        _snapshot = _audio.Snapshot();
        List<KeyInstance> keys;
        lock (_gate) keys = _keys.Values.ToList();
        foreach (var k in keys) await RenderKey(k, force: false);
    }

    // ---- rendering ------------------------------------------------------------------------

    async Task RenderKey(KeyInstance key, bool force)
    {
        var s = key.Settings;
        var snap = _snapshot;
        var dev = snap.ById(s.DeviceId);

        // Windows re-enumerated the endpoint (new ID, same name): re-bind silently
        if ((dev is null || !dev.IsActive) && snap.ByName(s.DeviceName) is { } byName && byName.Id != s.DeviceId)
        {
            Log.Info($"re-bound '{s.DeviceName}' to new id {byName.Id}");
            s.DeviceId = byName.Id;
            dev = byName;
            await _sd.SetSettings(key.Context, new JsonObject
            {
                ["deviceId"] = s.DeviceId, ["deviceName"] = s.DeviceName, ["glyph"] = s.Glyph,
                ["colorActive"] = s.ColorActive, ["colorInactive"] = s.ColorInactive, ["colorAbsent"] = s.ColorAbsent,
                ["absentMode"] = s.AbsentMode, ["setCommunications"] = s.SetCommunications, ["showLabel"] = s.ShowLabel
            });
        }

        var state = dev is null || !dev.IsActive ? KeyState.Absent
                  : dev.Id == snap.DefaultMultimedia ? KeyState.Active
                  : KeyState.Inactive;
        if (force) Log.Info($"key {key.Context[..8]}: '{s.DeviceName ?? s.DeviceId ?? "(none)"}' glyph={s.Glyph} state={state}");

        string image;
        if (s.DeviceId is null)
            image = Renderer.Render(s.Glyph, Color.FromArgb(120, 120, 120), false, s.ShowLabel ? "no device" : null);
        else if (state == KeyState.Absent && s.AbsentMode == "hidden")
            image = Renderer.Hidden;
        else
        {
            var (color, on) = state switch
            {
                KeyState.Active => (Renderer.ParseColor(s.ColorActive, Color.LimeGreen), true),
                KeyState.Absent => (Renderer.ParseColor(s.ColorAbsent, Color.Gold), true),
                _ => (Renderer.ParseColor(s.ColorInactive, Color.LimeGreen), false),
            };
            image = Renderer.Render(s.Glyph, color, on, s.ShowLabel ? ShortName(dev?.Name ?? s.DeviceName) : null);
        }

        if (!force && ReferenceEquals(image, key.LastImage)) return;   // cached string identity == same picture
        key.LastImage = image;
        await _sd.SetImage(key.Context, image);
    }

    static string? ShortName(string? name)
    {
        if (name is null) return null;
        // "Headphones (ATH-M50xBT2)" -> "ATH-M50xBT2"
        var i = name.IndexOf('('); var j = name.LastIndexOf(')');
        return i >= 0 && j > i ? name[(i + 1)..j].Trim() : name;
    }
}
