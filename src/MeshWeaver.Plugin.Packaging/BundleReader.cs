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
    public sealed record Manifest(
        string? Plugin,
        string? Version,
        string? FrameworkMvid,
        IReadOnlyList<AssemblyRef>? Assemblies);

    /// <summary>One assembly and the NodeType it implements.</summary>
    /// <param name="NodePath">Mesh path of the NodeType — the key a consumer re-seeds under.</param>
    /// <param name="Assembly">File name inside <see cref="NuGetPackageWriter.AssemblyFolder"/>.</param>
    public sealed record AssemblyRef(string NodePath, string Assembly);

    /// <summary>An assembly's bytes, ready to seed.</summary>
    /// <param name="NodePath">Mesh path of the NodeType these bytes implement.</param>
    /// <param name="Assembly">The compiled assembly.</param>
    /// <param name="Pdb">Symbols, when the bundle carried them.</param>
    public sealed record Payload(string NodePath, byte[] Assembly, byte[]? Pdb);

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
                reference.NodePath, ReadAll(dll), pdb is null ? null : ReadAll(pdb)));
        }

        return (manifest, payloads);
    }

    private static byte[] ReadAll(ZipArchiveEntry entry)
    {
        using var source = entry.Open();
        using var target = new MemoryStream();
        source.CopyTo(target);
        return target.ToArray();
    }
}
