using System.Text.Json;
using NodiClawdbot.Backend;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSignalR();
builder.Services.AddHttpClient();

// Minimal CORS for local dev (adjust later)
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(p =>
        p.AllowAnyHeader()
         .AllowAnyMethod()
         .AllowCredentials()
         .SetIsOriginAllowed(_ => true));
});

var app = builder.Build();

app.UseRouting();
app.UseCors();

app.MapGet("/health", () => Results.Ok(new { ok = true, app = "nodi-console" }));

app.MapGet("/api/openclaw/ping", (IConfiguration cfg) =>
{
    var target = cfg["OPENCLAW_URL"] ?? "(unset)";
    var hasToken = !string.IsNullOrWhiteSpace(cfg["OPENCLAW_GATEWAY_TOKEN"]);
    return Results.Ok(new { ok = true, openclawUrl = target, hasToken, note = "WS RPC client wired; use /api/openclaw/health" });
});

app.MapGet("/api/openclaw/health", async (IConfiguration cfg, CancellationToken ct) =>
{
    var url = cfg["OPENCLAW_URL"] ?? "";
    var token = cfg["OPENCLAW_GATEWAY_TOKEN"];
    var origin = cfg["OPENCLAW_ORIGIN"];
    var client = new OpenClawGatewayRpcClient(url, token, origin);
    var payload = await client.CallAsync("health", null, ct);
    return Results.Ok(payload);
});

app.MapGet("/api/openclaw/status", async (IConfiguration cfg, CancellationToken ct) =>
{
    var url = cfg["OPENCLAW_URL"] ?? "";
    var token = cfg["OPENCLAW_GATEWAY_TOKEN"];
    var origin = cfg["OPENCLAW_ORIGIN"];
    var client = new OpenClawGatewayRpcClient(url, token, origin);
    var payload = await client.CallAsync("status", null, ct);
    return Results.Ok(payload);
});

// Generic OpenClaw call passthrough (useful for iterating on the contract)
app.MapPost("/api/openclaw/call", async (IConfiguration cfg, OpenClawCallRequest req, CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(req.Method))
        return Results.BadRequest(new { ok = false, error = "Missing method" });

    var url = cfg["OPENCLAW_URL"] ?? "";
    var token = cfg["OPENCLAW_GATEWAY_TOKEN"];
    var origin = cfg["OPENCLAW_ORIGIN"];
    var client = new OpenClawGatewayRpcClient(url, token, origin);

    object? p = null;
    if (req.Params is { ValueKind: not JsonValueKind.Undefined and not JsonValueKind.Null })
        p = req.Params.Value;

    var payload = await client.CallAsync(req.Method.Trim(), p, ct);
    return Results.Ok(new { ok = true, payload });
});

static string UploadRootDir() => "/tmp/nodi-clawdbot/uploads";

static string SanitizeFileName(string name)
{
    var n = (name ?? "file").Trim();
    n = n.Replace("/", "-").Replace("\\", "-");
    foreach (var c in Path.GetInvalidFileNameChars()) n = n.Replace(c, '_');
    if (n.Length == 0) n = "file";
    return n;
}

static bool IsAllowedMime(string mime) =>
    mime.StartsWith("image/") || mime == "application/pdf";

