using System.Text.Json;

namespace NodiClawdbot.Backend;

/// <summary>
/// Generic OpenClaw JSON-RPC call wrapper.
/// </summary>
public sealed record OpenClawCallRequest(string Method, JsonElement? Params);
