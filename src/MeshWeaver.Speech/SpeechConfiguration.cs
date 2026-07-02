namespace MeshWeaver.Speech;

/// <summary>
/// Points the mesh at the centralized speech-to-text container (the Whisper Swiss-German model, previously
/// run on-device in the MAUI client, now hosted once as a container and exposed to every client). Bound from
/// the <c>Speech</c> configuration section and overridable at runtime from the portal.
/// </summary>
public record SpeechConfiguration
{
    /// <summary>Configuration section name (<c>Speech</c> in appsettings / the portal config node).</summary>
    public const string SectionName = "Speech";

    /// <summary>
    /// Base URL of the Whisper container (a <c>whisper.cpp</c> server), e.g. <c>http://whisper:8080</c> in
    /// compose or <c>http://localhost:8080</c> locally. This is the "local model running as container" the
    /// portal configures. Null/empty = transcription is not available.
    /// </summary>
    public string? Endpoint { get; init; }

    /// <summary>
    /// Whisper language hint. <c>"de"</c> (default) transcribes Swiss German OUT as Standard German — the
    /// same behaviour as the on-device path. <c>"auto"</c> lets Whisper detect (mixed de/fr/it), at some
    /// accuracy cost for strong dialect.
    /// </summary>
    public string Language { get; init; } = "de";

    /// <summary>
    /// Informational label for the model the container serves (the container is bound to one model file).
    /// Defaults to the Swiss-German fine-tune shipped on-device (<c>ggml-swiss-german-turbo-q5_0</c>).
    /// </summary>
    public string Model { get; init; } = "swiss-german-turbo-q5_0";

    /// <summary>Per-request timeout. Whisper is not instant; a minute of audio can take several seconds.</summary>
    public int TimeoutSeconds { get; init; } = 120;

    /// <summary>Master on/off toggle (a portal switch). Off => the mic UI stays hidden and calls short-circuit.</summary>
    public bool Enabled { get; init; }

    /// <summary>True only when speech is enabled AND an endpoint is set — the guard every caller checks.</summary>
    public bool IsConfigured => Enabled && !string.IsNullOrWhiteSpace(Endpoint);
}
