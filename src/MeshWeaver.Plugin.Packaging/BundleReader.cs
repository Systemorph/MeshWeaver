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
    /// <param name="Architecture">The portable RID the <paramref name="FrameworkMvid"/> lane belongs
    /// to (#1751), or null from a producer that predates the link. DIAGNOSTIC, never a second gate:
    /// the identity is the proof and already folds the architecture in. It is here so a decline can
    /// say "no arm64 lane" instead of only printing two hashes.</param>
    /// <param name="Misses">NodeTypes the producer could NOT resolve an assembly for in the
    /// requested lane, each with its reason (#1751). 🚨 Carried so the miss is COUNTABLE on the
    /// consumer too — a bundle that quietly arrives with fewer assemblies than the package has types
    /// is indistinguishable from a complete one, and "adopted N" is the only evidence the whole
    /// distribution lane works.</param>
    public sealed record Manifest(
        string? Plugin,
        string? Version,
        string? FrameworkMvid,
        IReadOnlyList<AssemblyRef>? Assemblies,
        ModuleRef? Module = null,
        string? Architecture = null,
        IReadOnlyList<string>? Misses = null);

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
        return Read(buffer);
    }

    /// <summary>
    /// <see cref="Read(byte[])"/> over a STREAM, optionally extracting only the node paths
    /// <paramref name="nodePaths"/> names.
    ///
    /// <para>🚨 Both parameters exist to keep bytes OUT of memory that nobody is going to use. A
    /// bundle's assemblies are the whole of its weight, and a consumer that has already adopted
    /// them needs none of it: reading a file as a stream avoids holding the compressed archive,
    /// and the filter avoids materialising decompressed assemblies the caller will discard. The
    /// boot-time seeder (<c>ShippedPrebuiltBundles</c>) asks
    /// <see cref="ReadManifest(string)"/> first and comes back here for the DEVIATING subset only
    /// — on a steady-state boot that subset is empty and this is never called at all.</para>
    ///
    /// <para><paramref name="nodePaths"/> <c>null</c> extracts everything, which is what a
    /// consumer holding a freshly-downloaded bundle wants.</para>
    /// </summary>
    /// <param name="bundle">The archive, positioned at its start.</param>
    /// <param name="nodePaths">Node paths to extract, or null for all of them.</param>
    /// <returns>The manifest (null when the archive carries none) and the payloads found.</returns>
    public static (Manifest? Manifest, IReadOnlyList<Payload> Assemblies) Read(
        Stream bundle, IReadOnlySet<string>? nodePaths = null)
    {
        using var archive = new ZipArchive(bundle, ZipArchiveMode.Read, leaveOpen: true);

        var manifest = ManifestOf(archive);
        if (manifest?.Assemblies is null)
            return (manifest, []);

        var payloads = new List<Payload>();
        foreach (var reference in manifest.Assemblies)
        {
            if (nodePaths is not null && !nodePaths.Contains(reference.NodePath))
                continue;

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
    /// The bundle's MANIFEST alone — every assembly's node path, dependency record and the
    /// framework identity, WITHOUT decompressing a single assembly.
    ///
    /// <para>This is what makes "load the manifest, then touch only what deviates" possible. The
    /// manifest is a few KB of JSON at a known entry; the assemblies it describes are megabytes.
    /// A consumer can therefore decide the whole adoption question — is this bundle for our
    /// framework, which of its types does this mesh hold, which of those are already on the
    /// store — before paying for any of the payload.</para>
    /// </summary>
    /// <param name="bundle">The archive, positioned at its start.</param>
    /// <returns>The manifest, or null when the archive carries none.</returns>
    public static Manifest? ReadManifest(Stream bundle)
    {
        using var archive = new ZipArchive(bundle, ZipArchiveMode.Read, leaveOpen: true);
        return ManifestOf(archive);
    }

    /// <summary>
    /// <see cref="ReadManifest(Stream)"/> for a bundle on disk — opened for sequential read and
    /// closed again, so the file's bytes never sit in the managed heap.
    /// </summary>
    /// <param name="bundlePath">Path to the bundle file.</param>
    /// <returns>The manifest, or null when the archive carries none.</returns>
    public static Manifest? ReadManifest(string bundlePath)
    {
        using var file = File.OpenRead(bundlePath);
        return ReadManifest(file);
    }

    /// <summary>
    /// <see cref="Read(Stream, IReadOnlySet{string})"/> for a bundle on disk.
    /// </summary>
    /// <param name="bundlePath">Path to the bundle file.</param>
    /// <param name="nodePaths">Node paths to extract, or null for all of them.</param>
    /// <returns>The manifest (null when the archive carries none) and the payloads found.</returns>
    public static (Manifest? Manifest, IReadOnlyList<Payload> Assemblies) ReadFile(
        string bundlePath, IReadOnlySet<string>? nodePaths = null)
    {
        using var file = File.OpenRead(bundlePath);
        return Read(file, nodePaths);
    }

    private static Manifest? ManifestOf(ZipArchive archive)
    {
        var manifestEntry = archive.GetEntry(NuGetPackageWriter.ManifestEntry);
        if (manifestEntry is null)
            return null;

        using var stream = manifestEntry.Open();
        return JsonSerializer.Deserialize<Manifest>(stream, Json);
    }

    /// <summary>
    /// Extracts the manifest and the MODULE closure files it names (#1664) — the module half of the
    /// same bundle <see cref="Read(byte[])"/> serves NodeType assemblies from.
    ///
    /// <para>Manifest-driven like <see cref="Read(byte[])"/>, for the same reason: the manifest is the
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