// Upload a file to be used in chat (and optionally persisted into Obsidian vault)
app.MapPost("/api/files/upload", async (IConfiguration cfg, HttpRequest request, CancellationToken ct) =>
{
    const long MaxBytes = 25L * 1024L * 1024L;

    if (!request.HasFormContentType)
        return Results.BadRequest(new { ok = false, error = "Expected multipart/form-data" });

    var form = await request.ReadFormAsync(ct);
    var file = form.Files.GetFile("file");
    if (file is null || file.Length == 0)
        return Results.BadRequest(new { ok = false, error = "Missing file" });

    if (file.Length > MaxBytes)
        return Results.BadRequest(new { ok = false, error = $"File too large (max {MaxBytes} bytes)" });

    var mime = (file.ContentType ?? "application/octet-stream").Split(';')[0].Trim().ToLowerInvariant();
    if (!IsAllowedMime(mime))
        return Results.BadRequest(new { ok = false, error = $"Unsupported mime type: {mime}" });

    var fileName = SanitizeFileName(string.IsNullOrWhiteSpace(file.FileName) ? "upload" : file.FileName);
    var fileId = Guid.NewGuid().ToString("N");

    Directory.CreateDirectory(UploadRootDir());
    var localPath = Path.Combine(UploadRootDir(), $"{fileId}__{fileName}");

    await using (var fs = File.Create(localPath))
    {
        await file.CopyToAsync(fs, ct);
    }

    string? obsRel = null;
    var vault = cfg["OBSIDIAN_VAULT_PATH"] ?? cfg["VAULT_PATH"];
    if (!string.IsNullOrWhiteSpace(vault) && Directory.Exists(vault))
    {
        var relDir = Path.Combine("99_📎_Bilagor", "nodi-clawdbot");
        var targetDir = Path.Combine(vault, relDir);
        Directory.CreateDirectory(targetDir);

        // Prefix with date for readability
        var ts = DateTimeOffset.Now.ToString("yyyy-MM-dd_HHmmss");
        var obsName = $"{ts}__{fileName}";
        var obsPath = Path.Combine(targetDir, obsName);
        File.Copy(localPath, obsPath, overwrite: true);

        obsRel = Path.Combine(relDir, obsName).Replace('\\', '/');
    }

    return Results.Ok(new FileUploadResponse(
        Ok: true,
        FileId: fileId,
        FileName: fileName,
        MimeType: mime,
        SizeBytes: file.Length,
        ObsidianRelativePath: obsRel));
});

static async Task<string> PdfToTextAsync(string pdfPath, CancellationToken ct)
{
    var outPath = Path.Combine(Path.GetDirectoryName(pdfPath)!, Path.GetFileName(pdfPath) + ".txt");

    var psi = new System.Diagnostics.ProcessStartInfo
    {
        FileName = "pdftotext",
        // -layout keeps columns somewhat; -nopgbrk avoids page break markers
        Arguments = $"-layout -nopgbrk \"{pdfPath}\" \"{outPath}\"",
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false,
        CreateNoWindow = true,
    };

    using var p = System.Diagnostics.Process.Start(psi);
    if (p is null) throw new Exception("Failed to start pdftotext");

    await p.WaitForExitAsync(ct);

    if (p.ExitCode != 0)
    {
        var err = await p.StandardError.ReadToEndAsync(ct);
        throw new Exception($"pdftotext failed ({p.ExitCode}): {err}");
    }

    if (!File.Exists(outPath)) return "";
    return await File.ReadAllTextAsync(outPath, ct);
}

