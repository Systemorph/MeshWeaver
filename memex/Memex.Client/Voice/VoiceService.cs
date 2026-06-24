using System.Reactive.Linq;
using MeshWeaver.Mesh.Threading;

namespace Memex.Client.Voice;

/// <summary>
/// App-facing entry point: lazily ensures the Whisper model is present, builds the transcriber once,
/// and transcribes 16 kHz mono audio on-device. Reactive — model download AND inference run on the
/// <see cref="IIoPool"/> (off the UI thread, bounded), so subscribing never freezes the UI. Safe to
/// register as a singleton.
/// </summary>
public sealed class VoiceService : IAsyncDisposable
{
    private readonly VoiceModelCatalog _catalog;
    private readonly IIoPool _pool;
    private WhisperTranscriber? _whisper;

    public VoiceService(VoiceModelCatalog catalog, IIoPool pool)
    {
        _catalog = catalog;
        _pool = pool;
    }

    public bool Ready => _whisper is not null;

    /// <summary>The Whisper native runtime actually loaded — e.g. <c>CoreML</c> (Mac GPU/Neural Engine),
    /// <c>Vulkan</c> (Windows/Android GPU), or <c>Cpu</c>. Populated once the first transcription has loaded
    /// the native library; lets the UI show which backend is in use (proof the GPU path engaged).</summary>
    public string Engine => Whisper.net.LibraryLoader.RuntimeOptions.LoadedLibrary?.ToString() ?? "(not loaded)";

    /// <summary>
    /// Streams transcript segments for the given audio. The model is ensured (downloaded/placed) on the
    /// pool on first use, then each segment is produced on the pool and pushed to the subscriber. Nothing
    /// runs on the calling (UI) thread.
    /// </summary>
    /// <param name="progress">Transcription progress 0–100 (a real progress bar).</param>
    /// <param name="status">Coarse status text (e.g. the one-time model download).</param>
    public IObservable<TranscriptSegment> Transcribe(
        float[] samples16kMono, string language,
        IProgress<int>? progress = null, IProgress<string>? status = null)
        => EnsureTranscriber(status)
            .SelectMany(whisper => whisper.Transcribe(samples16kMono, language, progress));

    private IObservable<WhisperTranscriber> EnsureTranscriber(IProgress<string>? status) =>
        _whisper is not null
            ? Observable.Return(_whisper)
            : _pool.Invoke(async ct =>
            {
                await _catalog.EnsureAsync(status, ct).ConfigureAwait(false);
                return _whisper ??= new WhisperTranscriber(_catalog.WhisperModelPath, _pool);
            });

    public async ValueTask DisposeAsync()
    {
        if (_whisper is not null) await _whisper.DisposeAsync().ConfigureAwait(false);
    }
}
