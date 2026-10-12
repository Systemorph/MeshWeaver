using System.Collections.Immutable;

namespace MeshWeaver.Plugin.Packaging;

/// <summary>
/// The ONE declaration of the runtime a module bundle targets (#4445): the fleet image's RID.
///
/// <para><b>Why a bundle has a target at all.</b> A module bundle is pulled from the registry into
/// a portal pod, and every portal pod runs the fleet image. The container lane resolves a module's
/// closure inside that image (pulled as <c>linux/amd64</c>), and the prebuilt bundles are filed per
/// identity under <c>&lt;identity&gt;/linux-x64</c>. So a RID-specific asset for another RID (the
/// measured case: <c>System.Security.Cryptography.ProtectedData</c>'s Windows DPAPI implementation
/// at <c>runtimes/win/lib/netstandard2.0/</c>) is not something a bundle fails to carry. Nothing
/// that loads the bundle could use it. It is NAMED, as information, and never refused.</para>
///
/// <para>🚨 <b>Both derivations read THIS.</b> The SDK lane (<c>DepsClosure</c>, behind
/// <c>module-pack --deps-closure</c>) and the container lane (<c>mw-plugin-test build-project</c>)
/// split their findings on <see cref="Applies"/>. A second copy of the RID anywhere else would let
/// the two lanes disagree about which bundles are complete. Moving the fleet to another RID is a
/// change to <see cref="Rid"/> and nothing else.</para>
///
/// <para>A RID that cannot be read off an asset (no <c>rid</c> property, no <c>runtimes/&lt;rid&gt;/</c>
/// key) counts as targeted. An unknown RID never becomes an exemption, so it stays a refusal.</para>
/// </summary>
public static class ModuleBundleTarget
{
    /// <summary>The runtime identifier every module bundle targets: the fleet image's.</summary>
    public const string Rid = "linux-x64";

    /// <summary>
    /// The RIDs whose assets a host on <see cref="Rid"/> can load, most specific first: the
    /// runtime's own fallback graph for <c>linux-x64</c> (the same chain
    /// <c>MeshWeaver.NuGet.RuntimeAssetSelector</c> documents). An asset under <c>runtimes/unix/</c>
    /// is therefore targeted, and one under <c>runtimes/win/</c> or <c>runtimes/osx-arm64/</c> is
    /// not. Immutable, written once.
    ///
    /// <para>🚨 This chain decides ONE thing: whether an asset the derivation does NOT carry is a
    /// refusal or information. It never marks anything carried. For a native, "carried" is decided
    /// by layout alone (<see cref="NuGetPackageWriter.IsModuleNativeLayout"/>), independently of
    /// this chain, and the module-native loader (<c>ModuleNativeAssets</c>) probes only the running
    /// RID — so a native under <c>runtimes/linux/</c> or <c>runtimes/unix/</c> that is NOT at the
    /// probed layout is refused here (the chain only ever errs towards refusing for natives), and
    /// whether a carried one at <c>runtimes/unix/native/</c> is ever probed is a question about the
    /// layout check, not about this chain.</para>
    /// </summary>
    public static readonly ImmutableArray<string> CompatibleRids =
        [Rid, "linux", "unix-x64", "unix", "any"];

    /// <summary>
    /// Whether an asset declared for <paramref name="rid"/> is one a bundle targets. Empty or
    /// unknown-shaped RIDs answer <c>true</c>: only a RID that is stated and outside the chain is
    /// exempt.
    /// </summary>
    /// <param name="rid">The RID the asset's declaration states.</param>
    /// <returns>True when a bundle must carry the asset (or refuse).</returns>
    public static bool Applies(string? rid) =>
        string.IsNullOrWhiteSpace(rid)
        || CompatibleRids.Contains(rid.Trim(), StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The RID a <c>runtimeTargets</c> / <c>native</c> key names by position — the segment after a
    /// leading <c>runtimes/</c> — or empty when the key is not a <c>runtimes/</c> path.
    /// </summary>
    /// <param name="relativePath">The deps.json key, <c>/</c>-separated.</param>
    /// <returns>The RID, or empty.</returns>
    public static string RidOfPath(string relativePath) =>
        relativePath.Replace('\\', '/').Split('/') is ["runtimes", var rid, ..] ? rid : "";
}
