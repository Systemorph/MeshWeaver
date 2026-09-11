using System.Collections.Immutable;
using System.IO;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.PluginCatalog;

/// <summary>
/// WHICH BYTES a registry can serve a consumer whose framework build identity is not its own —
/// the NodeType counterpart of <see cref="ServedModuleBytes"/> (#3244), which answered the same
/// question for a package's compiled MODULE.
///
/// <para>🚨 <b>The asymmetry this closes.</b> Since #1751 the bundle route resolves an off-lane
/// caller's assemblies through each NodeType's <c>Release</c> node, which records per
/// (identity, architecture) which assembly-store version holds bytes proven built for that lane.
/// That is the right rule and it stays; what it cannot do is FIND bytes. A
/// <c>ReleaseArtifact</c> is minted in exactly one place — a compile, in this mesh, stamping the
/// COMPILING process's own identity (<c>NodeTypeBuildState.TryCreateReleaseNode</c>) — and an
/// in-portal compile writes its assembly to the pod's local cache
/// (<c>collection: "local"</c>, which <c>FileSystemAssemblyStore</c> documents as "the bytes live
/// in the local filesystem cache only; cross-silo readers must recompile"). So the only artifacts
/// that exist for a lane the registry is NOT running were written by a pod that has since been
/// replaced, and they point at bytes no later pod can open. Adoption is never unsafe, but it is
/// structurally empty.</para>
///
/// <para><b>The bytes that ARE durable are the sealed publication the registry already mounts</b> —
/// <c>&lt;root&gt;/&lt;identity&gt;/&lt;source&gt;/&lt;package&gt;.zip</c>, the exact artifact the
/// consumer's own boot seeder reads off the share and the exact artifact this registry already
/// serves at <c>…/prebuilt/{identity}/{source}/{bundle}</c>. This class is the lookup that lets the
/// PACKAGE route reach them, so the decision stays server-side under the per-package grant
/// (<c>ServedModuleBytes</c> records why that matters: fetching from the prebuilt route directly
/// needs a whole-source grant, and a whole-source grant deliberately bypasses plan tiering).</para>
///
/// <para><b>Nothing here decides adoption.</b> A sealed bundle states its own producer's
/// <c>frameworkMvid</c> in its manifest, and the consumer re-checks it with the unchanged
/// <c>PrebuiltAssemblySeeder.DeclineReason</c> — first for the archive as a whole, then per
/// assembly as it seeds. This only decides WHICH bytes are offered; ordinal identity equality
/// still decides whether they land.</para>
/// </summary>
public static class SealedLaneBundles
{
    /// <summary>
    /// The package bundle ids this root has SEALED for one framework identity — i.e. the lane a
    /// consumer running that identity can actually be served from here. Extension stripped,
    /// case-insensitive.
    ///
    /// <para>Empty means the same thing everywhere it is read: this root holds no COMPLETE
    /// publication for that identity — no such identity directory, a publication being replaced
    /// right now (its seal removed), or one torn beyond its seal. All three are "cannot serve this
    /// lane", which is the answer the index must give, and none of them is an error.</para>
    /// </summary>
    public static ImmutableHashSet<string> ServableFor(
        string? publishedRoot, string? identity, ILogger? logger = null) =>
        PublishedBundleCatalogue.SealedBundlesForIdentity(publishedRoot, identity, logger);

    /// <summary>
    /// The sealed bundle carrying <paramref name="packageId"/>'s NodeType assemblies under
    /// <paramref name="identity"/>, or null when no complete publication for that identity lists
    /// one.
    ///
    /// <para>🚨 Resolved through <see cref="PublishedBundleCatalogue.SealedPublicationOf"/>, never
    /// by composing a path and testing it: that reader applies THE completeness rule (sentinel
    /// present AND every listed bundle on disk) and resolves the generation pointer (#3461), so a
    /// consumer can never be handed a bundle out of a publication the boot seeder itself would
    /// refuse. A source whose publication is torn contributes nothing and the next source is
    /// tried — one source being replaced must never hide another's sealed bundles.</para>
    /// </summary>
    public static SealedLaneBundle? Locate(
        string? publishedRoot, string? identity, string packageId, ILogger? logger = null)
    {
        if (string.IsNullOrWhiteSpace(publishedRoot)
            || string.IsNullOrWhiteSpace(identity)
            || string.IsNullOrWhiteSpace(packageId))
            return null;

        var identityDirectory = Path.Combine(publishedRoot, identity);
        if (!Directory.Exists(identityDirectory))
            return null;

        foreach (var sourceDirectory in Directory
                     .EnumerateDirectories(identityDirectory)
                     .OrderBy(d => d, StringComparer.Ordinal))
        {
            var reading = PublishedBundleCatalogue.SealedPublicationOf(sourceDirectory, logger);
            if (reading.Bundles is not { Count: > 0 })
                continue;

            var listed = reading.Bundles.FirstOrDefault(
                name => string.Equals(BaseNameOf(name), packageId, StringComparison.OrdinalIgnoreCase));
            if (listed is null)
                continue;

            return new SealedLaneBundle(
                identity!,
                Path.GetFileName(sourceDirectory)!,
                listed,
                Path.Combine(reading.Directory ?? sourceDirectory, listed),
                reading.Generation);
        }

        return null;
    }

    /// <summary>A sealed bundle's id as the seal lists it, minus the <c>.zip</c> the bake writes —
    /// the package id (<c>RequiredPackage.BundleName</c>).</summary>
    private static string BaseNameOf(string bundleFileName) =>
        bundleFileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)
            ? bundleFileName[..^4]
            : bundleFileName;
}

/// <summary>One package's bundle inside a sealed publication.</summary>
/// <param name="Identity">The framework build identity the publication was sealed for — the lane
/// these bytes are provably for, and what the bundle's own manifest states.</param>
/// <param name="Source">The producing repo's publication prefix (<c>plugins</c>, <c>crm</c>, …).</param>
/// <param name="BundleName">The file name the seal lists.</param>
/// <param name="Path">The resolved full path, composed under the publication's own directory so a
/// generation-pointer layout reads the generation's bytes rather than the flat layout's.</param>
/// <param name="Generation">The publication INSTANCE these bytes came out of, or null when the
/// reading could not derive one.</param>
public readonly record struct SealedLaneBundle(
    string Identity, string Source, string BundleName, string Path, string? Generation);