app.MapPost("/api/chat/send", async (IConfiguration cfg, ChatSendRequest req, CancellationToken ct) =>
{
    var url = cfg["OPENCLAW_URL"] ?? "";
    var token = cfg["OPENCLAW_GATEWAY_TOKEN"];
    var origin = cfg["OPENCLAW_ORIGIN"];
    var client = new OpenClawGatewayRpcClient(url, token, origin);

    var sessionKey = string.IsNullOrWhiteSpace(req.SessionKey)
        ? $"agent:main:webchat:{Guid.NewGuid()}"
        : req.SessionKey.Trim();

    var message = (req.Text ?? string.Empty);

    // Resolve uploaded files
    var attachments = new List<Dictionary<string, object?>>();
    var fileIds = req.FileIds ?? Array.Empty<string>();
    foreach (var id in fileIds.Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x.Trim()))
    {
        var dir = UploadRootDir();
        if (!Directory.Exists(dir)) continue;
        var match = Directory.GetFiles(dir, id + "__*").FirstOrDefault();
        if (match is null) continue;

        var fn = Path.GetFileName(match);
        var idx = fn.IndexOf("__", StringComparison.Ordinal);
        var originalName = idx >= 0 ? fn[(idx + 2)..] : fn;

        var mime = "application/octet-stream";
        // naive mime from extension
        var ext = Path.GetExtension(originalName).ToLowerInvariant();
        if (ext is ".png") mime = "image/png";
        else if (ext is ".jpg" or ".jpeg") mime = "image/jpeg";
        else if (ext is ".webp") mime = "image/webp";
        else if (ext is ".gif") mime = "image/gif";
        else if (ext is ".pdf") mime = "application/pdf";

        var bytes = await File.ReadAllBytesAsync(match, ct);
        if (bytes.LongLength > 25L * 1024L * 1024L) throw new Exception("Attachment exceeds max size");

        if (mime.StartsWith("image/"))
        {
            var b64 = Convert.ToBase64String(bytes);
            attachments.Add(new Dictionary<string, object?>
            {
                ["type"] = "image",
                ["mimeType"] = mime,
                ["fileName"] = originalName,
                ["content"] = $"data:{mime};base64,{b64}",
            });
        }
        else if (mime == "application/pdf")
        {
            // Extract text and append to message so the agent can analyze it.
            var text = await PdfToTextAsync(match, ct);
            text = (text ?? "").Trim();
            if (text.Length > 30_000) text = text[..30_000] + "\n\n[...truncated]";

            var header = $"\n\n[PDF: {originalName}]\n";
            var body = text.Length > 0 ? $"\n```\n{text}\n```\n" : "\n(kunde inte extrahera text)\n";
            message += header + body;
        }
    }

    // Cost control: only switch to a paid vision-capable model when we actually have image attachments.
    // We also isolate the model override into a separate session key so the normal chat session stays on the default model.
    var hasImages = attachments.Any(a => (a.TryGetValue("type", out var t) && (t as string) == "image"));
    var sessionKeyForRun = hasImages ? (sessionKey + ":vision") : sessionKey;
    if (hasImages)
    {
        // Set model via sessions.patch (slash directives won't work reliably because the gateway injects timestamps).
        await client.CallAsync("sessions.patch", new { key = sessionKeyForRun, model = "anthropic/claude-sonnet-4-5" }, ct);
    }

    var result = await client.ChatSendWaitFinalAsync(
        sessionKey: sessionKeyForRun,
        message: message,
        thinking: req.Thinking,
        timeoutMs: req.TimeoutMs,
        attachments: attachments.Count > 0 ? attachments : null,
        ct: ct);

    return Results.Ok(new
    {
        ok = true,
        sessionKey = sessionKey,
        runId = result.RunId,
        text = result.FinalText,
    });
});

app.MapPost("/api/stt", async (IConfiguration cfg, IHttpClientFactory httpFactory, HttpRequest request, CancellationToken ct) =>
{
    var apiKey = cfg["OPENAI_API_KEY"];
    if (string.IsNullOrWhiteSpace(apiKey))
        return Results.BadRequest(new { ok = false, error = "OPENAI_API_KEY is not set" });

    if (!request.HasFormContentType)
        return Results.BadRequest(new { ok = false, error = "Expected multipart/form-data" });

    var form = await request.ReadFormAsync(ct);
    var file = form.Files.GetFile("file");
    if (file is null || file.Length == 0)
        return Results.BadRequest(new { ok = false, error = "Missing file" });

    await using var ms = new MemoryStream();
    await file.CopyToAsync(ms, ct);

    var bytes = ms.ToArray();
    var mime = string.IsNullOrWhiteSpace(file.ContentType) ? "audio/webm" : file.ContentType;
    var name = string.IsNullOrWhiteSpace(file.FileName) ? "audio.webm" : file.FileName;

    var client = new OpenAiAudioClient(httpFactory.CreateClient(), apiKey);
    var text = await client.TranscribeAsync(bytes, name, mime, ct, language: "sv");

    return Results.Ok(new { ok = true, text });
});

