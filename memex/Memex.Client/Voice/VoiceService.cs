namespace Memex.Client.Voice;

/// <summary>
/// App-facing entry point: lazily ensures the Whisper model is present, builds the transcriber once,
/// and transcribes 16 kHz mono audio on-device. Safe to register as a singleton.
/// </summary>
public sealed class VoiceService : IAsyncDisposable
{
    private readonly VoiceModelCatalog _catalog;
    private WhisperTranscriber? _whisper;

    public VoiceService(VoiceModelCatalog catalog) => _catalog = catalog;

    public bool Ready => _whisper is not null;

    public async Task<IReadOnlyList<TranscriptSegment>> TranscribeAsync(
        float[] samples16kMono, string language = "auto",
        IProgress<string>? progress = null, CancellationToken ct = default)
    {
        if (_whisper is null)
        {
            await _catalog.EnsureAsync(progress, ct).ConfigureAwait(false);
            _whisper = new WhisperTranscriber(_catalog.WhisperModelPath);
        }

        var result = new List<TranscriptSegment>();
        await foreach (var seg in _whisper.TranscribeAsync(samples16kMono, language, ct).ConfigureAwait(false))
            result.Add(seg);
        return result;
    }

    public async ValueTask DisposeAsync()
    {
        if (_whisper is not null) await _whisper.DisposeAsync().ConfigureAwait(false);
    }
}
