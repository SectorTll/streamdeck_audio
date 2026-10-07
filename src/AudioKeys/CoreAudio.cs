using System.Runtime.InteropServices;

using DeckKeys;

namespace AudioKeys;

// Minimal Windows Core Audio interop: enumerate render endpoints, watch for changes,
// set the default endpoint (IPolicyConfig, the same undocumented interface every audio
// switcher on Windows uses) and read/write the master volume of an endpoint.

enum EDataFlow { Render = 0, Capture = 1, All = 2 }
enum ERole { Console = 0, Multimedia = 1, Communications = 2 }

static class DeviceState
{
    public const uint Active = 0x1;
    public const uint Disabled = 0x2;
    public const uint NotPresent = 0x4;
    public const uint Unplugged = 0x8;
    public const uint All = 0xF;
}

[StructLayout(LayoutKind.Sequential)]
struct PropertyKey
{
    public Guid fmtid;
    public uint pid;
    public PropertyKey(Guid f, uint p) { fmtid = f; pid = p; }
}

[StructLayout(LayoutKind.Sequential)]
struct PropVariant
{
    public ushort vt;
    ushort r1, r2, r3;
    public IntPtr p;
    IntPtr p2;

    public const ushort VT_LPWSTR = 31;

    public string? GetString() => vt == VT_LPWSTR && p != IntPtr.Zero ? Marshal.PtrToStringUni(p) : null;

    [DllImport("ole32.dll")]
    static extern int PropVariantClear(ref PropVariant pvar);
    public void Clear() => PropVariantClear(ref this);
}

[ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
class MMDeviceEnumeratorComObject { }

[ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IMMDeviceEnumerator
{
    [PreserveSig] int EnumAudioEndpoints(EDataFlow dataFlow, uint stateMask, out IMMDeviceCollection devices);
    [PreserveSig] int GetDefaultAudioEndpoint(EDataFlow dataFlow, ERole role, out IMMDevice endpoint);
    [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);
    [PreserveSig] int RegisterEndpointNotificationCallback(IMMNotificationClient client);
    [PreserveSig] int UnregisterEndpointNotificationCallback(IMMNotificationClient client);
}

[ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IMMDeviceCollection
{
    [PreserveSig] int GetCount(out uint count);
    [PreserveSig] int Item(uint index, out IMMDevice device);
}

[ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IMMDevice
{
    [PreserveSig] int Activate(ref Guid iid, uint clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object iface);
    [PreserveSig] int OpenPropertyStore(uint stgmAccess, out IPropertyStore properties);
    [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
    [PreserveSig] int GetState(out uint state);
}

[ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IPropertyStore
{
    [PreserveSig] int GetCount(out uint count);
    [PreserveSig] int GetAt(uint index, out PropertyKey key);
    [PreserveSig] int GetValue(ref PropertyKey key, out PropVariant value);
    [PreserveSig] int SetValue(ref PropertyKey key, ref PropVariant value);
    [PreserveSig] int Commit();
}

[ComImport, Guid("7991EEC9-7E89-4D85-8390-6C703CEC60C0"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IMMNotificationClient
{
    void OnDeviceStateChanged([MarshalAs(UnmanagedType.LPWStr)] string deviceId, uint newState);
    void OnDeviceAdded([MarshalAs(UnmanagedType.LPWStr)] string deviceId);
    void OnDeviceRemoved([MarshalAs(UnmanagedType.LPWStr)] string deviceId);
    void OnDefaultDeviceChanged(EDataFlow flow, ERole role, [MarshalAs(UnmanagedType.LPWStr)] string? defaultDeviceId);
    void OnPropertyValueChanged([MarshalAs(UnmanagedType.LPWStr)] string deviceId, PropertyKey key);
}

[ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IAudioEndpointVolume
{
    [PreserveSig] int RegisterControlChangeNotify(IAudioEndpointVolumeCallback notify);
    [PreserveSig] int UnregisterControlChangeNotify(IAudioEndpointVolumeCallback notify);
    [PreserveSig] int GetChannelCount(out uint count);
    [PreserveSig] int SetMasterVolumeLevel(float levelDb, IntPtr eventContext);
    [PreserveSig] int SetMasterVolumeLevelScalar(float level, IntPtr eventContext);
    [PreserveSig] int GetMasterVolumeLevel(out float levelDb);
    [PreserveSig] int GetMasterVolumeLevelScalar(out float level);
    [PreserveSig] int SetChannelVolumeLevel(uint channel, float levelDb, IntPtr eventContext);
    [PreserveSig] int SetChannelVolumeLevelScalar(uint channel, float level, IntPtr eventContext);
    [PreserveSig] int GetChannelVolumeLevel(uint channel, out float levelDb);
    [PreserveSig] int GetChannelVolumeLevelScalar(uint channel, out float level);
    [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, IntPtr eventContext);
    [PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
    [PreserveSig] int GetVolumeStepInfo(out uint step, out uint stepCount);
    [PreserveSig] int VolumeStepUp(IntPtr eventContext);
    [PreserveSig] int VolumeStepDown(IntPtr eventContext);
    [PreserveSig] int QueryHardwareSupport(out uint mask);
    [PreserveSig] int GetVolumeRange(out float minDb, out float maxDb, out float incrementDb);
}

[ComImport, Guid("657804FA-D6AD-4496-8A60-352752AF4F89"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IAudioEndpointVolumeCallback
{
    [PreserveSig] int OnNotify(IntPtr notificationData);   // AUDIO_VOLUME_NOTIFICATION_DATA*
}

[ComImport, Guid("870AF99C-171D-4F9E-AF0D-E63DF40C2BC9")]
class PolicyConfigComObject { }

[ComImport, Guid("F8679F50-850A-41CF-9C72-430F290290C8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IPolicyConfig
{
    [PreserveSig] int GetMixFormat([MarshalAs(UnmanagedType.LPWStr)] string deviceId, IntPtr format);
    [PreserveSig] int GetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string deviceId, int isDefault, IntPtr format);
    [PreserveSig] int ResetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string deviceId);
    [PreserveSig] int SetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string deviceId, IntPtr endpointFormat, IntPtr mixFormat);
    [PreserveSig] int GetProcessingPeriod([MarshalAs(UnmanagedType.LPWStr)] string deviceId, int isDefault, IntPtr defaultPeriod, IntPtr minPeriod);
    [PreserveSig] int SetProcessingPeriod([MarshalAs(UnmanagedType.LPWStr)] string deviceId, IntPtr period);
    [PreserveSig] int GetShareMode([MarshalAs(UnmanagedType.LPWStr)] string deviceId, IntPtr mode);
    [PreserveSig] int SetShareMode([MarshalAs(UnmanagedType.LPWStr)] string deviceId, IntPtr mode);
    [PreserveSig] int GetPropertyValue([MarshalAs(UnmanagedType.LPWStr)] string deviceId, ref PropertyKey key, IntPtr value);
    [PreserveSig] int SetPropertyValue([MarshalAs(UnmanagedType.LPWStr)] string deviceId, ref PropertyKey key, IntPtr value);
    [PreserveSig] int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string deviceId, ERole role);
    [PreserveSig] int SetEndpointVisibility([MarshalAs(UnmanagedType.LPWStr)] string deviceId, int visible);
}

/// <summary>One render endpoint as the plugin sees it.</summary>
sealed record AudioDevice(string Id, string Name, uint State)
{
    public bool IsActive => State == DeviceState.Active;
}

/// <summary>Snapshot of all render endpoints plus the current defaults.</summary>
sealed record AudioSnapshot(IReadOnlyList<AudioDevice> Devices, string? DefaultMultimedia, string? DefaultCommunications)
{
    public AudioDevice? ById(string? id) => id is null ? null : Devices.FirstOrDefault(d => d.Id == id);

    /// <summary>Active device whose name matches (used when Windows re-enumerates and the ID changes).</summary>
    public AudioDevice? ByName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        var n = Norm(name);
        return Devices.FirstOrDefault(d => d.IsActive && Norm(d.Name) == n);
    }

    static string Norm(string s) => string.Join(' ', s.Split(' ', StringSplitOptions.RemoveEmptyEntries)).ToLowerInvariant();
}

/// <summary>Master volume of one endpoint with change notifications.</summary>
sealed class EndpointVolume : IDisposable
{
    readonly IAudioEndpointVolume _vol;
    readonly Callback _cb;
    public string DeviceId { get; }

    /// <summary>(volume 0..1, muted)</summary>
    public event Action<float, bool>? Changed;

    public EndpointVolume(IMMDevice device, string id)
    {
        DeviceId = id;
        var iid = typeof(IAudioEndpointVolume).GUID;
        var hr = device.Activate(ref iid, 0x17 /*CLSCTX_ALL*/, IntPtr.Zero, out var o);
        if (hr != 0) throw new InvalidOperationException($"Activate(IAudioEndpointVolume) failed: 0x{hr:X8}");
        _vol = (IAudioEndpointVolume)o;
        _cb = new Callback(this);
        _vol.RegisterControlChangeNotify(_cb);
    }

    public float Volume { get { _vol.GetMasterVolumeLevelScalar(out var v); return v; } }
    public bool Muted { get { _vol.GetMute(out var m); return m; } }

    public void SetVolume(float v)
    {
        var hr = _vol.SetMasterVolumeLevelScalar(Math.Clamp(v, 0f, 1f), IntPtr.Zero);
        if (hr != 0) throw new InvalidOperationException($"SetMasterVolumeLevelScalar failed: 0x{hr:X8}");
    }

    public void SetMute(bool mute)
    {
        var hr = _vol.SetMute(mute, IntPtr.Zero);
        if (hr != 0) throw new InvalidOperationException($"SetMute failed: 0x{hr:X8}");
    }

    public void Dispose()
    {
        try { _vol.UnregisterControlChangeNotify(_cb); } catch { }
        try { Marshal.ReleaseComObject(_vol); } catch { }
    }

    sealed class Callback : IAudioEndpointVolumeCallback
    {
        readonly EndpointVolume _owner;
        public Callback(EndpointVolume owner) => _owner = owner;
        public int OnNotify(IntPtr data)
        {
            // AUDIO_VOLUME_NOTIFICATION_DATA: GUID guidEventContext; BOOL bMuted; float fMasterVolume; UINT nChannels; float[]
            var muted = Marshal.ReadInt32(data, 16) != 0;
            var volume = BitConverter.Int32BitsToSingle(Marshal.ReadInt32(data, 20));
            // never do work on the audio engine's callback thread
            var owner = _owner;
            Task.Run(() => owner.Changed?.Invoke(volume, muted));
            return 0;
        }
    }
}

/// <summary>Enumerates render endpoints, raises Changed on any audio topology event, sets defaults.</summary>
sealed class CoreAudio : IDisposable
{
    static readonly PropertyKey PKEY_Device_FriendlyName = new(new Guid("A45C254E-DF1C-4EFD-8020-67D146A850E0"), 14);
    static readonly PropertyKey PKEY_Device_DeviceDesc = new(new Guid("A45C254E-DF1C-4EFD-8020-67D146A850E0"), 2);
    static readonly PropertyKey PKEY_DeviceInterface_FriendlyName = new(new Guid("B3F8FA53-0004-438E-9003-51A46E139BFC"), 6);

    readonly IMMDeviceEnumerator _enumerator;
    readonly Notifier _notifier;

    public event Action? Changed;

    public CoreAudio()
    {
        _enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
        _notifier = new Notifier(this);
        var hr = _enumerator.RegisterEndpointNotificationCallback(_notifier);
        if (hr != 0) Log.Warn($"RegisterEndpointNotificationCallback failed: 0x{hr:X8}");
    }

    public AudioSnapshot Snapshot(EDataFlow flow = EDataFlow.Render)
    {
        var list = new List<AudioDevice>();
        if (_enumerator.EnumAudioEndpoints(flow, DeviceState.All, out var coll) == 0 && coll != null)
        {
            coll.GetCount(out var n);
            for (uint i = 0; i < n; i++)
            {
                if (coll.Item(i, out var dev) != 0 || dev == null) continue;
                try
                {
                    dev.GetId(out var id);
                    dev.GetState(out var state);
                    list.Add(new AudioDevice(id, ReadName(dev), state));
                }
                catch (Exception ex) { Log.Warn($"device read failed: {ex.Message}"); }
                finally { Marshal.ReleaseComObject(dev); }
            }
            Marshal.ReleaseComObject(coll);
        }
        return new AudioSnapshot(list, DefaultId(flow, ERole.Multimedia), DefaultId(flow, ERole.Communications));
    }

    string? DefaultId(EDataFlow flow, ERole role)
    {
        if (_enumerator.GetDefaultAudioEndpoint(flow, role, out var dev) != 0 || dev == null) return null;
        try { dev.GetId(out var id); return id; }
        catch { return null; }
        finally { Marshal.ReleaseComObject(dev); }
    }

    /// <summary>Volume control of the current default multimedia render device, or null when there is none.</summary>
    public EndpointVolume? OpenDefaultVolume()
    {
        if (_enumerator.GetDefaultAudioEndpoint(EDataFlow.Render, ERole.Multimedia, out var dev) != 0 || dev == null) return null;
        try
        {
            dev.GetId(out var id);
            return new EndpointVolume(dev, id);
        }
        catch (Exception ex) { Log.Warn($"open default volume failed: {ex.Message}"); return null; }
        finally { Marshal.ReleaseComObject(dev); }
    }

    static string ReadName(IMMDevice dev)
    {
        if (dev.OpenPropertyStore(0 /*STGM_READ*/, out var store) != 0 || store == null) return "?";
        try
        {
            var full = ReadString(store, PKEY_Device_FriendlyName);
            if (!string.IsNullOrWhiteSpace(full)) return full;
            var desc = ReadString(store, PKEY_Device_DeviceDesc) ?? "";
            var iface = ReadString(store, PKEY_DeviceInterface_FriendlyName) ?? "";
            return iface.Length > 0 ? $"{desc} ({iface})" : desc;
        }
        finally { Marshal.ReleaseComObject(store); }
    }

    static string? ReadString(IPropertyStore store, PropertyKey key)
    {
        if (store.GetValue(ref key, out var pv) != 0) return null;
        try { return pv.GetString(); }
        finally { pv.Clear(); }
    }

    public void SetDefault(string deviceId, bool alsoCommunications)
    {
        var policy = (IPolicyConfig)new PolicyConfigComObject();
        try
        {
            Check(policy.SetDefaultEndpoint(deviceId, ERole.Console), "console");
            Check(policy.SetDefaultEndpoint(deviceId, ERole.Multimedia), "multimedia");
            if (alsoCommunications) Check(policy.SetDefaultEndpoint(deviceId, ERole.Communications), "communications");
        }
        finally { Marshal.ReleaseComObject(policy); }

        static void Check(int hr, string role)
        {
            if (hr != 0) throw new InvalidOperationException($"SetDefaultEndpoint({role}) failed: 0x{hr:X8}");
        }
    }

    public void Dispose()
    {
        try { _enumerator.UnregisterEndpointNotificationCallback(_notifier); } catch { }
    }

    /// <summary>
    /// IMMNotificationClient callbacks run on the audio service's thread and must not call back into
    /// the MMDevice API (that deadlocks). So they only hand the event off to the thread pool.
    /// </summary>
    sealed class Notifier : IMMNotificationClient
    {
        readonly CoreAudio _owner;
        public Notifier(CoreAudio owner) => _owner = owner;
        void Raise() { var o = _owner; Task.Run(() => o.Changed?.Invoke()); }
        public void OnDeviceStateChanged(string deviceId, uint newState) => Raise();
        public void OnDeviceAdded(string deviceId) => Raise();
        public void OnDeviceRemoved(string deviceId) => Raise();
        public void OnDefaultDeviceChanged(EDataFlow flow, ERole role, string? defaultDeviceId) => Raise();   // render and capture
        public void OnPropertyValueChanged(string deviceId, PropertyKey key) { /* names rarely change; ignore */ }
    }
}
