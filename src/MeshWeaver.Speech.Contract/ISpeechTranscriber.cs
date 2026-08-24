namespace MeshWeaver.Speech;

/// <summary>The result of a transcription: the recognized text plus a little metadata.</summary>
/// <param name="Text">The transcript (Swiss German comes out as Standard German with the default model).</param>
public record SpeechTranscript(string Text)
{
    /// <summary>The language the transcript is in (the resolved Whisper language), when the server reports it.</summary>
    public string? Language { get; init; }
}

/// <summary>Per-call knobs; all optional — omitted values fall back to the implementation's own
/// configuration (<c>SpeechConfiguration</c>, which lives with the Whisper client, not here).</summary>
public record SpeechTranscriptionOptions
{
    /// <summary>Override the configured Whisper language for this call (e.g. force <c>"fr"</c>).</summary>
    public string? Language { get; init; }

    /// <summary>MIME type of the audio bytes (default <c>audio/wav</c>). WAV/PCM is the safe interchange format.</summary>
    public string ContentType { get; init; } = "audio/wav";

    /// <summary>Suggested filename for the multipart part (some servers key format detection off the extension).</summary>
    public string FileName { get; init; } = "audio.wav";
}

/// <summary>
/// The centralized speech-to-text surface — one implementation the whole mesh shares, so the Whisper model
/// is configured and hosted in ONE place (a container) and reached from the portal, React Native, and MAUI
/// alike. Cold and reactive per the async rules: the transcription runs on the HTTP <c>IIoPool</c> when the
/// returned observable is subscribed.
/// </summary>
public interface ISpeechTranscriber
{
    /// <summary>True when the implementation is configured and speech is enabled. A host that has no
/// transcriber at all resolves null instead — both mean "no voice", and every caller handles it.</summary>
    bool IsConfigured { get; }

    /// <summary>
    /// Transcribe <paramref name="audio"/> to text. Cold <see cref="IObservable{T}"/>: the HTTP round-trip to
    /// the Whisper container runs on the bounded HTTP I/O pool on subscribe, and emits one
    /// <see cref="SpeechTranscript"/> then completes (or errors — surfaced, never swallowed).
    /// </summary>
    IObservable<SpeechTranscript> Transcribe(ReadOnlyMemory<byte> audio, SpeechTranscriptionOptions? options = null);
}