app.MapPost("/api/tts", async (IConfiguration cfg, IHttpClientFactory httpFactory, TtsRequest req, CancellationToken ct) =>
{
    var apiKey = cfg["OPENAI_API_KEY"];
    if (string.IsNullOrWhiteSpace(apiKey))
        return Results.BadRequest(new { ok = false, error = "OPENAI_API_KEY is not set" });

    var input = (req.Text ?? string.Empty).Trim();
    if (input.Length == 0)
        return Results.BadRequest(new { ok = false, error = "Missing text" });

    var voice = string.IsNullOrWhiteSpace(req.Voice) ? "alloy" : req.Voice;

    var client = new OpenAiAudioClient(httpFactory.CreateClient(), apiKey);
    var mp3 = await client.SpeechAsync(input, voice, ct);

    return Results.File(mp3, "audio/mpeg", "speech.mp3");
});

static string EscapeCell(string s)
{
    // Keep markdown pipe-table safe.
    var t = (s ?? string.Empty).Replace("|", "\\|");
    t = t.Replace("\r\n", "\n").Replace("\r", "\n");
    t = t.Replace("\n", "<br>");
    t = t.Replace("\t", " ");
    return t.Trim();
}

static string DeriveTitle(string text)
{
    var t = (text ?? string.Empty).Trim();
    if (t.Length == 0) return "(tom)";

    // Use first sentence/line as title.
    var firstLine = t.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? t;
    firstLine = firstLine.Trim();

    // Cut at sentence end if early.
    var m = System.Text.RegularExpressions.Regex.Match(firstLine, @"^(.{12,90}?)[\.!\?](\s|$)");
    var title = m.Success ? m.Groups[1].Value : firstLine;

    title = title.Trim();
    if (title.Length > 90) title = title[..90].TrimEnd() + "…";
    return title;
}

static async Task<(string title, string summary)> TrySummarizeAsync(IConfiguration cfg, string text, CancellationToken ct)
{
    // Best-effort summary using OpenClaw (if configured). If it fails, fall back to empty summary.
    try
    {
        var url = cfg["OPENCLAW_URL"] ?? "";
        var token = cfg["OPENCLAW_GATEWAY_TOKEN"];
        var origin = cfg["OPENCLAW_ORIGIN"];

        if (string.IsNullOrWhiteSpace(url))
            return (DeriveTitle(text), "");

        var client = new OpenClawGatewayRpcClient(url, token, origin);

        var prompt = "Du får en text som ska sparas i en Obsidian inbox. " +
                     "Svara som strikt JSON med fälten title och summary. " +
                     "title: max 90 tecken. summary: max 280 tecken. Språk: svenska. " +
                     "Ingen extra text.\n\nTEXT:\n" + text;

        // Use a dedicated low-stakes session key.
        var sessionKey = $"agent:ops:web:nodi-inbox-summarize";
        var result = await client.ChatSendWaitFinalAsync(sessionKey, prompt, thinking: "off", timeoutMs: 20000, attachments: null, ct: ct);
        var raw = (result.FinalText ?? string.Empty).Trim();

        // Parse JSON (best-effort)
        using var doc = JsonDocument.Parse(raw);
        var root = doc.RootElement;
        var title = root.TryGetProperty("title", out var tt) ? (tt.GetString() ?? "") : "";
        var summary = root.TryGetProperty("summary", out var ss) ? (ss.GetString() ?? "") : "";

        title = string.IsNullOrWhiteSpace(title) ? DeriveTitle(text) : title.Trim();
        if (title.Length > 90) title = title[..90].TrimEnd() + "…";

        summary = (summary ?? "").Trim();
        if (summary.Length > 280) summary = summary[..280].TrimEnd() + "…";

        return (title, summary);
    }
    catch
    {
        return (DeriveTitle(text), "");
    }
}

