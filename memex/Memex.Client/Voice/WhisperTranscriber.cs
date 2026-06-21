using System.Runtime.CompilerServices;
using Whisper.net;

namespace Memex.Client.Voice;

/// <summary>On-device speech-to-text over 16 kHz mono PCM samples.</summary>
public interface ISpeechTranscriber : IAsyncDisposable
{
    /// <param name="language">A Whisper code ("de", "en", ...), <c>"auto"</c> (detect over all
    /// languages), or <c>"auto-de-en"</c> — detect restricted to German/English so any Swiss German
    /// dialect (incl. Bernese) reliably routes to German rather than a look-alike like Dutch.</param>
    IAsyncEnumerable<TranscriptSegment> TranscribeAsync(
        float[] samples16kMono, string language = "auto", CancellationToken ct = default);
}

/// <summary>
/// On-device Whisper (whisper.cpp via Whisper.net) from a local GGML model. Runs fully on the device —
/// Whisper.net ships native libs for iOS (incl. Metal/GPU), Android, macOS, and Windows.
/// </summary>
public sealed class WhisperTranscriber : ISpeechTranscriber
{
    /// <summary>Sentinel for "detect among German and English only" — Swiss German → German.</summary>
    public const string AutoGermanEnglish = "auto-de-en";

    private readonly WhisperFactory _factory;

    public WhisperTranscriber(string modelPath) => _factory = WhisperFactory.FromPath(modelPath);

    public async IAsyncEnumerable<TranscriptSegment> TranscribeAsync(
        float[] samples16kMono, string language = "auto",
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var resolved = language == AutoGermanEnglish ? DetectGermanOrEnglish(samples16kMono) : language;

        await using var processor = _factory.CreateBuilder()
            .WithLanguage(resolved)
            .Build();

        await foreach (var seg in processor.ProcessAsync(samples16kMono, ct).ConfigureAwait(false))
            yield return new TranscriptSegment(seg.Start, seg.End, seg.Text.Trim());
    }

    /// <summary>
    /// Detects the language constrained to German/English. Swiss German (any dialect, Bernese included)
    /// has no Whisper code, so constraining the candidates makes it resolve to "de" (→ Standard German
    /// output) instead of a phonetic look-alike. English speech still resolves to "en".
    /// </summary>
    private string DetectGermanOrEnglish(float[] samples)
    {
        using var processor = _factory.CreateBuilder().WithLanguage("auto").Build();
        var (language, _) = processor.DetectLanguageWithProbability(samples, ["de", "en"]);
        return string.IsNullOrEmpty(language) ? "de" : language;
    }

    public ValueTask DisposeAsync()
    {
        _factory.Dispose();
        return ValueTask.CompletedTask;
    }
}
