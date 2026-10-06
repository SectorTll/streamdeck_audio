namespace AudioKeys;

/// <summary>
/// Follows the master volume of whatever the default output device is. Re-binds when the default
/// changes, raises Changed on any volume/mute change (from us, the keyboard, the tray — anything).
/// All COM work happens on thread-pool threads, never on an audio callback thread.
/// </summary>
sealed class VolumeMonitor : IDisposable
{
    static readonly TimeSpan LockTimeout = TimeSpan.FromSeconds(2);

    readonly CoreAudio _audio;
    readonly object _gate = new();
    EndpointVolume? _ep;
    CancellationTokenSource? _fade;

    public float Volume { get; private set; }   // 0..1
    public bool Muted { get; private set; }
    public bool HasDevice => _ep is not null;

    public event Action? Changed;

    public VolumeMonitor(CoreAudio audio)
    {
        _audio = audio;
        _audio.Changed += Rebind;
        Rebind();
    }

    /// <summary>(Re)open the default device's volume interface. Only re-opens when the device id changed.</summary>
    public void Rebind()
    {
        if (!Monitor.TryEnter(_gate, LockTimeout)) { Log.Warn("rebind: volume lock busy"); return; }
        try
        {
            var ep = _audio.OpenDefaultVolume();
            if (ep is null) { Drop(); return; }
            if (_ep is not null && _ep.DeviceId == ep.DeviceId) { ep.Dispose(); ReadState(); return; }
            Drop();
            _ep = ep;
            _ep.Changed += OnEndpointChanged;
            ReadState();
            Log.Info($"volume follows {ep.DeviceId[^14..]}: {Volume:P0}{(Muted ? " muted" : "")}");
        }
        catch (Exception ex) { Log.Warn($"volume rebind: {ex.Message}"); Drop(); }
        finally { Monitor.Exit(_gate); }
        Changed?.Invoke();
    }

    void Drop()
    {
        if (_ep is null) return;
        _ep.Changed -= OnEndpointChanged;
        _ep.Dispose();
        _ep = null;
        Volume = 0; Muted = false;
    }

    void ReadState()
    {
        if (_ep is null) return;
        Volume = _ep.Volume;
        Muted = _ep.Muted;
    }

    void OnEndpointChanged(float volume, bool muted)
    {
        Volume = volume; Muted = muted;
        Log.Info($"volume {volume:P0}{(muted ? " muted" : "")}");
        Changed?.Invoke();
    }

    /// <summary>Runs an operation on the bound endpoint under the lock, with a timeout so a stuck COM call can never hang the key handler.</summary>
    void WithEndpoint(Action<EndpointVolume> op)
    {
        if (!Monitor.TryEnter(_gate, LockTimeout)) throw new InvalidOperationException("volume control is busy");
        try
        {
            if (_ep is null) throw new InvalidOperationException("no output device");
            op(_ep);
        }
        finally { Monitor.Exit(_gate); }
    }

    public void Adjust(int percentStep)
    {
        CancelFade();
        WithEndpoint(ep =>
        {
            var v = Math.Clamp(MathF.Round(ep.Volume * 100f) + percentStep, 0, 100) / 100f;
            ep.SetVolume(v);
            if (ep.Muted && percentStep > 0) ep.SetMute(false);   // turning the volume up un-mutes, like the keyboard key does
            Log.Info($"adjust {percentStep:+#;-#;0} -> {v:P0}");
        });
    }

    public void ToggleMute()
    {
        CancelFade();
        WithEndpoint(ep => ep.SetMute(!ep.Muted));
    }

    /// <summary>Set the volume, optionally fading there over fadeMs (steps of 25 ms).</summary>
    public void Set(int percent, int fadeMs)
    {
        CancelFade();
        var target = Math.Clamp(percent, 0, 100) / 100f;
        EndpointVolume? ep = null;
        WithEndpoint(e => ep = e);
        if (ep is null) return;

        var start = ep.Volume;
        if (fadeMs < 50 || Math.Abs(start - target) < 0.005f)
        {
            ep.SetVolume(target);
            return;
        }

        var cts = new CancellationTokenSource();
        lock (_gate) _fade = cts;
        _ = Task.Run(async () =>
        {
            var steps = Math.Max(2, fadeMs / 25);
            try
            {
                for (int i = 1; i <= steps && !cts.IsCancellationRequested; i++)
                {
                    ep.SetVolume(start + (target - start) * i / steps);
                    await Task.Delay(fadeMs / steps, cts.Token);
                }
                if (!cts.IsCancellationRequested) ep.SetVolume(target);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { Log.Warn($"fade: {ex.Message}"); }
        });
    }

    void CancelFade()
    {
        lock (_gate) { _fade?.Cancel(); _fade = null; }
    }

    public void Dispose()
    {
        _audio.Changed -= Rebind;
        CancelFade();
        lock (_gate) Drop();
    }
}
