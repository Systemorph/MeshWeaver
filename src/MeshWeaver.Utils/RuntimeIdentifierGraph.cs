using System.Collections.Immutable;
using System.Runtime.InteropServices;

namespace MeshWeaver.Utils;

/// <summary>
/// The .NET runtime-identifier (RID) fallback chain: which <c>runtimes/&lt;rid&gt;/</c> folders an asset
/// built for a more general RID may come from on the running one. ONE definition, read by every place
/// that asks "is a RID-specific asset usable here?": the NuGet runtime-asset selector
/// (<c>MeshWeaver.NuGet.RuntimeAssetSelector</c>) and the module native-asset resolver
/// (<c>MeshWeaver.Mesh.ModuleNativeAssets</c>).
///
/// <para><b>Why derived here and not read from the runtime.</b> Since .NET 8 the runtime ships no RID
/// graph it can be asked about: <c>runtime.json</c> is a restore-time NuGet artifact, a portable app's
/// <c>deps.json</c> carries no <c>runtimes</c> fallback section, and <see cref="RuntimeInformation"/>
/// reports only the running RID. So the chain is derived from the running RID by the rules of NuGet's
/// portable RID graph for the families a portal runs on.</para>
/// </summary>
public static class RuntimeIdentifierGraph
{
    private static bool IsArchitecture(string segment) => segment.ToLowerInvariant() switch
    {
        "x64" or "x86" or "arm64" or "arm" or "armel" or "armv6" or "s390x" or "ppc64le"
            or "loongarch64" or "riscv64" or "wasm" or "mips64" => true,
        _ => false,
    };

    /// <summary>
    /// The RID fallback chain for <paramref name="rid"/>, nearest first, ending in <c>any</c> — an
    /// image of NuGet's portable runtime graph for the families a portal runs on:
    /// <c>linux-x64 → linux → unix-x64 → unix → any</c>,
    /// <c>linux-musl-x64 → linux-musl → linux-x64 → linux → unix-x64 → unix → any</c>,
    /// <c>osx-arm64 → osx → unix-arm64 → unix → any</c>, <c>win-x64 → win → any</c>.
    /// A versioned RID keeps its own rungs first and then falls back to its portable family
    /// (<c>osx.14-arm64 → osx.14 → osx-arm64 → osx → …</c>); a distro RID (<c>ubuntu.22.04-x64</c>)
    /// falls back to <c>linux</c>.
    /// </summary>
    /// <param name="rid">A runtime identifier; <see cref="RuntimeInformation.RuntimeIdentifier"/> when null or blank.</param>
    /// <returns>The chain, nearest first.</returns>
    public static ImmutableArray<string> FallbackChain(string? rid = null)
    {
        rid = string.IsNullOrWhiteSpace(rid) ? RuntimeInformation.RuntimeIdentifier : rid.Trim();
        var chain = ImmutableArray.CreateBuilder<string>();
        void Add(string r)
        {
            if (!chain.Contains(r, StringComparer.OrdinalIgnoreCase)) chain.Add(r);
        }

        var dash = rid.LastIndexOf('-');
        var arch = dash > 0 && IsArchitecture(rid[(dash + 1)..])
            ? rid[(dash + 1)..]
            : null;
        var os = arch is null ? rid : rid[..dash];

        // The OS family, nearest first.
        var family = ImmutableArray.CreateBuilder<string>();
        family.Add(os);
        bool isWindows = os.StartsWith("win", StringComparison.OrdinalIgnoreCase);
        if (os.Equals("linux-musl", StringComparison.OrdinalIgnoreCase)
            || os.StartsWith("alpine", StringComparison.OrdinalIgnoreCase))
        {
            family.Add("linux-musl");
            family.Add("linux");
        }
        else if (os.StartsWith("osx", StringComparison.OrdinalIgnoreCase))
            family.Add("osx");
        else if (isWindows)
            family.Add("win");
        else if (!os.Equals("unix", StringComparison.OrdinalIgnoreCase)
                 && !os.Equals("any", StringComparison.OrdinalIgnoreCase)
                 && !os.StartsWith("freebsd", StringComparison.OrdinalIgnoreCase)
                 && !os.StartsWith("android", StringComparison.OrdinalIgnoreCase)
                 && !os.StartsWith("ios", StringComparison.OrdinalIgnoreCase)
                 && !os.StartsWith("maccatalyst", StringComparison.OrdinalIgnoreCase)
                 && !os.StartsWith("browser", StringComparison.OrdinalIgnoreCase))
            family.Add("linux"); // linux itself, or a distro RID (ubuntu.22.04, rhel.9, …)

        foreach (var f in family)
        {
            if (f.Equals("any", StringComparison.OrdinalIgnoreCase)) continue;
            if (arch is not null) Add($"{f}-{arch}");
            Add(f);
        }
        if (!isWindows && !os.StartsWith("browser", StringComparison.OrdinalIgnoreCase))
        {
            if (arch is not null) Add($"unix-{arch}");
            Add("unix");
        }
        Add("any");
        return chain.ToImmutable();
    }

    /// <summary>
    /// Whether <paramref name="rid"/> names a musl-libc Linux (<c>linux-musl-*</c>, <c>alpine*</c>).
    /// </summary>
    /// <param name="rid">A runtime identifier.</param>
    /// <returns><c>true</c> for a musl RID.</returns>
    public static bool IsMusl(string? rid)
        => rid is not null
           && (rid.StartsWith("linux-musl", StringComparison.OrdinalIgnoreCase)
               || rid.StartsWith("alpine", StringComparison.OrdinalIgnoreCase));
}
