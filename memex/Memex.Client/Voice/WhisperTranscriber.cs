using System.Runtime.CompilerServices;
using Whisper.net;

namespace Memex.Client.Voice;

/// <summary>On-device speech-to-text over 16 kHz mono PCM samples.</summary>
public interface ISpeechTranscriber : IAsyncDisposable
{
    /// <param name="language">A Whisper code ("de", "en", ...), <c>"auto"</c> (detect over all
    /// languages), <c>"auto-de-en"</c> (detect restricted to German/English — Swiss German → German),
    /// or <c>"auto-de-en-first"</c> — PREFER German/Swiss German/English, but fall back to full
    /// auto-detect for any other language (the default for a Swiss user who also speaks others).</param>
    IAsyncEnumerable<TranscriptSegment> TranscribeAsync(
        float[] samples16kMono, string language = "auto-de-en-first", CancellationToken ct = default);
}

/// <summary>
/// On-device Whisper (whisper.cpp via Whisper.net) from a local GGML model. Runs fully on the device —
/// Whisper.net ships native libs for iOS (incl. Metal/GPU), Android, macOS, and Windows.
/// </summary>
public sealed class WhisperTranscriber : ISpeechTranscriber
{
    /// <summary>Sentinel for "detect among German and English only" — Swiss German → German.</summary>
    public const string AutoGermanEnglish = "auto-de-en";

    /// <summary>Sentinel for "prefer German/Swiss German/English, else full auto-detect".</summary>
    public const string AutoPreferGermanEnglish = "auto-de-en-first";

    /// <summary>How confidently the audio must read as German/English to pin it before falling back to
    /// full auto-detect. Swiss German still reads as German well above this; a French/Italian sample
    /// reads de/en low, so it falls through to auto.</summary>
    private const float PreferThreshold = 0.5f;

    private readonly WhisperFactory _factory;

    public WhisperTranscriber(string modelPath) => _factory = WhisperFactory.FromPath(modelPath);

    public async IAsyncEnumerable<TranscriptSegment> TranscribeAsync(
        float[] samples16kMono, string language = AutoPreferGermanEnglish,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var resolved = language switch
        {
            AutoGermanEnglish => DetectGermanOrEnglish(samples16kMono),
            AutoPreferGermanEnglish => DetectPreferGermanEnglish(samples16kMono),
            _ => language,
        };

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

    /// <summary>
    /// PREFER German/Swiss German/English, then fall back to any language. Detects among de/en first
    /// (Swiss German → de); if the audio confidently reads as one of them (≥ <see cref="PreferThreshold"/>)
    /// it's pinned, otherwise it returns "auto" so Whisper detects the actual language (French, Italian,
    /// any) during processing. Gives a Swiss user de/swiss-de/en by default without losing other tongues.
    /// </summary>
    private string DetectPreferGermanEnglish(float[] samples)
    {
        using var processor = _factory.CreateBuilder().WithLanguage("auto").Build();
        var (preferred, probability) = processor.DetectLanguageWithProbability(samples, ["de", "en"]);
        return !string.IsNullOrEmpty(preferred) && probability >= PreferThreshold ? preferred : "auto";
    }

    public ValueTask DisposeAsync()
    {
        _factory.Dispose();
        return ValueTask.CompletedTask;
    }
}
