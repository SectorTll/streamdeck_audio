using System.Drawing;
using System.Text.Json.Nodes;
using DeckKeys;

namespace AudioKeys;

static class Actions
{
    public const string OutputDevice = "com.deniss.audiokeys.output-device";
    public const string InputDevice = "com.deniss.audiokeys.input-device";
    public const string VolumeUp = "com.deniss.audiokeys.volume-up";
    public const string VolumeDown = "com.deniss.audiokeys.volume-down";
    public const string VolumeSet = "com.deniss.audiokeys.volume-set";
    public const string Mute = "com.deniss.audiokeys.mute";
    public const string PlayPause = "com.deniss.audiokeys.play-pause";
}

sealed class KeyInstance
{
    public required string Context;
    public required string Action;
    public Settings Settings = new(null);
    public string? LastImage;
}

/// <summary>All plugin behaviour: tracks visible keys, re-renders on audio/media events, handles presses and the property inspector.</summary>
sealed class Plugin
{
    static readonly Color Green = System.Drawing.Color.FromArgb(60, 230, 80);
    static readonly Color Yellow = System.Drawing.Color.FromArgb(255, 210, 0);
    static readonly Color Red = System.Drawing.Color.FromArgb(255, 59, 48);
    static readonly Color Grey = System.Drawing.Color.FromArgb(120, 120, 120);

    readonly StreamDeck _sd;
    readonly CoreAudio _audio;
    readonly VolumeMonitor _volume;
    readonly MediaMonitor _media;
    readonly Dictionary<string, KeyInstance> _keys = new();
    readonly object _gate = new();
    AudioSnapshot _snapshot;      // render endpoints
    AudioSnapshot _inputs;        // capture endpoints
    CancellationTokenSource? _debounce;

