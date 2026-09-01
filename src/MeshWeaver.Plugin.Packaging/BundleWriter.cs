using System.IO.Compression;
using System.Text;
using System.Text.Json;

namespace MeshWeaver.Plugin.Packaging;

/// <summary>
/// Writes a PREBUILT-ASSEMBLY BUNDLE — the zip <see cref="BundleReader"/> reads: the
/// <see cref="NuGetPackageWriter.ManifestEntry"/> manifest binding node paths to assemblies, plus
/// each compiled assembly (and its symbols) under <see cref="NuGetPackageWriter.AssemblyFolder"/>.
///
/// <para>Lives beside <see cref="BundleReader"/> for the same reason the reader lives beside
/// <see cref="NuGetPackageWriter"/>: the entry names and the manifest shape are stated ONCE, so a
/// producer and a consumer cannot drift. Producers: the CI bake (<c>mw-plugin-test
/// --bake-output</c>, which persists what the content gate just compiled) and, later, the registry
/// bundle lane. Consumers: <c>PluginBundleClient</c> (HTTP) and the boot-time shipped-bundle
/// seeder — all of them go through <see cref="BundleReader.Read(byte[])"/>.</para>
///
/// <para>A bundle is deliberately NOT a nupkg: no nuspec, no dependency graph — compiled bytes, the
/// identity they are pinned to, and (optionally) the package's own NODE DEFINITIONS. The framework
/// MVID in the manifest is what <c>PrebuiltAssemblySeeder.DeclineReason</c> gates adoption on, so
/// writing it is mandatory: a bundle without it is dead weight every consumer refuses (correctly).</para>
///
/// <para>🚨 The node definitions are what make a bundle a REPLACEMENT for a source checkout rather
/// than a speed-up on top of one. Until it carried them, a dependent repo could get an upstream's
/// bytes but not the nodes those bytes stamp, so it still cloned the upstream and recompiled it —
/// the rebuild that <c>Doc/Architecture/ReleaseGates</c> forbids, and the one that produces an ALC
/// per recompile. Carrying them is optional at the format level (a NodeType-only bundle stays
/// valid) and required for a bundle meant to be CONSUMED as an upstream.</para>
/// </summary>
public static class BundleWriter
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    /// <summary>One compiled NodeType destined for the bundle.</summary>
    /// <param name="NodePath">Mesh path of the NodeType these bytes implement — the key a consumer
    /// re-seeds under (<see cref="BundleReader.AssemblyRef.NodePath"/>).</param>
    /// <param name="OpenAssembly">Opens the assembly bytes. A factory rather than a byte[] so a
    /// large assembly streams into the archive instead of being held in memory.</param>
    /// <param name="OpenPdb">Opens the symbols, or null when the assembly embeds them / has none.</param>
    /// <param name="SourceVersions">The source snapshot the compile consumed
    /// (<c>NodeTypeDefinition.CompiledSources</c>: Code-node path → version ticks) — provenance for
    /// auditing which bytes were built from which sources. Ignored by <see cref="BundleReader"/>
    /// (unknown JSON properties are skipped), so carrying it costs consumers nothing.</param>
    /// <param name="Dependencies">The per-type DEPENDENCY RECORD the compile stamped
    /// (<c>NodeTypeDefinition.CompiledDependencies</c>, #1707 slice 2): referenced assembly name →
    /// surface-id. The consumer validates it against ITS environment before adopting and stamps it
    /// on adopt. Null for a legacy producer.</param>
    public sealed record AssemblyEntry(
        string NodePath,
        Func<Stream> OpenAssembly,
        Func<Stream>? OpenPdb = null,
        IReadOnlyDictionary<string, long>? SourceVersions = null,
        IReadOnlyDictionary<string, string>? Dependencies = null)
    {
        /// <summary>
        /// 🚨 <b>The CONTENT fingerprint of the sources these bytes were compiled from</b> (#2813)
        /// — <c>NodeTypeSourceFingerprint.Compute</c> over the type's resolved source+test set.
        /// The consumer stamps it as <c>NodeTypeDefinition.AdoptedSourceFingerprint</c> and refuses
        /// the adoption when its OWN live sources hash to something else: the whole point is that a
        /// portal can prove the assembly it is about to run was built from the source it is
        /// holding, which every other signal (<c>compiledSources</c> vs
        /// <c>currentSourceVersions</c>, <c>compilationStatus: Ok</c>) reported healthy while being
        /// false.
        ///
        /// <para>Null only for a producer that records none. Such a bundle still adopts — as
        /// <c>BuildProvenance.AdoptedUnverified</c>, never silently as verified — because refusing
        /// on an absence would park every legacy-bundle type at once.</para>
        ///
        /// <para>An INIT property rather than a sixth primary-constructor parameter: adding one to
        /// a public record REPLACES the constructor signature, which is a binary break for every
        /// module compiled against the old arity (<c>scripts/check-record-signatures.py</c> is
        /// right to refuse it) and would take down a mixed fleet on the roll.</para>
        /// </summary>
        public string? SourceFingerprint { get; init; }
    }

    /// <summary>
    /// One NODE-DEFINITION file destined for the bundle — the package's own tree (its
    /// <c>index.json</c>, its NodeType nodes, its <c>Source/*.cs</c>, its markdown), shipped
    /// verbatim under <see cref="NuGetPackageWriter.ContentFolder"/>.
    ///
    /// <para>🚨 Why a bundle of assemblies carries source-shaped files at all. A consumer of an
    /// upstream needs the upstream's DEFINITIONS, not only its bytes: the consumer's package roots
    /// are typed by an upstream type (<c>nodeType: Store/Plugin</c>) and its NodeTypes' `sources`
    /// queries reach into upstream packages (<c>shared=@Edu/…</c>). Seeding an assembly only
    /// STAMPS a node that already exists — with no upstream nodes in the tree there is nothing to
    /// stamp and the roots do not bind. So a bundle that carries assemblies alone cannot replace a
    /// source checkout, which is why every dependent still rebuilt its upstreams from source.</para>
    /// </summary>
    /// <param name="RelativePath">Path within the package tree, forward-slashed
    /// (<c>index.json</c>, <c>Slide/Source/SlideContent.cs</c>) — the layout a consumer recreates.</param>
    /// <param name="Open">Opens the file's bytes. A factory, for the same streaming reason as
    /// <see cref="AssemblyEntry.OpenAssembly"/>.</param>
    public sealed record ContentEntry(string RelativePath, Func<Stream> Open);

    /// <summary>
    /// Writes the bundle into <paramref name="destination"/> (left open, mirroring
    /// <see cref="NuGetPackageWriter.Write"/> — a caller may rewind and serve it).
    /// </summary>
    /// <param name="destination">Target stream.</param>
    /// <param name="plugin">Plugin / package id the bundle is for (<see cref="BundleReader.Manifest.Plugin"/>).</param>
    /// <param name="version">The package's released version, or null when the producer has none
    /// (e.g. a repo-tree bake keyed by commit rather than by release).</param>
    /// <param name="frameworkMvid">MVID of the MeshWeaver.Graph assembly that compiled these bytes
    /// (<c>PrebuiltAssemblySeeder.LiveFrameworkMvid</c> on the producing process). Required —
    /// see the class doc for why a bundle without it is unusable by construction.</param>
    /// <param name="assemblies">One entry per compiled NodeType.</param>
    /// <param name="sourceSha">The commit the content was synced from, recorded in the manifest for
    /// provenance (which tree produced these bytes). Ignored by <see cref="BundleReader"/>.</param>
    public static void Write(
        Stream destination,
        string? plugin,
        string? version,
        string frameworkMvid,
        IReadOnlyList<AssemblyEntry> assemblies,
        string? sourceSha = null,
        IReadOnlyList<ContentEntry>? content = null,
        bool? sourceIncluded = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(frameworkMvid);
        ArgumentNullException.ThrowIfNull(assemblies);

        using var archive = new ZipArchive(destination, ZipArchiveMode.Create, leaveOpen: true);

        // The manifest names each assembly RELATIVE to the assembly folder — the exact value
        // BundleReader joins back (`{AssemblyFolder}/{reference.Assembly}`), and the node path
        // VERBATIM per NuGetPackageWriter.EntryPathFor's contract (never slash-sanitized: the
        // mapping must stay injective, and zip entry names take slashes natively).
        var manifest = new
        {
            plugin,
            version,
            frameworkMvid,
            sourceSha,
            assemblies = assemblies
                .Select(a => new
                {
                    nodePath = a.NodePath,
                    assembly = $"{a.NodePath}.dll",
                    sourceVersions = a.SourceVersions,
                    dependencies = a.Dependencies,
                    // #2813. Unlike sourceVersions (provenance a reader skips), this one is READ
                    // by BundleReader and carried to PrebuiltAssemblySeeder — it is the value the
                    // consumer's refusal is decided on.
                    sourceFingerprint = a.SourceFingerprint,
                })
                .ToList(),
            // DECLARED, never left to be discovered by enumerating the folder — the same rule the
            // assemblies follow (see BundleReader.Read). A reader that globbed the archive could
            // not tell a package file from a stray entry a future producer adds, and would silently
            // recreate it into the consumer's tree.
            content = content?.Select(c => c.RelativePath).ToList(),
            sourceIncluded,
        };

        WriteText(archive, NuGetPackageWriter.ManifestEntry, JsonSerializer.Serialize(manifest, Json));

        foreach (var entry in assemblies)
        {
            using (var source = entry.OpenAssembly())
            using (var target = archive.CreateEntry(NuGetPackageWriter.EntryPathFor(entry.NodePath)).Open())
                source.CopyTo(target);

            if (entry.OpenPdb is null)
                continue;
            using (var source = entry.OpenPdb())
            using (var target = archive.CreateEntry(NuGetPackageWriter.EntryPathFor(entry.NodePath, ".pdb")).Open())
                source.CopyTo(target);
        }

        foreach (var file in content ?? [])
        {
            // Defence at the producing end too: a bundle should never LEAVE here carrying a path a
            // consumer must refuse. Cheap, and it turns a packaging mistake into an error next to
            // the code that made it rather than a refusal on someone else's build agent.
            if (string.IsNullOrWhiteSpace(file.RelativePath)
                || Path.IsPathRooted(file.RelativePath)
                || file.RelativePath.Contains('\\')
                || file.RelativePath.Contains(':')
                || file.RelativePath.Split('/').Any(segment => segment is ".." or "."))
                throw new ArgumentException(
                    $"content path '{file.RelativePath}' is not a safe package-relative path",
                    nameof(content));

            using var source = file.Open();
            using var target = archive
                .CreateEntry($"{NuGetPackageWriter.ContentFolder}/{file.RelativePath}").Open();
            source.CopyTo(target);
        }
    }

    private static void WriteText(ZipArchive archive, string path, string content)
    {
        using var stream = archive.CreateEntry(path).Open();
        using var writer = new StreamWriter(stream, Encoding.UTF8);
        writer.Write(content);
    }
}
