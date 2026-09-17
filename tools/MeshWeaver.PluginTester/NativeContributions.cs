using System.Collections.Immutable;
using System.Text.Json;
using MeshWeaver.Plugin.Packaging;

namespace MeshWeaver.PluginTester;

/// <summary>
/// The LOADABLE native payloads a <c>deps.json</c> node declares, and where their bytes are on
/// disk — the container lane's half of #4126.
///
/// <para><b>Why one helper for two readers.</b> Both byte sources the container lane draws on —
/// the image's <c>/app</c> (<see cref="ContainerReferenceSet"/>) and the curated module-libraries
/// shelf (<see cref="ModuleLibrariesShelf"/>) — record their contents in a <c>deps.json</c>, and
/// both have to answer the same two questions about a native. Two copies of that reading would
/// drift, and a drift here is silent: the module builds, lands, loads, and throws
/// <c>DllNotFoundException</c> at the first P/Invoke.</para>
///
/// <para>🚨 <b>TWO deps.json shapes, and the fleet's images are the second.</b> A PORTABLE publish
/// leaves natives in <c>runtimeTargets</c> with <c>assetType: "native"</c>, laid out under
/// <c>runtimes/&lt;rid&gt;/native/</c> beside the app. A RID-SPECIFIC publish — which is what every
/// MeshWeaver image is — resolves them into a <c>native</c> section whose KEY is still
/// <c>runtimes/&lt;rid&gt;/native/&lt;file&gt;</c> while the FILE itself is flattened into the app
/// directory. Measured 2026-09-15 on a <c>SQLitePCLRaw.lib.e_sqlite3</c> publish: portable → 29
/// <c>runtimeTargets</c> entries and a <c>runtimes/</c> tree; <c>-r linux-x64</c> → one
/// <c>native</c> entry and <c>libe_sqlite3.so</c> sitting flat. Reading only one of the two shapes
/// would answer "this image has no natives" about an image that has them.</para>
///
/// <para>🚨 <b>The declared path is the CONTRACT, and it is filtered by the ONE spelling</b>
/// (<see cref="NuGetPackageWriter.IsModuleNativeLayout"/>, shared with the SDK derivation, the
/// packer, the bundle reader and the landing). A payload at anything but those exact four segments
/// is not carried: <c>ModuleNativeAssets</c> composes its probe from exactly them and has no
/// recursive walk, so carrying one anywhere else is bytes that read as shipped and behave as
/// absent. <c>.a</c> / <c>.lib</c> are link-time inputs that nothing loads and are excluded for
/// the same reason the in-image lane has excluded them since #1728.</para>
/// </summary>
internal static class NativeContributions
{
    /// <summary>
    /// The native payloads one <c>deps.json</c> target node declares, as module-relative paths
    /// (<c>runtimes/&lt;rid&gt;/native/&lt;file&gt;</c>), ordered and de-duplicated ORDINAL.
    ///
    /// <para>ORDINAL, unlike every assembly name in the same files: assembly binding is
    /// case-insensitive, a Linux filesystem is not, and <c>libFoo.so</c> / <c>libfoo.so</c> are two
    /// distinct loadable libraries — collapsing them would drop one.</para>
    /// </summary>
    /// <param name="node">One entry of the deps.json <c>targets/&lt;tfm&gt;</c> object.</param>
    /// <returns>The declared module-relative paths.</returns>
    public static ImmutableArray<string> DeclaredBy(JsonElement node)
    {
        if (node.ValueKind != JsonValueKind.Object)
            return [];

        var found = new SortedSet<string>(StringComparer.Ordinal);

        // A RID-SPECIFIC publish: the section is already resolved to one RID, so every entry is a
        // native and there is no assetType to read.
        if (node.TryGetProperty("native", out var native) && native.ValueKind == JsonValueKind.Object)
            foreach (var file in native.EnumerateObject())
                Consider(file.Name);

        // A PORTABLE publish: runtimeTargets carries every RID, and MANAGED RID-specific assets
        // (assetType "runtime") share the section — those belong to the flat closure's rules, not
        // here, so the assetType is read rather than assumed.
        if (node.TryGetProperty("runtimeTargets", out var targets)
            && targets.ValueKind == JsonValueKind.Object)
            foreach (var file in targets.EnumerateObject())
                if (file.Value.ValueKind == JsonValueKind.Object
                    && file.Value.TryGetProperty("assetType", out var assetType)
                    && string.Equals(assetType.GetString(), "native", StringComparison.OrdinalIgnoreCase))
                    Consider(file.Name);

        return [.. found];

        void Consider(string key)
        {
            var relative = key.Replace('\\', '/');
            if (relative.EndsWith(".a", StringComparison.OrdinalIgnoreCase)
                || relative.EndsWith(".lib", StringComparison.OrdinalIgnoreCase))
                return;
            if (NuGetPackageWriter.IsModuleNativeLayout(relative))
                found.Add(relative);
        }
    }

