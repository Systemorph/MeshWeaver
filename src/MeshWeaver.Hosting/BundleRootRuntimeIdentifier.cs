using System.Runtime.InteropServices;
using Microsoft.Extensions.Configuration;

namespace MeshWeaver.Hosting;

/// <summary>
/// The shared artifact store holds ONE bundle store per runtime identifier —
/// <c>&lt;mount&gt;/&lt;rid&gt;/prebuilt-bundles</c> — because a compatibility key (<c>c003e001</c>) names no
/// architecture and the publisher refuses a second architecture under one key
/// (Doc/Architecture/SharedArtifactStore). A record that takes part in the store therefore renders the
/// bundle root with a <see cref="Placeholder"/>, and this expands it ONCE at boot to the running
/// process's own identifier, before anything reads <see cref="ShippedPrebuiltBundles.PublishedRootConfigKey"/>.
/// Every reader below the key stays unchanged.
/// </summary>
public static class BundleRootRuntimeIdentifier
{
    /// <summary>The placeholder a record's bundle root carries where the runtime identifier goes.</summary>
    public const string Placeholder = "{rid}";

    /// <summary>
    /// The running process's runtime identifier as the store names its per-architecture directories:
    /// <c>&lt;os&gt;-&lt;arch&gt;</c> — <c>linux-x64</c>, <c>linux-arm64</c>, <c>osx-arm64</c>, <c>win-x64</c> — from
    /// the OS and the PROCESS architecture, never from configuration (a configured value could name an
    /// architecture the process cannot load). Pure over its inputs.
    /// </summary>
    public static string Current() => Compose(
        OperatingSystem.IsLinux() ? "linux" : OperatingSystem.IsMacOS() ? "osx" : OperatingSystem.IsWindows() ? "win" : "unknown",
        RuntimeInformation.ProcessArchitecture);

    /// <summary>The identifier for an OS name and an architecture. Pure.</summary>
    public static string Compose(string os, Architecture architecture) =>
        $"{os}-{architecture switch
        {
            Architecture.X64 => "x64",
            Architecture.Arm64 => "arm64",
            Architecture.X86 => "x86",
            Architecture.Arm => "arm",
            _ => architecture.ToString().ToLowerInvariant(),
        }}";

    /// <summary>
    /// <paramref name="root"/> with every <see cref="Placeholder"/> replaced by <paramref name="rid"/>;
    /// a root without one is returned unchanged (every per-portal volume). Pure.
    /// </summary>
    public static string? Expand(string? root, string rid) =>
        root is null || !root.Contains(Placeholder, StringComparison.Ordinal)
            ? root
            : root.Replace(Placeholder, rid, StringComparison.Ordinal);

    /// <summary>
    /// Expands the placeholder in the configured bundle root IN PLACE, so every later read of the key —
    /// through this configuration or the DI-registered one built from it — sees the expanded path.
    /// Returns the expanded root, or null when none is configured. Call before the host reads the key.
    /// </summary>
    public static string? ExpandConfigured(IConfiguration configuration, string? rid = null)
    {
        var configured = configuration[ShippedPrebuiltBundles.PublishedRootConfigKey];
        var expanded = Expand(configured, rid ?? Current());
        if (expanded is not null && !string.Equals(expanded, configured, StringComparison.Ordinal))
        {
            // The template stays readable: a reader that serves OTHER runtimes (a registry answering a
            // client of another architecture) must resolve the CLIENT's store, not this process's.
            configuration[TemplateConfigKey] = configured;
            configuration[ShippedPrebuiltBundles.PublishedRootConfigKey] = expanded;
        }
        return expanded;
    }

    /// <summary>
    /// Where <see cref="ExpandConfigured"/> keeps the unexpanded root, so a reader serving another runtime
    /// can resolve that runtime's store with <see cref="Expand"/>. Absent on a root without the placeholder.
    /// </summary>
    public const string TemplateConfigKey = "PreWarm:PrebuiltBundleRootTemplate";
}
