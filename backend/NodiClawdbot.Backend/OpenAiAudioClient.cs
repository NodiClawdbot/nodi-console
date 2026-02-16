using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace NodiClawdbot.Backend;

public sealed class OpenAiAudioClient
{
    private static readonly Uri BaseUri = new("https://api.openai.com/v1/");

    private readonly HttpClient _http;
    private readonly string _apiKey;

    public OpenAiAudioClient(HttpClient http, string apiKey)
    {
        _http = http;
        _apiKey = apiKey;
    }

    public async Task<string> TranscribeAsync(byte[] audioBytes, string fileName, string mimeType, CancellationToken ct, string language = "sv")
    {
        // Whisper endpoint
        // POST /v1/audio/transcriptions (multipart)
        using var req = new HttpRequestMessage(HttpMethod.Post, new Uri(BaseUri, "audio/transcriptions"));
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);

        var content = new MultipartFormDataContent();

        var fileContent = new ByteArrayContent(audioBytes);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue(mimeType);
        content.Add(fileContent, "file", fileName);
        content.Add(new StringContent("whisper-1"), "model");
        content.Add(new StringContent("json"), "response_format");

        // Hint the language to reduce mis-transcriptions.
        // (If omitted, Whisper auto-detects, but can hallucinate more on near-silence.)
        if (!string.IsNullOrWhiteSpace(language))
            content.Add(new StringContent(language), "language");

        req.Content = content;

        using var res = await _http.SendAsync(req, ct);
        var body = await res.Content.ReadAsStringAsync(ct);
        if (!res.IsSuccessStatusCode)
            throw new Exception($"OpenAI transcribe failed ({(int)res.StatusCode}): {body}");

        using var doc = JsonDocument.Parse(body);
        if (doc.RootElement.TryGetProperty("text", out var t))
            return t.GetString() ?? string.Empty;

        return string.Empty;
    }

    public async Task<byte[]> SpeechAsync(string text, string voice, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, new Uri(BaseUri, "audio/speech"));
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);

        var payload = new
        {
            model = "tts-1",
            voice = voice,
            format = "mp3",
            input = text,
        };

        req.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

        using var res = await _http.SendAsync(req, ct);
        if (!res.IsSuccessStatusCode)
        {
            var body = await res.Content.ReadAsStringAsync(ct);
            throw new Exception($"OpenAI TTS failed ({(int)res.StatusCode}): {body}");
        }

        return await res.Content.ReadAsByteArrayAsync(ct);
    }
}
