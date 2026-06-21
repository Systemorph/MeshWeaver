namespace Memex.Client.Voice;

/// <summary>
/// Resolves and downloads the on-device Whisper GGML model. The model is large (~140 MB) so it is
/// NOT shipped in the app — it downloads to the app data directory on first use. Swap the URL for
/// a Swiss-German fine-tune for better Schwyzerdütsch (transcribed as Standard German).
/// </summary>
public sealed class VoiceModelCatalog
{
    public string ModelsDirectory { get; }

    public VoiceModelCatalog(string modelsDirectory)
    {
        ModelsDirectory = modelsDirectory;
        Directory.CreateDirectory(modelsDirectory);
    }

    public string WhisperModelPath => Path.Combine(ModelsDirectory, "ggml-base.bin");

    public const string WhisperModelUrl =
        "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-base.bin";

    public bool Present => File.Exists(WhisperModelPath);

    public async Task EnsureAsync(IProgress<string>? progress = null, CancellationToken ct = default)
    {
        if (File.Exists(WhisperModelPath)) return;

        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        progress?.Report("Downloading Whisper model (~140 MB, one time)…");

        var tmp = WhisperModelPath + ".part";
        await using (var src = await http.GetStreamAsync(WhisperModelUrl, ct).ConfigureAwait(false))
        await using (var file = File.Create(tmp))
            await src.CopyToAsync(file, ct).ConfigureAwait(false);
        File.Move(tmp, WhisperModelPath, overwrite: true);

        progress?.Report("Model ready.");
    }
}
