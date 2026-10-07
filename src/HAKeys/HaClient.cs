using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DeckKeys;

namespace HAKeys;

public sealed record EntityState(string EntityId, string State, string Name)
{
    public bool IsOn => State is "on" or "open" or "playing" or "home" or "heat" or "cool" or "active" or "locked";
    public bool IsUnavailable => State is "unavailable" or "unknown";
}

/// <summary>
/// One WebSocket connection to Home Assistant for all keys: authenticates with a long-lived token,
/// loads all states once, then follows state_changed events. Reconnects with backoff when the
/// link drops. Everything is event driven; nothing polls.
/// </summary>
public sealed class HaClient : IAsyncDisposable
{
    readonly ConcurrentDictionary<string, EntityState> _states = new();
    readonly ConcurrentDictionary<int, TaskCompletionSource<JsonNode?>> _pending = new();
    readonly SemaphoreSlim _sendGate = new(1, 1);
    readonly object _gate = new();
    ClientWebSocket? _ws;
    CancellationTokenSource? _loop;
    int _nextId = 1;
    string? _url, _token;

    public bool Connected { get; private set; }
    public string? LastError { get; private set; }

    /// <summary>Entity id whose state changed, or null when the whole picture changed (connect/disconnect/full reload).</summary>
    public event Action<string?>? Changed;

    public IReadOnlyCollection<EntityState> States => (IReadOnlyCollection<EntityState>)_states.Values;
    public EntityState? Get(string? entityId) => entityId is not null && _states.TryGetValue(entityId, out var s) ? s : null;

    /// <summary>Starts (or restarts, when url/token changed) the connection loop.</summary>
    public void Configure(string? url, string? token)
    {
        url = url?.Trim().TrimEnd('/');
        token = token?.Trim();
        lock (_gate)
        {
            if (url == _url && token == _token && _loop is not null) return;
            _url = url; _token = token;
            _loop?.Cancel();
            _loop = null;
            Connected = false;
            if (string.IsNullOrEmpty(url) || string.IsNullOrEmpty(token)) { LastError = "not configured"; Log.Warn("HA: not configured (no url/token)"); Changed?.Invoke(null); return; }
            _loop = new CancellationTokenSource();
            var ct = _loop.Token;
            _ = Task.Run(() => RunLoop(url, token, ct), ct);
        }
    }

    /// <summary>Force a reconnect (after system wake-up).</summary>
    public void Kick()
    {
        try { _ws?.Abort(); } catch { }
    }

