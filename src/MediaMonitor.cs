using System.Runtime.InteropServices;
using Windows.Media.Control;

namespace AudioKeys;

/// <summary>
/// Play/pause state of the current media session (Spotify, browser, MSFS music, anything that
/// reports to Windows) via the WinRT media-transport API, with change notifications.
/// </summary>
sealed class MediaMonitor
{
    GlobalSystemMediaTransportControlsSessionManager? _mgr;
    GlobalSystemMediaTransportControlsSession? _session;
    readonly object _gate = new();

    public bool HasSession { get; private set; }
    public bool IsPlaying { get; private set; }

    public event Action? Changed;

    public async Task StartAsync()
    {
        try
        {
            _mgr = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            _mgr.CurrentSessionChanged += (_, _) => Bind();
            Bind();
        }
        catch (Exception ex) { Log.Warn($"media sessions unavailable: {ex.Message}"); }
    }

    void Bind()
    {
        lock (_gate)
        {
            try
            {
                if (_session is not null) _session.PlaybackInfoChanged -= OnPlaybackInfoChanged;
                _session = _mgr?.GetCurrentSession();
                if (_session is not null) _session.PlaybackInfoChanged += OnPlaybackInfoChanged;
                ReadState();
                Log.Info(_session is null ? "media: no session" : $"media: {_session.SourceAppUserModelId} {(IsPlaying ? "playing" : "paused")}");
            }
            catch (Exception ex) { Log.Warn($"media bind: {ex.Message}"); _session = null; HasSession = false; IsPlaying = false; }
        }
        Changed?.Invoke();
    }

    void OnPlaybackInfoChanged(GlobalSystemMediaTransportControlsSession s, PlaybackInfoChangedEventArgs e)
    {
        lock (_gate) ReadState();
        Log.Info($"media: {(IsPlaying ? "playing" : "paused")}");
        Changed?.Invoke();
    }

    void ReadState()
    {
        HasSession = _session is not null;
        IsPlaying = false;
        if (_session is null) return;
        try { IsPlaying = _session.GetPlaybackInfo().PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing; }
        catch { }
    }

    public async Task PlayPauseAsync()
    {
        GlobalSystemMediaTransportControlsSession? s;
        lock (_gate) s = _session;
        if (s is not null)
        {
            try { if (await s.TryTogglePlayPauseAsync()) return; }
            catch (Exception ex) { Log.Warn($"TryTogglePlayPause: {ex.Message}"); }
        }
        SendMediaKey();   // no session (or it refused): behave like the keyboard key
    }

    const ushort VK_MEDIA_PLAY_PAUSE = 0xB3;

    [DllImport("user32.dll")] static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);

    static void SendMediaKey()
    {
        keybd_event((byte)VK_MEDIA_PLAY_PAUSE, 0, 0x1 /*KEYEVENTF_EXTENDEDKEY*/, UIntPtr.Zero);
        keybd_event((byte)VK_MEDIA_PLAY_PAUSE, 0, 0x1 | 0x2 /*KEYUP*/, UIntPtr.Zero);
    }
}