    public Plugin(StreamDeck sd, CoreAudio audio, VolumeMonitor volume, MediaMonitor media)
    {
        _sd = sd; _audio = audio; _volume = volume; _media = media;
        _snapshot = _audio.Snapshot(EDataFlow.Render);
        _inputs = _audio.Snapshot(EDataFlow.Capture);
        Log.Info($"render endpoints: {_snapshot.Devices.Count} total, {_snapshot.Devices.Count(d => d.IsActive)} active; default = {_snapshot.ById(_snapshot.DefaultMultimedia)?.Name ?? "?"}");
        Log.Info($"capture endpoints: {_inputs.Devices.Count} total, {_inputs.Devices.Count(d => d.IsActive)} active; default = {_inputs.ById(_inputs.DefaultMultimedia)?.Name ?? "?"}");
        _audio.Changed += ScheduleRefresh;
        _volume.Changed += ScheduleRefresh;
        _media.Changed += ScheduleRefresh;
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
                if (context is null || action is null) return;
                {
                    var key = new KeyInstance { Context = context, Action = action, Settings = new Settings(msg["payload"]?["settings"]) };
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
                    var key = Get(context);
                    if (key is null) return;
                    key.Settings = new Settings(msg["payload"]?["settings"]);
                    if (key.Action is Actions.OutputDevice or Actions.InputDevice) await ResolveName(key);
                    await RenderKey(key, force: false);
                }
                break;

            case "keyDown":
                if (context is null) return;
                await OnKeyDown(context, action, new Settings(msg["payload"]?["settings"]));
                break;

            case "sendToPlugin":
                if (context is null) return;
                await OnSendToPlugin(context, msg["payload"] as JsonObject);
                break;

            case "propertyInspectorDidAppear":
                _snapshot = _audio.Snapshot(EDataFlow.Render);   // fresh lists for the dropdowns
                _inputs = _audio.Snapshot(EDataFlow.Capture);
                break;

            case "systemDidWakeUp":
                _volume.Rebind();
                ScheduleRefresh();
                break;
        }
    }

    KeyInstance? Get(string context)
    {
        lock (_gate) return _keys.TryGetValue(context, out var k) ? k : null;
    }

    async Task OnKeyDown(string context, string? action, Settings s)
    {
        Log.Info($"keyDown {action?[(action.LastIndexOf('.') + 1)..]} ({context[..8]})");
        try
        {
            switch (action)
            {
                case Actions.OutputDevice:
                    await PressDevice(context, s, EDataFlow.Render);
                    return;
                case Actions.InputDevice:
                    await PressDevice(context, s, EDataFlow.Capture);
                    return;
                case Actions.VolumeUp:
                    _volume.Adjust(+Math.Abs(s.Int("step", 5)));
                    break;
                case Actions.VolumeDown:
                    _volume.Adjust(-Math.Abs(s.Int("step", 5)));
                    break;
                case Actions.VolumeSet:
                    _volume.Set(s.Int("target", 0), s.Int("fadeMs", 1000));
                    break;
                case Actions.Mute:
                    _volume.ToggleMute();
                    break;
                case Actions.PlayPause:
                    await _media.PlayPauseAsync();
                    break;
            }
        }
        catch (Exception ex)
        {
            Log.Error($"{action}: {ex.Message}");
            await _sd.ShowAlert(context);
        }
        ScheduleRefresh();
    }

    static EDataFlow FlowOf(string action) => action == Actions.InputDevice ? EDataFlow.Capture : EDataFlow.Render;
    AudioSnapshot SnapshotFor(EDataFlow flow) => flow == EDataFlow.Capture ? _inputs : _snapshot;
    AudioSnapshot Refresh(EDataFlow flow) => flow == EDataFlow.Capture ? (_inputs = _audio.Snapshot(flow)) : (_snapshot = _audio.Snapshot(flow));

    async Task PressDevice(string context, Settings s, EDataFlow flow)
    {
        var snap = Refresh(flow);
        var dev = snap.ById(s.Str("deviceId")) ?? snap.ByName(s.Str("deviceName"));
        if (dev is null || !dev.IsActive)
        {
            Log.Info($"press: device not available ({s.Str("deviceName") ?? s.Str("deviceId")})");
            await _sd.ShowAlert(context);
            return;
        }
        _audio.SetDefault(dev.Id, s.Bool("setCommunications", true));
        Log.Info($"press: default {(flow == EDataFlow.Capture ? "input" : "output")} -> {dev.Name}" + (s.Bool("setCommunications", true) ? " (+communications)" : ""));
        ScheduleRefresh();
    }

    async Task OnSendToPlugin(string context, JsonObject? payload)
    {
        var ev = payload?["event"]?.GetValue<string>();
        if (ev is "getDevices" or "getInputDevices")
        {
            var snap = Refresh(ev == "getInputDevices" ? EDataFlow.Capture : EDataFlow.Render);
            var key = Get(context);
            var selected = key?.Settings.Str("deviceId");

            var items = new JsonArray();
            foreach (var d in snap.Devices.Where(d => d.IsActive).OrderBy(d => d.Name))
                items.Add(new JsonObject { ["value"] = d.Id, ["label"] = d.Name });
            // a device that is configured but currently absent still has to be selectable/visible
            if (selected is not null && snap.ById(selected) is { IsActive: false } absent)
                items.Add(new JsonObject { ["value"] = absent.Id, ["label"] = absent.Name + " (not connected)" });
            else if (selected is not null && snap.ById(selected) is null && key?.Settings.Str("deviceName") is { } nm)
                items.Add(new JsonObject { ["value"] = selected, ["label"] = nm + " (not found)" });

            await _sd.SendToPropertyInspector(context, new JsonObject { ["event"] = ev, ["items"] = items });
        }
    }

    /// <summary>When the user picks a device in the inspector only deviceId arrives; store its name so we can re-bind after Windows re-enumerates.</summary>
    async Task ResolveName(KeyInstance key)
    {
        var dev = SnapshotFor(FlowOf(key.Action)).ById(key.Settings.Str("deviceId"));
        if (dev is null || dev.Name == key.Settings.Str("deviceName")) return;
        var updated = (JsonObject)key.Settings.Raw.DeepClone();
        updated["deviceName"] = dev.Name;
        key.Settings = new Settings(updated);
        await _sd.SetSettings(key.Context, updated);
    }

    // ---- events → coalesced refresh --------------------------------------------------------

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
        _snapshot = _audio.Snapshot(EDataFlow.Render);
        _inputs = _audio.Snapshot(EDataFlow.Capture);
        List<KeyInstance> keys;
        lock (_gate) keys = _keys.Values.ToList();
        foreach (var k in keys) await RenderKey(k, force: false);
    }

    // ---- rendering ------------------------------------------------------------------------

    async Task RenderKey(KeyInstance key, bool force)
    {
        var image = key.Action switch
        {
            Actions.OutputDevice => await RenderDevice(key, force, EDataFlow.Render, "headphones"),
            Actions.InputDevice => await RenderDevice(key, force, EDataFlow.Capture, "mic"),
            Actions.VolumeUp => RenderVolume(key, "vol-up"),
            Actions.VolumeDown => RenderVolume(key, "vol-down"),
            Actions.VolumeSet => RenderVolume(key, key.Settings.Int("target", 0) == 0 ? "vol-mute" : "vol-set"),
            Actions.Mute => RenderMute(key),
            Actions.PlayPause => RenderPlayPause(key),
            _ => null,
        };
        if (image is null) return;
        if (force && key.Action is not (Actions.OutputDevice or Actions.InputDevice)) Log.Info($"key {key.Context[..8]}: {key.Action[(key.Action.LastIndexOf('.') + 1)..]} appeared");
        if (!force && ReferenceEquals(image, key.LastImage)) return;   // cached string identity == same picture
        key.LastImage = image;
        await _sd.SetImage(key.Context, image);
    }

    async Task<string> RenderDevice(KeyInstance key, bool log, EDataFlow flow, string defaultGlyph)
    {
        var s = key.Settings;
        var snap = SnapshotFor(flow);
        var deviceId = s.Str("deviceId");
        var deviceName = s.Str("deviceName");
        var dev = snap.ById(deviceId);

        // Windows re-enumerated the endpoint (new ID, same name): re-bind silently
        if ((dev is null || !dev.IsActive) && snap.ByName(deviceName) is { } byName && byName.Id != deviceId)
        {
            Log.Info($"re-bound '{deviceName}' to new id {byName.Id}");
            var updated = (JsonObject)s.Raw.DeepClone();
            updated["deviceId"] = byName.Id;
            key.Settings = s = new Settings(updated);
            dev = byName;
            await _sd.SetSettings(key.Context, updated);
        }

        var state = dev is null || !dev.IsActive ? KeyState.Absent
                  : dev.Id == snap.DefaultMultimedia ? KeyState.Active
                  : KeyState.Inactive;
        if (log) Log.Info($"key {key.Context[..8]}: '{deviceName ?? deviceId ?? "(none)"}' glyph={s.Str("glyph", defaultGlyph)} state={state}");

        var glyph = s.Str("glyph", defaultGlyph);
        var label = s.Bool("showLabel", false) ? ShortName(dev?.Name ?? deviceName) : null;

        if (deviceId is null)
            return Renderer.Render(glyph, Grey, false, s.Bool("showLabel", false) ? "no device" : null);
        if (state == KeyState.Absent && s.Str("absentMode", "led") == "hidden")
            return Renderer.Hidden;

        var (color, on) = state switch
        {
            KeyState.Active => (s.Color("colorActive", Green), true),
            KeyState.Absent => (s.Color("colorAbsent", Yellow), true),
            _ => (s.Color("colorInactive", Green), false),
        };
        return Renderer.Render(glyph, color, on, label);
    }

    /// <summary>Volume keys: the LED is a level meter; red and fully lit while muted.</summary>
    string RenderVolume(KeyInstance key, string glyph)
    {
        var s = key.Settings;
        var showPercent = s.Bool("showPercent", true);
        if (!_volume.HasDevice)
            return Renderer.Render(glyph, Grey, false, showPercent ? "—" : null);

        var percent = (int)MathF.Round(_volume.Volume * 100f);
        var label = showPercent ? (_volume.Muted ? "muted" : $"{percent}%") : null;
        if (_volume.Muted)
            return Renderer.Render(glyph, s.Color("colorMuted", Red), true, label);
        return Renderer.Render(glyph, s.Color("colorLed", Green), percent > 0, label, percent / 100f);
    }

    /// <summary>Mute key: red bar while muted, green bar while sound is on. No level meter, no percent unless asked.</summary>
    string RenderMute(KeyInstance key)
    {
        var s = key.Settings;
        var showPercent = s.Bool("showPercent", false);
        if (!_volume.HasDevice)
            return Renderer.Render("vol-mute", Grey, false, showPercent ? "—" : null);
        if (_volume.Muted)
            return Renderer.Render("vol-mute", s.Color("colorMuted", Red), true, showPercent ? "muted" : null);
        var percent = (int)MathF.Round(_volume.Volume * 100f);
        return Renderer.Render("speaker", s.Color("colorLed", Green), true, showPercent ? $"{percent}%" : null);
    }

    string RenderPlayPause(KeyInstance key)
    {
        var s = key.Settings;
        if (!_media.HasSession) return Renderer.Render("play-pause", Grey, false);
        return _media.IsPlaying
            ? Renderer.Render("pause", s.Color("colorPlaying", Green), true)
            : Renderer.Render("play", s.Color("colorPlaying", Green), false);
    }

    static string? ShortName(string? name)
    {
        if (name is null) return null;
        // "Headphones (ATH-M50xBT2)" -> "ATH-M50xBT2"
        var i = name.IndexOf('('); var j = name.LastIndexOf(')');
        return i >= 0 && j > i ? name[(i + 1)..j].Trim() : name;
    }
}
