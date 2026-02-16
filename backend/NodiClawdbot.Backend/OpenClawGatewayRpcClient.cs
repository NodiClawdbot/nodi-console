using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace NodiClawdbot.Backend;

public sealed class OpenClawGatewayRpcClient
{
    public sealed record ChatSendResult(string SessionKey, string RunId, string? FinalText);

    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
    };

    private readonly Uri _wsUrl;
    private readonly string? _token;
    private readonly string? _origin;

    public OpenClawGatewayRpcClient(string wsUrl, string? token, string? origin = null)
    {
        if (string.IsNullOrWhiteSpace(wsUrl))
            throw new ArgumentException("OPENCLAW_URL is required (e.g. ws://127.0.0.1:18790)", nameof(wsUrl));

        _wsUrl = new Uri(wsUrl);
        _token = string.IsNullOrWhiteSpace(token) ? null : token;
        _origin = string.IsNullOrWhiteSpace(origin) ? null : origin.Trim();
    }

    public async Task<JsonElement> CallAsync(string method, object? @params = null, CancellationToken ct = default)
    {
        using var ws = new ClientWebSocket();
        ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
        if (!string.IsNullOrWhiteSpace(_origin))
            ws.Options.SetRequestHeader("Origin", _origin);

        await ws.ConnectAsync(_wsUrl, ct);

        // 1) connect handshake
        await ConnectAsync(ws, ct);

        // 2) call method
        var callId = Guid.NewGuid().ToString();
        await SendJsonAsync(ws, new RequestFrame { Type = "req", Id = callId, Method = method, Params = @params }, ct);

        while (ws.State == WebSocketState.Open)
        {
            var msg = await ReceiveJsonAsync(ws, ct);
            if (msg.ValueKind != JsonValueKind.Object) continue;

            var type = msg.TryGetProperty("type", out var typeEl) ? typeEl.GetString() : null;
            if (type != "res") continue;

            if (!msg.TryGetProperty("id", out var idEl) || idEl.GetString() != callId) continue;

            var ok = msg.TryGetProperty("ok", out var okEl) && okEl.ValueKind == JsonValueKind.True;
            if (ok)
            {
                if (msg.TryGetProperty("payload", out var payload)) return payload;
                return msg;
            }

            var errMsg = (msg.TryGetProperty("error", out var errEl) && errEl.ValueKind == JsonValueKind.Object)
                ? (errEl.TryGetProperty("message", out var m) ? m.GetString() : null)
                : null;
            throw new Exception(errMsg ?? "Gateway call failed");
        }

        throw new Exception("Gateway disconnected");
    }

    public async Task<ChatSendResult> ChatSendWaitFinalAsync(
        string sessionKey,
        string message,
        string? thinking = null,
        int? timeoutMs = null,
        List<Dictionary<string, object?>>? attachments = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(sessionKey)) throw new ArgumentException("sessionKey required", nameof(sessionKey));
        if (message is null) message = "";

        using var ws = new ClientWebSocket();
        ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
        if (!string.IsNullOrWhiteSpace(_origin))
            ws.Options.SetRequestHeader("Origin", _origin);
        await ws.ConnectAsync(_wsUrl, ct);
        await ConnectAsync(ws, ct);

        var runId = Guid.NewGuid().ToString(); // used as idempotencyKey + run id in events
        var callId = Guid.NewGuid().ToString();

        var sendParams = new Dictionary<string, object?>
        {
            ["sessionKey"] = sessionKey,
            ["message"] = message,
            ["idempotencyKey"] = runId,
            ["deliver"] = false,
        };
        if (attachments is { Count: > 0 }) sendParams["attachments"] = attachments;
        if (!string.IsNullOrWhiteSpace(thinking)) sendParams["thinking"] = thinking;
        if (timeoutMs is not null) sendParams["timeoutMs"] = timeoutMs.Value;

        await SendJsonAsync(ws, new RequestFrame { Type = "req", Id = callId, Method = "chat.send", Params = sendParams }, ct);

        // First, wait for the chat.send response (it may just confirm start/accept).
        while (ws.State == WebSocketState.Open)
        {
            var msg = await ReceiveJsonAsync(ws, ct);
            if (msg.ValueKind != JsonValueKind.Object) continue;

            var type = msg.TryGetProperty("type", out var typeEl) ? typeEl.GetString() : null;
            if (type == "res")
            {
                if (!msg.TryGetProperty("id", out var idEl) || idEl.GetString() != callId) continue;
                var ok = msg.TryGetProperty("ok", out var okEl) && okEl.ValueKind == JsonValueKind.True;
                if (!ok)
                {
                    var errMsg = (msg.TryGetProperty("error", out var errEl) && errEl.ValueKind == JsonValueKind.Object)
                        ? (errEl.TryGetProperty("message", out var m) ? m.GetString() : null)
                        : null;
                    throw new Exception(errMsg ?? "chat.send failed");
                }
                break; // now wait for final event
            }
        }

        var accumulated = "";
        var finalSeen = false;

        static string? ExtractContentText(JsonElement payload)
        {
            // payload.message.content[0].text
            if (!payload.TryGetProperty("message", out var m) || m.ValueKind != JsonValueKind.Object) return null;
            if (!m.TryGetProperty("content", out var c) || c.ValueKind != JsonValueKind.Array || c.GetArrayLength() == 0) return null;
            var first = c[0];
            if (first.ValueKind != JsonValueKind.Object) return null;
            if (!first.TryGetProperty("text", out var t)) return null;
            return t.GetString();
        }

        while (ws.State == WebSocketState.Open)
        {
            var msg = await ReceiveJsonAsync(ws, ct);
            if (msg.ValueKind != JsonValueKind.Object) continue;

            var type = msg.TryGetProperty("type", out var typeEl) ? typeEl.GetString() : null;
            if (type != "event") continue;

            var ev = msg.TryGetProperty("event", out var evEl) ? evEl.GetString() : null;
            if (ev != "chat") continue;

            if (!msg.TryGetProperty("payload", out var payload) || payload.ValueKind != JsonValueKind.Object) continue;
            if (!payload.TryGetProperty("runId", out var runIdEl) || runIdEl.GetString() != runId) continue;

            var state = payload.TryGetProperty("state", out var stEl) ? stEl.GetString() : null;
            if (state == "delta")
            {
                var t = ExtractContentText(payload) ?? "";

                // Some gateways stream full-so-far text, others stream token deltas.
                // Handle both: if the new chunk starts with what we already have, treat it as full-so-far.
                if (t.StartsWith(accumulated, StringComparison.Ordinal))
                    accumulated = t;
                else
                    accumulated += t;
            }
            else if (state == "final")
            {
                finalSeen = true;

                // If the final event includes content, prefer that; else fall back to what we accumulated.
                var t = ExtractContentText(payload);
                if (!string.IsNullOrEmpty(t))
                {
                    if (t.StartsWith(accumulated, StringComparison.Ordinal)) accumulated = t;
                    else accumulated += t;
                }

                return new ChatSendResult(sessionKey, runId, accumulated);
            }
            else if (state == "error")
            {
                var errMsg = payload.TryGetProperty("errorMessage", out var e) ? e.GetString() : null;
                throw new Exception(errMsg ?? "chat run error");
            }
            else if (state == "aborted")
            {
                throw new Exception("chat run aborted");
            }
        }

        if (finalSeen)
            return new ChatSendResult(sessionKey, runId, accumulated);

        throw new Exception("Gateway disconnected during chat");
    }

    private async Task<JsonElement> ConnectAsync(ClientWebSocket ws, CancellationToken ct)
    {
        var connectId = Guid.NewGuid().ToString();
        var connectSent = false;
        string? nonce = null;

        async Task SendConnectAsync(string? nonceValue)
        {
            var client = new Dictionary<string, object?>
            {
                // Must match one of the Gateway client IDs (see OpenClaw protocol client-info.ts)
                ["id"] = "webchat",
                ["displayName"] = "nodi-clawdbot webchat bridge",
                ["version"] = "dev",
                ["platform"] = "dotnet",
                ["mode"] = "webchat",
            };

            Dictionary<string, object?>? auth = null;
            if (_token is not null)
            {
                auth = new Dictionary<string, object?>
                {
                    ["token"] = _token,
                };
            }

            var connectParams = new Dictionary<string, object?>
            {
                ["minProtocol"] = 3,
                ["maxProtocol"] = 3,
                ["client"] = client,
                ["caps"] = Array.Empty<string>(),
                ["role"] = "operator",
                ["scopes"] = new[] { "operator.admin" },
            };
            if (auth is not null)
                connectParams["auth"] = auth;

            var req = new RequestFrame
            {
                Type = "req",
                Id = connectId,
                Method = "connect",
                Params = connectParams,
            };

            // Note: nonce is carried inside the signed device payload in the full client.
            // For shared-token auth, the gateway generally accepts connect without device signature.
            // If it ever requires a challenge nonce for this mode, we can extend the payload.

            await SendJsonAsync(ws, req, ct);
            connectSent = true;
        }

        // Fire a delayed connect (mimic JS client behavior).
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(750, ct);
                if (!connectSent && ws.State == WebSocketState.Open)
                    await SendConnectAsync(nonce);
            }
            catch
            {
                // ignore
            }
        }, ct);

        while (ws.State == WebSocketState.Open)
        {
            var msg = await ReceiveJsonAsync(ws, ct);
            if (msg.ValueKind != JsonValueKind.Object)
                continue;

            // event frames
            if (msg.TryGetProperty("type", out var typeEl) && typeEl.GetString() == "event")
            {
                if (msg.TryGetProperty("event", out var evEl) && evEl.GetString() == "connect.challenge")
                {
                    // Best-effort: capture nonce (even if we don't use it yet).
                    if (msg.TryGetProperty("payload", out var payload) && payload.ValueKind == JsonValueKind.Object)
                    {
                        if (payload.TryGetProperty("nonce", out var nonceEl) && nonceEl.ValueKind == JsonValueKind.String)
                            nonce = nonceEl.GetString();
                    }
                    if (!connectSent)
                        await SendConnectAsync(nonce);
                }
                continue;
            }

            // response frames
            if (msg.TryGetProperty("type", out var t2) && t2.GetString() == "res")
            {
                if (!msg.TryGetProperty("id", out var idEl) || idEl.GetString() != connectId)
                    continue;

                var ok = msg.TryGetProperty("ok", out var okEl) && okEl.ValueKind == JsonValueKind.True;
                if (ok)
                {
                    if (msg.TryGetProperty("payload", out var payload))
                        return payload;
                    return msg;
                }

                if (msg.TryGetProperty("error", out var errEl) && errEl.ValueKind == JsonValueKind.Object)
                {
                    var errMsg = errEl.TryGetProperty("message", out var m) ? m.GetString() : null;
                    throw new Exception(errMsg ?? "Gateway connect failed");
                }

                throw new Exception("Gateway connect failed");
            }
        }

        throw new Exception("Gateway disconnected during connect");
    }

    private static async Task SendJsonAsync(ClientWebSocket ws, object obj, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(obj, JsonOpts);
        var bytes = Encoding.UTF8.GetBytes(json);
        await ws.SendAsync(bytes, WebSocketMessageType.Text, true, ct);
    }

    private static async Task<JsonElement> ReceiveJsonAsync(ClientWebSocket ws, CancellationToken ct)
    {
        var buffer = new ArraySegment<byte>(new byte[1024 * 256]);
        using var ms = new MemoryStream();

        while (true)
        {
            var result = await ws.ReceiveAsync(buffer, ct);
            if (result.MessageType == WebSocketMessageType.Close)
                throw new Exception($"gateway closed: {ws.CloseStatus} {ws.CloseStatusDescription}");

            ms.Write(buffer.Array!, buffer.Offset, result.Count);
            if (result.EndOfMessage)
                break;
        }

        var str = Encoding.UTF8.GetString(ms.ToArray());
        using var doc = JsonDocument.Parse(str);
        return doc.RootElement.Clone();
    }

    private sealed class RequestFrame
    {
        public string Type { get; set; } = "req";
        public string Id { get; set; } = "";
        public string Method { get; set; } = "";
        public object? Params { get; set; }
    }
}
