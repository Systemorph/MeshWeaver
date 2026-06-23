using System.Reactive.Linq;
using System.Runtime.CompilerServices;
using MeshWeaver.Mesh.Threading;
using Whisper.net;

namespace Memex.Client.Voice;

/// <summary>On-device speech-to-text over 16 kHz mono PCM samples. Reactive — the heavy CPU work runs on
/// the <see cref="IIoPool"/> (off the UI thread, bounded), so subscribing never blocks the caller.</summary>
public interface ISpeechTranscriber : IAsyncDisposable
{
    /// <param name="language">A Whisper code ("de", "en", "fr", …), <c>"auto"</c> (detect over all
    /// languages), <c>"auto-de-en"</c> (detect restricted to German/English — Swiss German → German),
    /// or <c>"auto-de-en-first"</c> — PREFER German/Swiss German/English, but fall back to full
    /// auto-detect for any other language.</param>
    /// <param name="progress">Reports transcription progress 0–100.</param>
    IObservable<TranscriptSegment> Transcribe(
        float[] samples16kMono, string language = "auto-de-en-first", IProgress<int>? progress = null);
}

/// <summary>
/// On-device Whisper (whisper.cpp via Whisper.net) from a local GGML model. Runs fully on the device —
/// Whisper.net ships native libs for iOS (incl. Metal/GPU), Android, macOS, and Windows. All inference
/// is routed through <see cref="IIoPool"/>: the language-detect pass via <c>InvokeBlocking</c> (CPU) and
/// the transcription via <c>InvokeStream</c> (the <c>ProcessAsync</c> enumerable). See
/// <c>Doc/Architecture/ControlledIoPooling.md</c>.
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

    /// <summary>Detect on only the first ~5 s. Language is obvious in a few seconds, and the detect pass
    /// runs the encoder — doing it over 5 s instead of the default 30 s is the big detection speedup.</summary>
    private const int DetectionSampleLength = 5 * 16000;

    /// <summary>Decode threads — all cores but one. whisper.cpp otherwise caps at 4, leaving cores idle.</summary>
    private static readonly int Threads = Math.Max(1, Environment.ProcessorCount - 1);

    private readonly WhisperFactory _factory;
    private readonly IIoPool _pool;

    public WhisperTranscriber(string modelPath, IIoPool pool)
    {
        _factory = WhisperFactory.FromPath(modelPath);
        _pool = pool;
    }

    public IObservable<TranscriptSegment> Transcribe(
        float[] samples16kMono, string language = AutoPreferGermanEnglish, IProgress<int>? progress = null)
        // Resolve the language off the UI thread (the detect pass runs the encoder), THEN stream the
        // transcription — both bounded on the pool so the UI never blocks.
        => ResolveLanguage(samples16kMono, language)
            .SelectMany(resolved => _pool.InvokeStream(ct => ProcessAsync(samples16kMono, resolved, progress, ct)));

    private IObservable<string> ResolveLanguage(float[] samples, string language) => language switch
    {
        AutoGermanEnglish => _pool.InvokeBlocking(_ => DetectGermanOrEnglish(samples)),
        AutoPreferGermanEnglish => _pool.InvokeBlocking(_ => DetectPreferGermanEnglish(samples)),
        _ => Observable.Return(language),
    };

    private async IAsyncEnumerable<TranscriptSegment> ProcessAsync(
        float[] samples, string language, IProgress<int>? progress, [EnumeratorCancellation] CancellationToken ct)
    {
        await using var processor = _factory.CreateBuilder()
            .WithThreads(Threads)
            .WithLanguage(language)
            .WithProgressHandler(p => progress?.Report(p))
            .Build();

        await foreach (var seg in processor.ProcessAsync(samples, ct).ConfigureAwait(false))
            yield return new TranscriptSegment(seg.Start, seg.End, seg.Text.Trim());
    }

    /// <summary>Detects German/English on the first ~5 s. Swiss German (any dialect) → "de".</summary>
    private string DetectGermanOrEnglish(float[] samples)
    {
        using var processor = _factory.CreateBuilder().WithThreads(Threads).WithLanguage("auto").Build();
        var (language, _) = processor.DetectLanguageWithProbability(DetectionSlice(samples), ["de", "en"]);
        return string.IsNullOrEmpty(language) ? "de" : language;
    }

    /// <summary>Prefer de/en on the first ~5 s; below the confidence threshold, return "auto" so Whisper
    /// detects the actual language (French, Italian, …) during the full transcription pass.</summary>
    private string DetectPreferGermanEnglish(float[] samples)
    {
        using var processor = _factory.CreateBuilder().WithThreads(Threads).WithLanguage("auto").Build();
        var (preferred, probability) = processor.DetectLanguageWithProbability(DetectionSlice(samples), ["de", "en"]);
        return !string.IsNullOrEmpty(preferred) && probability >= PreferThreshold ? preferred : "auto";
    }

    private static float[] DetectionSlice(float[] samples) =>
        samples.Length <= DetectionSampleLength ? samples : samples[..DetectionSampleLength];

    public ValueTask DisposeAsync()
    {
        _factory.Dispose();
        return ValueTask.CompletedTask;
    }
}
