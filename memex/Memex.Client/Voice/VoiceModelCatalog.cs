namespace Memex.Client.Voice;

/// <summary>Which on-device Whisper GGML model to use — a size/quality vs. footprint trade-off.</summary>
public enum WhisperModelSize
{
    /// <summary>~140 MB. Fast and small; OK for clear German/English. Poor on strong Swiss dialect.</summary>
    Base,

    /// <summary>~1.5 GB. Much better, incl. Swiss German (still imperfect on Bernese). Heavier download +
    /// slower, but runs with Metal/GPU on iPhone. The realistic floor for proper dialect.</summary>
    LargeV3Turbo,
}

/// <summary>
/// Resolves and downloads the on-device Whisper GGML model to app data on first use (models are large
/// and not shipped in the app).
///
/// <para>For <b>proper Swiss German (e.g. Bernese)</b>, prefer <see cref="WhisperModelSize.LargeV3Turbo"/>,
/// or drop a <b>Swiss-German fine-tuned</b> model into <see cref="ModelsDirectory"/> and point at it: the
/// HuggingFace fine-tunes are safetensors and must be converted to GGML for whisper.cpp
/// (<c>whisper.cpp/models/convert-h5-to-ggml.py</c>). Either way Swiss German is transcribed AS Standard German.</para>
/// </summary>
public sealed class VoiceModelCatalog
{
    private const string GgmlBaseUrl = "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/";

    public string ModelsDirectory { get; }
    private readonly string _fileName;
    private readonly string _url;

    public VoiceModelCatalog(string modelsDirectory, WhisperModelSize model = WhisperModelSize.Base)
    {
        ModelsDirectory = modelsDirectory;
        Directory.CreateDirectory(modelsDirectory);
        _fileName = model switch
        {
            WhisperModelSize.LargeV3Turbo => "ggml-large-v3-turbo.bin",
            _ => "ggml-base.bin",
        };
        _url = GgmlBaseUrl + _fileName;
    }

    public string WhisperModelPath => Path.Combine(ModelsDirectory, _fileName);

    public bool Present => File.Exists(WhisperModelPath);

    public async Task EnsureAsync(IProgress<string>? progress = null, CancellationToken ct = default)
    {
        if (File.Exists(WhisperModelPath)) return;

        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
        progress?.Report($"Downloading {_fileName} (one time)…");

        var tmp = WhisperModelPath + ".part";
        await using (var src = await http.GetStreamAsync(_url, ct).ConfigureAwait(false))
        await using (var file = File.Create(tmp))
            await src.CopyToAsync(file, ct).ConfigureAwait(false);
        File.Move(tmp, WhisperModelPath, overwrite: true);

        progress?.Report("Model ready.");
    }
}