    /// <summary>
    /// The remedy the container lane can actually offer — appended to the SHARED finding wording
    /// (<see cref="UncarriedAssetFindings"/>). This lane reads no per-module pack additions, so the
    /// only step a module can take here is the sdk path, whose pack names the exact csproj lines.
    /// </summary>
    internal const string ContainerRemedy =
        " The container lane carries neither shape and does not refuse yet — a refusal here is "
        + "armed only after a measured container wave (#4445). If the module needs it, build the "
        + "module on the sdk path (\"build\": \"sdk\" on its modules: entry) and declare the carrier "
        + "in its csproj (MeshWeaverPackWith / MeshWeaverPackWithNative) — that path's module-pack "
        + "refuses until it is carried and prints the exact lines.";

    /// <summary>
    /// 🚨 What one <c>deps.json</c> node DECLARES that this lane does NOT carry — NAMED, never
    /// passed over (#4367, #4445). <see cref="DeclaredBy"/> filters to the probed layout and says
    /// nothing about the rest, and the managed walk reads only <c>runtime</c>, so until this existed
    /// the container lane dropped both shapes without a line: a native at an unprobed layout (from
    /// either deps.json shape) and a RID-specific MANAGED assembly (<c>assetType: "runtime"</c> under
    /// <c>runtimeTargets</c> — present in a PORTABLE record such as the shelf's; a RID-specific image
    /// has already resolved its RID's copy into <c>runtime</c>, and that copy rides). With no line
    /// there was nothing to measure, so a refusal could never be armed on evidence.
    ///
    /// <para>Worded with the SAME finding the SDK lane refuses on, so one grep measures both lanes,
    /// plus <see cref="ContainerRemedy"/>. Static libraries are excluded exactly as in
    /// <see cref="DeclaredBy"/>: link-time inputs nothing loads.</para>
    /// </summary>
    /// <param name="node">One entry of the deps.json <c>targets/&lt;tfm&gt;</c> object.</param>
    /// <param name="packageId">The package the node describes, for the finding.</param>
    /// <returns>The worded findings, ordered and de-duplicated ORDINAL.</returns>
    public static ImmutableArray<string> UncarriedBy(JsonElement node, string packageId)
    {
        if (node.ValueKind != JsonValueKind.Object)
            return [];
        var found = new SortedSet<string>(StringComparer.Ordinal);

        if (node.TryGetProperty("native", out var native) && native.ValueKind == JsonValueKind.Object)
            foreach (var file in native.EnumerateObject())
                ConsiderNative(file.Name);

        if (node.TryGetProperty("runtimeTargets", out var targets)
            && targets.ValueKind == JsonValueKind.Object)
            foreach (var file in targets.EnumerateObject())
            {
                if (file.Value.ValueKind != JsonValueKind.Object
                    || !file.Value.TryGetProperty("assetType", out var assetType))
                    continue;
                if (string.Equals(assetType.GetString(), "native", StringComparison.OrdinalIgnoreCase))
                    ConsiderNative(file.Name);
                else if (string.Equals(assetType.GetString(), "runtime", StringComparison.OrdinalIgnoreCase))
                    found.Add(UncarriedAssetFindings.RidSpecificManaged(
                        packageId, file.Name.Replace('\\', '/')) + ContainerRemedy);
            }

        return [.. found];

        void ConsiderNative(string key)
        {
            var relative = key.Replace('\\', '/');
            if (NuGetPackageWriter.IsModuleNativeLayout(relative)
                || relative.EndsWith(".a", StringComparison.OrdinalIgnoreCase)
                || relative.EndsWith(".lib", StringComparison.OrdinalIgnoreCase))
                return;
            found.Add(UncarriedAssetFindings.UnprobedNative(packageId, relative) + ContainerRemedy);
        }
    }

    /// <summary>
    /// The file backing one declared native path inside <paramref name="directory"/>, or null when
    /// neither layout has it.
    ///
    /// <para>The preserved layout is tried FIRST (a portable publish keeps
    /// <c>runtimes/&lt;rid&gt;/native/&lt;file&gt;</c> on disk), then the flat basename (a
    /// RID-specific publish resolved the asset and dropped the file beside the app). Both are the
    /// directory's OWN deps.json talking about the directory's own contents, so this is a lookup
    /// and never a guess.</para>
    /// </summary>
    /// <param name="directory">The app or shelf directory the deps.json describes.</param>
    /// <param name="relativePath">The declared module-relative path.</param>
    /// <returns>The absolute file path, or null.</returns>
    public static string? FileFor(string directory, string relativePath)
    {
        if (string.IsNullOrEmpty(directory) || !NuGetPackageWriter.IsModuleNativeLayout(relativePath))
            return null;
        var preserved = Path.Combine(
            directory, relativePath.Replace('/', Path.DirectorySeparatorChar));
        if (File.Exists(preserved))
            return preserved;
        var flat = Path.Combine(directory, relativePath.Split('/')[^1]);
        return File.Exists(flat) ? flat : null;
    }
}
