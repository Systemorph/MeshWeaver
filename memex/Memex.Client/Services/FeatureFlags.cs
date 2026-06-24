using Microsoft.Maui.Storage;

namespace Memex.Client.Services;

/// <summary>
/// Lightweight, device-persisted feature flags backed by MAUI <see cref="Preferences"/> — the same store
/// the rest of the client uses for small persisted toggles (see <see cref="InstanceStore"/>). Defaults are
/// declared here; a flag can be flipped at runtime (a settings/debug screen) and survives restarts with no
/// rebuild. Deliberately tiny: the MAUI client has no <c>appsettings</c>/<c>IConfiguration</c> host, and
/// <c>Microsoft.FeatureManagement</c> would drag in configuration + hosting we don't run on-device.
/// </summary>
public sealed class FeatureFlags
{
    private const string Prefix = "feature.";

    /// <summary>
    /// Apple GPU / Neural-Engine inference for on-device Whisper via CoreML (<b>MacCatalyst only</b> — the
    /// macOS client). Whisper.net builds whisper.cpp with <c>GGML_METAL=OFF</c> on MacCatalyst, so unlike
    /// the iOS device build (Metal) the macOS client has <b>no Metal</b> — CoreML is the only GPU path.
    /// It engages when the CoreML "apple image" (<c>…-encoder.mlmodelc</c>) sits next to the ggml model;
    /// <see cref="VoiceModelCatalog"/> provisions it and whisper falls back to CPU when it's absent.
    /// <para><b>Default on for MacCatalyst</b> (the macOS client) so Whisper runs on the GPU out of the box —
    /// the default Base model's CoreML encoder is publicly hosted (ggerganov), so no hosting is needed; a
    /// missing encoder degrades cleanly to CPU. Off elsewhere (irrelevant). Still per-device overridable.
    /// See <c>Doc/Architecture/OnDeviceVoice</c> → "GPU on macOS — the CoreML apple image".</para>
    /// </summary>
    public const string AppleGpu = "apple-gpu";

    /// <summary>
    /// Use the large Swiss-German fine-tune for on-device voice (547 MB ggml, + ~1.2 GB CoreML apple image
    /// on macOS) instead of the ~140 MB stock <c>Base</c> Whisper model. <b>Default off</b>: the client uses
    /// the small Base model and only downloads the much larger Swiss-German bundle when this is turned on.
    /// It's the marquee feature, but it's a big on-demand download — see <c>Doc/Architecture/OnDeviceVoice</c>.
    /// </summary>
    public const string SwissGerman = "swiss-german";

    /// <summary>Compile-time default for a flag when the device has no stored value yet.</summary>
    private static bool DefaultOf(string key) => key switch
    {
        // On the macOS client, run Whisper on the GPU (CoreML) by default — the Base model's encoder is
        // publicly hosted, so it works out of the box; missing/failed → clean CPU fallback.
        AppleGpu => OperatingSystem.IsMacCatalyst(),
        SwissGerman => false,
        _ => false,
    };

    /// <summary>Reads a flag (device value if set, else its <see cref="DefaultOf"/> default).</summary>
    public bool IsEnabled(string key) => Preferences.Default.Get(Prefix + key, DefaultOf(key));

    /// <summary>Flips a flag and persists it on the device.</summary>
    public void Set(string key, bool value) => Preferences.Default.Set(Prefix + key, value);

    /// <summary>Clears a device override so the flag reverts to its compile-time default.</summary>
    public void Reset(string key) => Preferences.Default.Remove(Prefix + key);

    /// <summary>
    /// Static read for startup code that runs before the DI container exists — the Whisper
    /// <c>RuntimeLibraryOrder</c> must be chosen before the first <c>WhisperFactory</c> is created.
    /// </summary>
    public static bool IsAppleGpuEnabled => Preferences.Default.Get(Prefix + AppleGpu, DefaultOf(AppleGpu));

    /// <summary>Static read of <see cref="SwissGerman"/> for startup model selection (before DI exists).</summary>
    public static bool IsSwissGermanEnabled => Preferences.Default.Get(Prefix + SwissGerman, DefaultOf(SwissGerman));
}
