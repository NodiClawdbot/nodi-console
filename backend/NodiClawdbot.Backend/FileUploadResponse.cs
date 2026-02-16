namespace NodiClawdbot.Backend;

public sealed record FileUploadResponse(
    bool Ok,
    string FileId,
    string FileName,
    string MimeType,
    long SizeBytes,
    string? ObsidianRelativePath);
