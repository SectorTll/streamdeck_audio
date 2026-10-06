using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AudioKeys;

/// <summary>WebSocket link to the Stream Deck application. Receives events, sends commands.</summary>
sealed class StreamDeck : IAsyncDisposable
{
    readonly ClientWebSocket _ws = new();
    readonly SemaphoreSlim _sendGate = new(1, 1);
    readonly int _port;
    readonly string _pluginUuid;
    readonly string _registerEvent;

    public event Func<string, JsonNode, Task>? Event;

    public StreamDeck(int port, string pluginUuid, string registerEvent)
    {
        _port = port; _pluginUuid = pluginUuid; _registerEvent = registerEvent;
    }

    public async Task ConnectAsync(CancellationToken ct)
    {
        await _ws.ConnectAsync(new Uri($"ws://127.0.0.1:{_port}"), ct);
        await SendAsync(new JsonObject { ["event"] = _registerEvent, ["uuid"] = _pluginUuid }, ct);
        Log.Info($"registered on port {_port}");
    }

    /// <summary>Reads until the socket closes. Each message is dispatched to Event sequentially.</summary>
    public async Task RunAsync(CancellationToken ct)
    {
        var buffer = new byte[64 * 1024];
        var sb = new StringBuilder();
        while (_ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
        {
            sb.Clear();
            WebSocketReceiveResult r;
            do
            {
                r = await _ws.ReceiveAsync(buffer, ct);
                if (r.MessageType == WebSocketMessageType.Close) { Log.Info("socket closed by Stream Deck"); return; }
                sb.Append(Encoding.UTF8.GetString(buffer, 0, r.Count));
            } while (!r.EndOfMessage);

            JsonNode? node;
            try { node = JsonNode.Parse(sb.ToString()); }
            catch (Exception ex) { Log.Warn($"bad json: {ex.Message}"); continue; }
            var ev = node?["event"]?.GetValue<string>();
            if (ev is null || node is null) continue;
            try { if (Event is not null) await Event(ev, node); }
            catch (Exception ex) { Log.Error($"handler {ev}: {ex}"); }
        }
    }

    public async Task SendAsync(JsonObject msg, CancellationToken ct = default)
    {
        var bytes = Encoding.UTF8.GetBytes(msg.ToJsonString());
        await _sendGate.WaitAsync(ct);
        try { await _ws.SendAsync(bytes, WebSocketMessageType.Text, true, ct); }
        finally { _sendGate.Release(); }
    }

    public Task SetImage(string context, string dataUri) =>
        SendAsync(new JsonObject { ["event"] = "setImage", ["context"] = context, ["payload"] = new JsonObject { ["image"] = dataUri, ["target"] = 0 } });

    public Task ShowAlert(string context) => SendAsync(new JsonObject { ["event"] = "showAlert", ["context"] = context });
    public Task ShowOk(string context) => SendAsync(new JsonObject { ["event"] = "showOk", ["context"] = context });

    public Task SetSettings(string context, JsonObject settings) =>
        SendAsync(new JsonObject { ["event"] = "setSettings", ["context"] = context, ["payload"] = settings });

    public Task SendToPropertyInspector(string context, JsonObject payload) =>
        SendAsync(new JsonObject { ["event"] = "sendToPropertyInspector", ["context"] = context, ["payload"] = payload });

    public Task LogMessage(string message) =>
        SendAsync(new JsonObject { ["event"] = "logMessage", ["payload"] = new JsonObject { ["message"] = message } });

    public async ValueTask DisposeAsync()
    {
        try { if (_ws.State == WebSocketState.Open) await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None); } catch { }
        _ws.Dispose();
    }
}
