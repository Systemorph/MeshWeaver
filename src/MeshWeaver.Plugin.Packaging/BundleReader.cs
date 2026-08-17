using System.IO.Compression;
using System.Text.Json;

namespace MeshWeaver.Plugin.Packaging;

/// <summary>
/// Reads what <see cref="NuGetPackageWriter"/> wrote. Lives beside the writer so the two cannot
/// drift: the folder names, the manifest entry and the node-path mapping are stated once.
/// </summary>
public static class BundleReader
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>The bundle manifest: which node each assembly implements, and what framework they
    /// were compiled against.</summary>
    /// <param name="Plugin">Plugin id the bundle is for.</param>
    /// <param name="Version">The plugin's released SemVer.</param>
    /// <param name="FrameworkMvid">MVID of the MeshWeaver.Graph assembly that compiled these bytes.
    /// Null when the producer recorded none, which a consumer must treat as "cannot adopt".</param>
    /// <param name="Assemblies">One entry per compiled NodeType.</param>
    /// <param name="Module">The compiled MODULE this bundle carries (#1664), or null for a
    /// NodeType-only bundle. A MIXED package (content + NodeTypes + a module) carries both
    /// <paramref name="Assemblies"/> and this in ONE bundle — one reader, one transport.</param>
    public sealed record Manifest(
        string? Plugin,
        string? Version,
        string? FrameworkMvid,
        IReadOnlyList<AssemblyRef>? Assemblies,
        ModuleRef? Module = null);

    /// <summary>One assembly and the NodeType it implements.</summary>
    /// <param name="NodePath">Mesh path of the NodeType — the key a consumer re-seeds under.</param>
    /// <param name="Assembly">File name inside <see cref="NuGetPackageWriter.AssemblyFolder"/>.</param>
    /// <param name="Dependencies">The producer's per-type dependency record (#1707 slice 2), or
    /// null for a legacy bundle.</param>
    public sealed record AssemblyRef(
        string NodePath, string Assembly, IReadOnlyDictionary<string, string>? Dependencies = null);

    /// <summary>The bundle's compiled-module declaration.</summary>
    /// <param name="AssemblyName">The module's entry-assembly name WITHOUT extension
    /// (e.g. <c>MeshWeaver.Social</c>) — the same identity <c>Modules:Assemblies</c> entries and
    /// <c>modules/&lt;name&gt;/</c> folders use.</param>
    /// <param name="Assemblies">File names inside <see cref="NuGetPackageWriter.ModuleFolder"/> —
    /// the module's private closure (for most modules just <c>&lt;AssemblyName&gt;.dll</c>).</param>
    /// <param name="MinMeshVersion">The module's declared platform FLOOR — the consumer's landing
    /// gate (a plain .NET assembly binding by simple name is compatible by API, expressed as a
    /// semver floor). Null = no constraint. The manifest-level <see cref="Manifest.FrameworkMvid"/>
    /// stays the NodeType lane's strict gate and, for the module, DIAGNOSTIC metadata only.</param>
    public sealed record ModuleRef(
        string? AssemblyName, IReadOnlyList<string>? Assemblies, string? MinMeshVersion = null);

    /// <summary>One landed-to-be module file: its name and bytes.</summary>
    /// <param name="FileName">File name as the manifest declared it.</param>
    /// <param name="Bytes">The file's bytes.</param>
    public sealed record ModuleFile(string FileName, byte[] Bytes);

    /// <summary>An assembly's bytes, ready to seed.</summary>
    /// <param name="NodePath">Mesh path of the NodeType these bytes implement.</param>
    /// <param name="Assembly">The compiled assembly.</param>
    /// <param name="Pdb">Symbols, when the bundle carried them.</param>
    /// <param name="Dependencies">The producer's per-type dependency record for these bytes
    /// (#1707 slice 2), joined from the manifest, or null for a legacy bundle.</param>
    public sealed record Payload(
        string NodePath, byte[] Assembly, byte[]? Pdb,
        IReadOnlyDictionary<string, string>? Dependencies = null);

    /// <summary>
    /// Extracts the manifest and every assembly it names.
    ///
    /// <para>🚨 Driven by the MANIFEST, never by enumerating
    /// <see cref="NuGetPackageWriter.AssemblyFolder"/>. Today
    /// <see cref="NuGetPackageWriter.EntryPathFor"/> keeps the node path verbatim, so the two happen
    /// to agree — but that is the WRITER's guarantee to make and change, not something a reader may
    /// assume. Reading the mapping the producer wrote is the only way to know which NodeType an
    /// assembly belongs to; recovering it from a file name would seed correct bytes against the
    /// wrong node, which fails at activation with a TypeLoadException rather than anywhere near
    /// here.</para>
    ///
    /// <para>An assembly the manifest names but the archive lacks is SKIPPED, not fatal: the rest of
    /// the bundle stays usable and that one NodeType compiles as it would have anyway.</para>
    /// </summary>
    /// <param name="bundle">The archive bytes.</param>
    /// <returns>The manifest (null when the archive carries none) and the payloads found.</returns>
    public static (Manifest? Manifest, IReadOnlyList<Payload> Assemblies) Read(byte[] bundle)
    {
        using var buffer = new MemoryStream(bundle, writable: false);
        using var archive = new ZipArchive(buffer, ZipArchiveMode.Read);

        var manifestEntry = archive.GetEntry(NuGetPackageWriter.ManifestEntry);
        if (manifestEntry is null)
            return (null, []);

        Manifest? manifest;
        using (var stream = manifestEntry.Open())
            manifest = JsonSerializer.Deserialize<Manifest>(stream, Json);

        if (manifest?.Assemblies is null)
            return (manifest, []);

        var payloads = new List<Payload>();
        foreach (var reference in manifest.Assemblies)
        {
            var dll = archive.GetEntry($"{NuGetPackageWriter.AssemblyFolder}/{reference.Assembly}");

            if (dll is null)
                continue;

            var pdb = archive.GetEntry(
                $"{NuGetPackageWriter.AssemblyFolder}/"
                + Path.ChangeExtension(reference.Assembly, ".pdb"));

            payloads.Add(new Payload(
                reference.NodePath, ReadAll(dll), pdb is null ? null : ReadAll(pdb),
                reference.Dependencies));
        }

        return (manifest, payloads);
    }

    /// <summary>
    /// Extracts the manifest and the MODULE closure files it names (#1664) — the module half of the
    /// same bundle <see cref="Read"/> serves NodeType assemblies from.
    ///
    /// <para>Manifest-driven like <see cref="Read"/>, for the same reason: the manifest is the
    /// producer's statement of which files belong to the module, and enumerating
    /// <see cref="NuGetPackageWriter.ModuleFolder"/> instead would adopt any stray entry a future
    /// writer happens to place there. A file the manifest names but the archive lacks is FATAL here,
    /// unlike the NodeType path: a NodeType with missing bytes simply compiles, but a module folder
    /// missing part of its closure loads and then faults at first use — so an incomplete module
    /// yields NO files rather than a subset that would land.</para>
    /// </summary>
    /// <param name="bundle">The archive bytes.</param>
    /// <returns>The manifest (null when the archive carries none) and the module's files — empty
    /// when the bundle declares no module or the declared closure is incomplete.</returns>
    public static (Manifest? Manifest, IReadOnlyList<ModuleFile> Files) ReadModule(byte[] bundle)
    {
        using var buffer = new MemoryStream(bundle, writable: false);
        using var archive = new ZipArchive(buffer, ZipArchiveMode.Read);

        var manifestEntry = archive.GetEntry(NuGetPackageWriter.ManifestEntry);
        if (manifestEntry is null)
            return (null, []);

        Manifest? manifest;
        using (var stream = manifestEntry.Open())
            manifest = JsonSerializer.Deserialize<Manifest>(stream, Json);

        if (manifest?.Module?.Assemblies is not { Count: > 0 } declared)
            return (manifest, []);

        var files = new List<ModuleFile>();
        foreach (var fileName in declared)
        {
            var entry = archive.GetEntry(NuGetPackageWriter.ModuleEntryPathFor(fileName));
            if (entry is null)
                // Incomplete closure — all or nothing (see remarks).
                return (manifest, []);
            files.Add(new ModuleFile(fileName, ReadAll(entry)));
        }

        return (manifest, files);
    }

    private static byte[] ReadAll(ZipArchiveEntry entry)
    {
        using var source = entry.Open();
        using var target = new MemoryStream();
        source.CopyTo(target);
        return target.ToArray();
    }
}
