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
/// <para>A bundle is deliberately NOT a nupkg: it carries no content, no nuspec, no dependency
/// graph — only compiled bytes and the identity they are pinned to. The framework MVID in the
/// manifest is what <c>PrebuiltAssemblySeeder.DeclineReason</c> gates adoption on, so writing it is
/// mandatory: a bundle without it is dead weight every consumer refuses (correctly).</para>
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
        IReadOnlyDictionary<string, string>? Dependencies = null);

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
        string? sourceSha = null)
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
                })
                .ToList(),
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
    }

    private static void WriteText(ZipArchive archive, string path, string content)
    {
        using var stream = archive.CreateEntry(path).Open();
        using var writer = new StreamWriter(stream, Encoding.UTF8);
        writer.Write(content);
    }
}
