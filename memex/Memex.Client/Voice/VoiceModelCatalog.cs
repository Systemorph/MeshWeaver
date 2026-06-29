using System.IO.Compression;
using Microsoft.Maui.Storage;

namespace Memex.Client.Voice;

/// <summary>Which on-device Whisper GGML model to use — a size/quality vs. footprint trade-off.</summary>
public enum WhisperModelSize
{
    /// <summary>~140 MB. Fast and small; OK for clear German/English. Poor on strong Swiss dialect.</summary>
    Base,

    /// <summary>~1.5 GB. Much better, incl. Swiss German (still imperfect on Bernese). Heavier download +
    /// slower, but runs with Metal/GPU on iPhone. The realistic floor for proper dialect.</summary>
    LargeV3Turbo,

    /// <summary>
    /// ~1.6 GB. The <b>Flurin17/whisper-large-v3-turbo-swiss-german</b> fine-tune converted to GGML —
    /// the best on-device Swiss German (trained partly on Bernese parliamentary speech; outputs
    /// Standard German). No public GGML exists, so this file is converted (whisper.cpp
    /// convert-h5-to-ggml.py) and PLACED in <see cref="ModelsDirectory"/> — there is no download URL.
    /// </summary>
    SwissGerman,
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
    private readonly string? _url;   // null = no public source; the file must be placed locally

    private readonly bool _provisionAppleImage;
    private readonly string? _appleImageUrl;   // CoreML encoder ("apple image") .zip; null = none hosted

    public VoiceModelCatalog(string modelsDirectory, WhisperModelSize model = WhisperModelSize.Base,
        bool provisionAppleImage = false)
    {
        ModelsDirectory = modelsDirectory;
        Directory.CreateDirectory(modelsDirectory);
        _provisionAppleImage = provisionAppleImage;
        (_fileName, _url) = model switch
        {
            // The converted Flurin17 Swiss-German fine-tune (no public GGML), quantized to q5_0 (~547 MB
            // vs 1.5 GB f16 — faster + iPhone-RAM-friendly, negligible quality loss). Served from the
            // MeshWeaver space's "static" FileSystem content collection (the AKS file-share mount),
            // downloaded on first use like the others.
            WhisperModelSize.SwissGerman => ("ggml-swiss-german-turbo-q5_0.bin",
                "https://memex.meshweaver.cloud/MeshWeaver/static/Speech/ggml-swiss-german-turbo-q5_0.bin"),
            WhisperModelSize.LargeV3Turbo => ("ggml-large-v3-turbo.bin", GgmlBaseUrl + "ggml-large-v3-turbo.bin"),
            _ => ("ggml-base.bin", GgmlBaseUrl + "ggml-base.bin"),
        };
        // The CoreML "apple image" (encoder) is hosted next to the ggml model as a .zip when one exists
        // (our static collection for Swiss German; ggerganov's HF repo for base/large). Same directory,
        // filename derived by whisper.cpp's own rule so the runtime auto-loads it.
        _appleImageUrl = _url is null ? null : DeriveAppleImageZipUrl(_url);
    }

    public string WhisperModelPath => Path.Combine(ModelsDirectory, _fileName);

    public bool Present => File.Exists(WhisperModelPath);

    /// <summary>
    /// The CoreML encoder directory whisper.cpp auto-loads next to the ggml model. The name is whisper.cpp's
    /// own: drop the extension, drop a trailing "-qX_Y" quantization tag, append "-encoder.mlmodelc"
    /// (so <c>ggml-swiss-german-turbo-q5_0.bin</c> → <c>ggml-swiss-german-turbo-encoder.mlmodelc</c>).
    /// </summary>
    public string AppleImagePath => Path.Combine(ModelsDirectory, AppleImageDirName(_fileName));

    /// <summary>True once the CoreML apple image is unpacked next to the model (GPU/ANE encoder active).</summary>
    public bool AppleImagePresent => Directory.Exists(AppleImagePath);

    public async Task EnsureAsync(IProgress<string>? progress = null, CancellationToken ct = default)
    {
        await EnsureGgmlAsync(progress, ct).ConfigureAwait(false);
        if (_provisionAppleImage)
            await EnsureAppleImageAsync(progress, ct).ConfigureAwait(false);
    }

    private async Task EnsureGgmlAsync(IProgress<string>? progress, CancellationToken ct)
    {
        if (File.Exists(WhisperModelPath)) return;

        // Prefer a copy BUNDLED in the app package (a packaged model) over any download.
        if (await TryCopyFromPackageAsync(_fileName, WhisperModelPath, ct).ConfigureAwait(false))
        {
            progress?.Report("Model ready (bundled).");
            return;
        }

        if (_url is null)
            throw new InvalidOperationException(
                $"The model '{_fileName}' has no public GGML and is not present. Convert " +
                $"Flurin17/whisper-large-v3-turbo-swiss-german (whisper.cpp convert-h5-to-ggml.py) and " +
                $"place '{_fileName}' in {ModelsDirectory}.");

        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
        progress?.Report($"Downloading {_fileName} (one time)…");

        var tmp = WhisperModelPath + ".part";
        await using (var src = await http.GetStreamAsync(_url, ct).ConfigureAwait(false))
        await using (var file = File.Create(tmp))
            await src.CopyToAsync(file, ct).ConfigureAwait(false);
        File.Move(tmp, WhisperModelPath, overwrite: true);

        progress?.Report("Model ready.");
    }

