using System.Runtime.CompilerServices;
using Whisper.net;

namespace Memex.Client.Voice;

/// <summary>On-device speech-to-text over 16 kHz mono PCM samples.</summary>
public interface ISpeechTranscriber : IAsyncDisposable
{
    /// <param name="language">Whisper language code ("de", "en", ...) or "auto". Swiss German has no
    /// code — pass "de" and Whisper transcribes it as Standard German.</param>
    IAsyncEnumerable<TranscriptSegment> TranscribeAsync(
        float[] samples16kMono, string language = "auto", CancellationToken ct = default);
}

/// <summary>
/// On-device Whisper (whisper.cpp via Whisper.net) from a local GGML model. Runs fully on the device —
/// Whisper.net ships native libs for iOS (incl. Metal/GPU), Android, macOS, and Windows.
/// </summary>
public sealed class WhisperTranscriber : ISpeechTranscriber
{
    private readonly WhisperFactory _factory;

    public WhisperTranscriber(string modelPath) => _factory = WhisperFactory.FromPath(modelPath);

    public async IAsyncEnumerable<TranscriptSegment> TranscribeAsync(
        float[] samples16kMono, string language = "auto",
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        await using var processor = _factory.CreateBuilder()
            .WithLanguage(language)
            .Build();

        await foreach (var seg in processor.ProcessAsync(samples16kMono, ct).ConfigureAwait(false))
            yield return new TranscriptSegment(seg.Start, seg.End, seg.Text.Trim());
    }

    public ValueTask DisposeAsync()
    {
        _factory.Dispose();
        return ValueTask.CompletedTask;
    }
}
