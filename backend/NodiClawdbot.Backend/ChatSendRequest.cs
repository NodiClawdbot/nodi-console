namespace NodiClawdbot.Backend;

public sealed record ChatSendRequest(
    string? Text,
    string? SessionKey,
    string? Thinking,
    int? TimeoutMs,
    string[]? FileIds);
