using System.Net.Http.Headers;
using System.Text.Json;
using MeshWeaver.Mesh.Threading;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Speech;

/// <summary>
/// <see cref="ISpeechTranscriber"/> over a <c>whisper.cpp</c> server container. Posts the audio to the
/// server's <c>POST /inference</c> endpoint (multipart <c>file</c> + <c>language</c> + <c>response_format</c>)
/// and returns the recognized text. The HTTP round-trip runs on the bounded HTTP <c>IIoPool</c> — never on a
/// hub/circuit thread — exactly as <c>Doc/Architecture/ControlledIoPooling</c> prescribes; the public surface
/// is a cold <see cref="IObservable{T}"/>, so nothing happens until the caller subscribes.
///
/// <para>Config (endpoint, language, enabled) is read live via a delegate, so the portal can change the
/// container endpoint at runtime without recreating the service.</para>
/// </summary>
public sealed class WhisperContainerTranscriber : ISpeechTranscriber
{
    private readonly HttpClient _http;
    private readonly IIoPool _pool;
    private readonly Func<SpeechConfiguration> _config;
    private readonly ILogger<WhisperContainerTranscriber> _logger;

    public WhisperContainerTranscriber(
        HttpClient http,
        IoPoolRegistry? ioPoolRegistry,
        Func<SpeechConfiguration> config,
        ILogger<WhisperContainerTranscriber> logger)
    {
        _http = http;
        _pool = ioPoolRegistry?.Get(IoPoolNames.Http) ?? IoPool.Unbounded;
        _config = config;
        _logger = logger;
    }

    public bool IsConfigured => _config().IsConfigured;

    public IObservable<SpeechTranscript> Transcribe(ReadOnlyMemory<byte> audio, SpeechTranscriptionOptions? options = null)
        => _pool.Invoke(ct => PostAsync(audio, options ?? new SpeechTranscriptionOptions(), ct));

    private async Task<SpeechTranscript> PostAsync(ReadOnlyMemory<byte> audio, SpeechTranscriptionOptions options, CancellationToken ct)
    {
        var cfg = _config();
        if (!cfg.IsConfigured)
            throw new InvalidOperationException(
                "Speech transcription is not configured. Set Speech:Endpoint to the Whisper container and " +
                "enable it (Speech:Enabled) in the portal.");

        var language = string.IsNullOrWhiteSpace(options.Language) ? cfg.Language : options.Language!;
        var endpoint = cfg.Endpoint!.TrimEnd('/') + "/inference";

        // Per-call timeout via a linked token — the shared HttpClient's Timeout is left untouched.
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(cfg.TimeoutSeconds));

        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(audio.ToArray());
        file.Headers.ContentType = MediaTypeHeaderValue.Parse(options.ContentType);
        form.Add(file, "file", options.FileName);
        form.Add(new StringContent(language), "language");
        form.Add(new StringContent("json"), "response_format"); // whisper.cpp server → {"text": "..."}

        _logger.LogDebug("Transcribing {Bytes} bytes via Whisper container {Endpoint} (lang {Lang})",
            audio.Length, endpoint, language);

        using var response = await _http.PostAsync(endpoint, form, timeoutCts.Token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadAsStringAsync(timeoutCts.Token).ConfigureAwait(false);

        return new SpeechTranscript(ExtractText(body).Trim()) { Language = language };
    }

    /// <summary>whisper.cpp's <c>response_format=json</c> returns <c>{"text": "…"}</c>; be lenient about casing
    /// and fall back to the raw body (which is what <c>response_format=text</c> returns).</summary>
    private static string ExtractText(string body)
    {
        body = body.Trim();
        if (body.Length == 0 || body[0] != '{')
            return body;
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.TryGetProperty("text", out var t) || root.TryGetProperty("Text", out t))
                return t.GetString() ?? string.Empty;
        }
        catch (JsonException)
        {
            // Not JSON after all — return the raw body rather than throwing away a valid transcript.
        }
        return body;
    }
}