app.MapPost("/api/inbox/capture", async (IConfiguration cfg, InboxCaptureRequest req, CancellationToken ct) =>
{
    var input = (req.Text ?? string.Empty).Trim();
    if (input.Length == 0)
        return Results.BadRequest(new { ok = false, error = "Missing text" });

    var vault = cfg["OBSIDIAN_VAULT_PATH"] ?? cfg["VAULT_PATH"];
    if (string.IsNullOrWhiteSpace(vault) || !Directory.Exists(vault))
        return Results.BadRequest(new { ok = false, error = "Vault path not configured" });

    var rel = cfg["INBOX_NOTE_REL"] ?? "00_🕸️_Inbox/Nodi Inbox.md";
    var target = Path.Combine(vault, rel);
    Directory.CreateDirectory(Path.GetDirectoryName(target)!);

    var (title, summary) = await TrySummarizeAsync(cfg, input, ct);

    var header = "| Datum | Rubrik | Text | Sammanfattning |\n|---|---|---|---|\n";

    if (!File.Exists(target))
    {
        await File.WriteAllTextAsync(target, "# Nodi Inbox\n\n" + header, ct);
    }
    else
    {
        // If the file exists but doesn't yet have the table header, append it once.
        var existing = await File.ReadAllTextAsync(target, ct);
        if (!existing.Contains("| Datum | Rubrik | Text | Sammanfattning |", StringComparison.Ordinal))
        {
            if (!existing.EndsWith("\n")) existing += "\n";
            existing += "\n" + header;
            await File.WriteAllTextAsync(target, existing, ct);
        }
    }

    var ts = DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm");
    var row = $"| {EscapeCell(ts)} | {EscapeCell(title)} | {EscapeCell(input)} | {EscapeCell(summary)} |\n";

    await File.AppendAllTextAsync(target, row, ct);

    return Results.Ok(new { ok = true, file = rel.Replace('\\', '/'), title, summary });
});

static string VaultRoot(IConfiguration cfg) => cfg["OBSIDIAN_VAULT_PATH"] ?? cfg["VAULT_PATH"] ?? "/vault";

static string? SafeReadNote(IConfiguration cfg, string rel)
{
    try
    {
        var vault = VaultRoot(cfg);
        if (string.IsNullOrWhiteSpace(vault) || !Directory.Exists(vault)) return null;
        var p = Path.Combine(vault, rel);
        if (!File.Exists(p)) return null;
        return File.ReadAllText(p);
    }
    catch
    {
        return null;
    }
}

static string? ExtractFirstBulletUnder(string text, string heading)
{
    var lines = text.Replace("\r", "").Split('\n');
    var inSection = false;

    for (var i = 0; i < lines.Length; i++)
    {
        var line = lines[i].TrimEnd();
        if (line.Trim().Equals(heading, StringComparison.OrdinalIgnoreCase))
        {
            inSection = true;
            continue;
        }
        if (inSection && line.StartsWith("## ")) break;
        if (!inSection) continue;

        var t = line.TrimStart();
        if (t.StartsWith("- ")) return t[2..].Trim();
    }

    return null;
}

app.MapGet("/api/ops/latest", (IConfiguration cfg) =>
{
    var rel = cfg["OPS_STATUS_NOTE_REL"] ?? "🤖 Ops status.md";
    var txt = SafeReadNote(cfg, rel);
    if (string.IsNullOrWhiteSpace(txt)) return Results.Ok(new { ok = true, line = (string?)null, note = rel });

    var line = ExtractFirstBulletUnder(txt, "## Senaste check");
    return Results.Ok(new { ok = true, line, note = rel });
});

app.MapGet("/api/approvals/summary", (IConfiguration cfg) =>
{
    var rel = cfg["ROUTING_NOTE_REL"] ?? "🤖 Nodi routing (förslag).md";
    var txt = SafeReadNote(cfg, rel);
    if (string.IsNullOrWhiteSpace(txt))
        return Results.Ok(new { ok = true, pending = 0, approved = 0, note = rel });

    var pending = 0;
    var approved = 0;
    var lines = txt.Replace("\r", "").Split('\n');
    var inApprove = false;

    foreach (var ln in lines)
    {
        var t = ln.Trim();
        if (t.StartsWith("## Godkänn", StringComparison.OrdinalIgnoreCase)) { inApprove = true; continue; }
        if (inApprove && t.StartsWith("## ")) break;
        if (!inApprove) continue;

        if (t.StartsWith("- [ ]")) pending++;
        else if (t.StartsWith("- [x]", StringComparison.OrdinalIgnoreCase)) approved++;
    }

    return Results.Ok(new { ok = true, pending, approved, note = rel });
});

app.MapHub<ChatHub>("/hub/chat");

app.Run("http://0.0.0.0:5300");