    async Task RunLoop(string url, string token, CancellationToken ct)
    {
        var delay = 2000;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Session(url, token, ct);
                delay = 2000;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                LastError = ex.Message;
                Log.Warn($"HA: {ex.Message}");
            }
            if (Connected) { Connected = false; Changed?.Invoke(null); }
            FailPending();
            try { await Task.Delay(delay, ct); } catch (OperationCanceledException) { break; }
            delay = Math.Min(delay * 2, 30000);
        }
    }

    async Task Session(string url, string token, CancellationToken ct)
    {
        var wsUrl = (url.StartsWith("https", StringComparison.OrdinalIgnoreCase) ? "wss" : "ws") + url[url.IndexOf("://")..] + "/api/websocket";
        using var ws = new ClientWebSocket();
        _ws = ws;
        await ws.ConnectAsync(new Uri(wsUrl), ct);

        var first = await Receive(ws, ct) ?? throw new IOException("empty hello");
        if (first["type"]?.GetValue<string>() != "auth_required") throw new IOException($"unexpected hello: {first["type"]}");
        await SendRaw(ws, new JsonObject { ["type"] = "auth", ["access_token"] = token }, ct);
        var auth = await Receive(ws, ct) ?? throw new IOException("no auth reply");
        if (auth["type"]?.GetValue<string>() != "auth_ok") throw new IOException("auth failed: " + (auth["message"]?.GetValue<string>() ?? auth["type"]?.GetValue<string>()));
        Log.Info($"HA connected: {auth["ha_version"]}");

        // subscribe first so no change is missed, then load the full picture
        var subId = await SendCommand(ws, new JsonObject { ["type"] = "subscribe_events", ["event_type"] = "state_changed" }, ct);
        var statesId = await SendCommand(ws, new JsonObject { ["type"] = "get_states" }, ct);

        while (!ct.IsCancellationRequested)
        {
            var msg = await Receive(ws, ct);
            if (msg is null) throw new IOException("connection closed");
            var type = msg["type"]?.GetValue<string>();
            var id = msg["id"]?.GetValue<int>() ?? 0;

            if (type == "event" && id == subId)
            {
                var data = msg["event"]?["data"];
                var entityId = data?["entity_id"]?.GetValue<string>();
                if (entityId is null) continue;
                var ns = data?["new_state"];
                if (ns is null) { _states.TryRemove(entityId, out _); Changed?.Invoke(entityId); continue; }
                var st = ToState(ns);
                if (st is null) continue;
                var old = Get(entityId);
                _states[entityId] = st;
                if (old is null || old.State != st.State || old.Name != st.Name) Changed?.Invoke(entityId);
            }
            else if (type == "result")
            {
                if (id == statesId)
                {
                    if (msg["result"] is JsonArray arr)
                    {
                        foreach (var n in arr) { var st = ToState(n); if (st is not null) _states[st.EntityId] = st; }
                        Log.Info($"HA: {_states.Count} entities loaded");
                    }
                    Connected = true;
                    Changed?.Invoke(null);
                }
                else if (_pending.TryRemove(id, out var tcs))
                {
                    if (msg["success"]?.GetValue<bool>() == true) tcs.TrySetResult(msg["result"]);
                    else tcs.TrySetException(new InvalidOperationException(msg["error"]?["message"]?.GetValue<string>() ?? "service call failed"));
                }
            }
            else if (type == "pong") { }
        }
    }

    static EntityState? ToState(JsonNode? n)
    {
        var id = n?["entity_id"]?.GetValue<string>();
        var state = n?["state"]?.GetValue<string>();
        if (id is null || state is null) return null;
        var name = n?["attributes"]?["friendly_name"]?.GetValue<string>() ?? id;
        return new EntityState(id, state, name);
    }

    /// <summary>Calls a service, e.g. ("switch", "toggle", "switch.plug_vr"). Throws on failure.</summary>
    public async Task CallService(string domain, string service, string entityId, CancellationToken ct = default)
    {
        var ws = _ws;
        if (ws is null || ws.State != WebSocketState.Open || !Connected) throw new InvalidOperationException("not connected to Home Assistant");
        var tcs = new TaskCompletionSource<JsonNode?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var id = Interlocked.Increment(ref _nextId);
        _pending[id] = tcs;
        await SendRaw(ws, new JsonObject
        {
            ["id"] = id, ["type"] = "call_service", ["domain"] = domain, ["service"] = service,
            ["target"] = new JsonObject { ["entity_id"] = entityId }
        }, ct);
        using var timeout = new CancellationTokenSource(5000);
        using var reg = timeout.Token.Register(() => tcs.TrySetException(new TimeoutException("Home Assistant did not answer")));
        await tcs.Task;
    }

    async Task<int> SendCommand(ClientWebSocket ws, JsonObject cmd, CancellationToken ct)
    {
        var id = Interlocked.Increment(ref _nextId);
        cmd["id"] = id;
        await SendRaw(ws, cmd, ct);
        return id;
    }

    async Task SendRaw(ClientWebSocket ws, JsonObject msg, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes(msg.ToJsonString());
        await _sendGate.WaitAsync(ct);
        try { await ws.SendAsync(bytes, WebSocketMessageType.Text, true, ct); }
        finally { _sendGate.Release(); }
    }

    static async Task<JsonNode?> Receive(ClientWebSocket ws, CancellationToken ct)
    {
        var buffer = new byte[64 * 1024];
        using var ms = new MemoryStream();
        WebSocketReceiveResult r;
        do
        {
            r = await ws.ReceiveAsync(buffer, ct);
            if (r.MessageType == WebSocketMessageType.Close) return null;
            ms.Write(buffer, 0, r.Count);
        } while (!r.EndOfMessage);
        return JsonNode.Parse(ms.ToArray());
    }

    void FailPending()
    {
        foreach (var kv in _pending) if (_pending.TryRemove(kv.Key, out var tcs)) tcs.TrySetException(new IOException("connection lost"));
    }

    public async ValueTask DisposeAsync()
    {
        _loop?.Cancel();
        try { if (_ws is { State: WebSocketState.Open } ws) await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None); } catch { }
    }
}