    /// <summary>
    /// Best-effort: fetch + unpack the CoreML "apple image" (<c>…-encoder.mlmodelc</c>) next to the ggml
    /// model so whisper.cpp's CoreML runtime runs the encoder on the Apple GPU / Neural Engine. NEVER
    /// fatal — the CoreML runtime is built <c>WHISPER_COREML_ALLOW_FALLBACK=ON</c>, so a missing or failed
    /// image just means CPU. The outcome is surfaced through <paramref name="progress"/>, never hidden.
    /// </summary>
    private async Task EnsureAppleImageAsync(IProgress<string>? progress, CancellationToken ct)
    {
        if (Directory.Exists(AppleImagePath)) return;

        var zipName = Path.GetFileName(AppleImagePath) + ".zip";   // …-encoder.mlmodelc.zip
        var zipTmp = AppleImagePath + ".zip.part";
        var extractTmp = AppleImagePath + ".extract";
        try
        {
            // Prefer the BUNDLED apple image (packaged) over a download; else fetch it; else nothing to do.
            var bundled = await TryCopyFromPackageAsync(zipName, zipTmp, ct).ConfigureAwait(false);
            if (!bundled)
            {
                if (_appleImageUrl is null) return;
                progress?.Report("Fetching Apple GPU model (one time)…");
                using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
                await using var src = await http.GetStreamAsync(_appleImageUrl, ct).ConfigureAwait(false);
                await using var file = File.Create(zipTmp);
                await src.CopyToAsync(file, ct).ConfigureAwait(false);
            }

            if (Directory.Exists(extractTmp)) Directory.Delete(extractTmp, recursive: true);
            ZipFile.ExtractToDirectory(zipTmp, extractTmp);

            // The zip root either IS the .mlmodelc directory, or contains it by name. Move it into place.
            var nested = Path.Combine(extractTmp, Path.GetFileName(AppleImagePath));
            Directory.Move(Directory.Exists(nested) ? nested : extractTmp, AppleImagePath);
            progress?.Report(bundled ? "Apple GPU model ready (bundled)." : "Apple GPU model ready.");
        }
        catch (Exception ex)
        {
            // Non-fatal by design: CoreML falls back to CPU. Surface it — do not silently swallow.
            progress?.Report($"Apple GPU model unavailable ({ex.Message}); using CPU.");
        }
        finally
        {
            if (File.Exists(zipTmp)) File.Delete(zipTmp);
            if (Directory.Exists(extractTmp)) Directory.Delete(extractTmp, recursive: true);
        }
    }

    /// <summary>
    /// Copies a file BUNDLED in the app package (a <c>MauiAsset</c>, by its logical name) to
    /// <paramref name="destPath"/>. Returns false when the file isn't bundled on this platform/build
    /// (the catalog then downloads it instead). The whole on-device-model packaging hinges on this.
    /// </summary>
    private static async Task<bool> TryCopyFromPackageAsync(string packageFile, string destPath, CancellationToken ct)
    {
        try
        {
            await using var src = await FileSystem.OpenAppPackageFileAsync(packageFile).ConfigureAwait(false);
            var tmp = destPath + ".pkg.part";
            await using (var file = File.Create(tmp))
                await src.CopyToAsync(file, ct).ConfigureAwait(false);
            File.Move(tmp, destPath, overwrite: true);
            return true;
        }
        catch
        {
            return false;   // not bundled (FileNotFound) — caller falls back to download
        }
    }

    /// <summary>whisper.cpp's <c>whisper_get_coreml_path_encoder</c> rule, mirrored exactly.</summary>
    private static string AppleImageDirName(string ggmlFileName)
    {
        var name = Path.GetFileNameWithoutExtension(ggmlFileName);            // drop ".bin"
        var dash = name.LastIndexOf('-');
        if (dash >= 0 && name.Length - dash == 5 && name[dash + 1] == 'q' && name[dash + 3] == '_')
            name = name[..dash];                                             // drop a "-qX_Y" tag
        return name + "-encoder.mlmodelc";
    }

    private static string DeriveAppleImageZipUrl(string ggmlUrl)
    {
        var slash = ggmlUrl.LastIndexOf('/');
        return ggmlUrl[..(slash + 1)] + AppleImageDirName(ggmlUrl[(slash + 1)..]) + ".zip";
    }
}
