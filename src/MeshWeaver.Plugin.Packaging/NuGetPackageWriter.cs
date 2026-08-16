using System.IO.Compression;
using System.Security;
using System.Text;

namespace MeshWeaver.Plugin.Packaging;

/// <summary>
/// Writes a plugin's <c>.nupkg</c> from entries the caller supplies.
///
/// <para>Deliberately source-agnostic: the CI tool feeds it files off disk, and the portal feeds it
/// node content plus assemblies out of the assembly store. Both must produce the SAME package for
/// the same plugin, so the layout and the nuspec live here once rather than in each caller.</para>
/// </summary>
public static class NuGetPackageWriter
{
    /// <summary>
    /// 🚨 Assemblies go under <c>meshweaver/assemblies/</c>, NOT <c>lib/net10.0/</c>.
    ///
    /// <para>A plugin's units are compiled SEPARATELY at runtime and may legitimately declare the
    /// same type names — <c>TaskAssignmentService</c> exists in two UWDeepfield units. Under
    /// <c>lib/</c> NuGet would surface all of them as compile-time references of any consumer, so
    /// those duplicates would collide and the CLR identity the runtime keeps separate would be
    /// unified. These are payload for the assembly store, not a reference set.</para>
    /// </summary>
    public const string AssemblyFolder = "meshweaver/assemblies";

    /// <summary>Node content, shipped verbatim so install stays a copy rather than a re-render.</summary>
    public const string ContentFolder = "meshweaver/content";

    /// <summary>
    /// A compiled MODULE's closure files (#1664). Distinct from <see cref="AssemblyFolder"/> on
    /// purpose: NodeType assemblies are payload for the assembly store (per-node ALC, no restart),
    /// while module files land beside the app in <c>modules/&lt;name&gt;/</c> and load into the
    /// DEFAULT ALC at the next restart. Mixing the two folders would let a consumer seed a module
    /// DLL as a NodeType assembly — correct bytes in the wrong lane, surfacing only as a
    /// <c>TypeLoadException</c> at activation.
    /// </summary>
    public const string ModuleFolder = "meshweaver/modules";

    /// <summary>Where the node-path→assembly map lives inside the package.</summary>
    public const string ManifestEntry = "meshweaver/manifest.json";

    /// <summary>
    /// The archive entry path for a NodeType's assembly — the node path VERBATIM under
    /// <see cref="AssemblyFolder"/>.
    ///
    /// <para>🚨 Never slash-replaced. Sanitising is not injective: <c>A/B/C</c> and <c>A_B/C</c>
    /// both become <c>A_B_C</c>, and mesh paths do contain underscores, so two NodeTypes would land
    /// on one archive entry and the second would silently adopt the first's bytes — a mismatch that
    /// surfaces only as a <c>TypeLoadException</c> at activation. Zip entry names take slashes
    /// natively and nothing extracts to disk (consumers read entries into memory), so there is no
    /// traversal concern to trade against it.</para>
    ///
    /// <para>The manifest still carries the mapping. A consumer must read the node path the producer
    /// wrote, never recover it from a file name.</para>
    /// </summary>
    /// <param name="nodePath">Mesh path of the NodeType.</param>
    /// <param name="extension">File extension including the dot, e.g. <c>.dll</c>.</param>
    public static string EntryPathFor(string nodePath, string extension = ".dll") =>
        $"{AssemblyFolder}/{nodePath}{extension}";

    /// <summary>
    /// The archive entry path for one of a MODULE's closure files — the file name verbatim under
    /// <see cref="ModuleFolder"/>. Module files are flat (a bundle carries at most ONE module, and
    /// its manifest names every file), so unlike <see cref="EntryPathFor"/> there is no path
    /// component to preserve — but the same rule holds: a consumer reads the file list from the
    /// MANIFEST, never by enumerating the folder.
    /// </summary>
    /// <param name="fileName">The closure file's name, e.g. <c>MeshWeaver.Social.dll</c>.</param>
    public static string ModuleEntryPathFor(string fileName) =>
        $"{ModuleFolder}/{fileName}";

    /// <summary>One file destined for the package.</summary>
    /// <param name="PathInPackage">Full entry path, e.g. <c>meshweaver/content/index.json</c>.</param>
    /// <param name="OpenRead">Opens the bytes. A factory rather than a byte[] so a large assembly
    /// is streamed into the archive instead of held in memory — the portal may assemble several
    /// packages concurrently.</param>
    public sealed record Entry(string PathInPackage, Func<Stream> OpenRead);

    /// <summary>
    /// Writes the package into <paramref name="destination"/>. The stream is left open so the
    /// caller can rewind and serve it — the portal writes to a buffer it then streams to a NuGet
    /// client, and closing here would defeat that.
    /// </summary>
    /// <param name="destination">Target stream.</param>
    /// <param name="manifest">Metadata; the nuspec is a projection of it.</param>
    /// <param name="frameworkVersion">MeshWeaver version the assemblies were built against, emitted
    /// as a MINIMUM. The bake recompiles against whatever the consumer resolves, so a newer
    /// framework satisfies it; pinning exactly would force republishing every plugin on each
    /// framework bump, at versions the content-derived PATCH forbids minting.</param>
    /// <param name="entries">Content and assembly entries.</param>
    /// <param name="manifestJson">The <see cref="ManifestEntry"/> body.</param>
    public static void Write(
        Stream destination,
        PluginManifest manifest,
        string frameworkVersion,
        IEnumerable<Entry> entries,
        string manifestJson)
    {
        using var archive = new ZipArchive(destination, ZipArchiveMode.Create, leaveOpen: true);

        WriteText(archive, $"{manifest.PackageId}.nuspec", BuildNuspec(manifest, frameworkVersion));
        WriteText(archive, "[Content_Types].xml", ContentTypes);
        WriteText(archive, ManifestEntry, manifestJson);

        foreach (var entry in entries)
        {
            using var source = entry.OpenRead();
            using var target = archive.CreateEntry(entry.PathInPackage).Open();
            source.CopyTo(target);
        }
    }

    private static void WriteText(ZipArchive archive, string path, string content)
    {
        using var stream = archive.CreateEntry(path).Open();
        using var writer = new StreamWriter(stream, Encoding.UTF8);
        writer.Write(content);
    }

    /// <summary>
    /// The nuspec — a projection of the mesh manifest authors already write, never something
    /// invented here. <c>"requires": ["Store@^1.0.0"]</c> becomes a caret range;
    /// <c>version</c> comes from <c>manifest.lock</c>, which is the number tagged
    /// <c>&lt;Module&gt;/vX.Y.Z</c>.
    /// </summary>
    public static string BuildNuspec(PluginManifest manifest, string frameworkVersion)
    {
        var sb = new StringBuilder();
        sb.AppendLine("""<?xml version="1.0" encoding="utf-8"?>""");
        sb.AppendLine("""<package xmlns="http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd">""");
        sb.AppendLine("  <metadata>");
        sb.AppendLine($"    <id>{SecurityElement.Escape(manifest.PackageId)}</id>");
        sb.AppendLine($"    <version>{SecurityElement.Escape(manifest.Version)}</version>");
        sb.AppendLine($"    <description>{SecurityElement.Escape(manifest.Description)}</description>");
        sb.AppendLine("    <authors>Systemorph</authors>");
        sb.AppendLine("    <dependencies>");
        sb.AppendLine("""      <group targetFramework="net10.0">""");
        sb.AppendLine(
            $"""        <dependency id="MeshWeaver.Graph" version="{SecurityElement.Escape(frameworkVersion)}" />""");

        foreach (var (id, range) in manifest.ResolveDependencies())
            sb.AppendLine(range is null
                ? $"""        <dependency id="{SecurityElement.Escape(id)}" />"""
                : $"""        <dependency id="{SecurityElement.Escape(id)}" version="{SecurityElement.Escape(range)}" />""");

        sb.AppendLine("      </group>");
        sb.AppendLine("    </dependencies>");
        sb.AppendLine("  </metadata>");
        sb.AppendLine("</package>");
        return sb.ToString();
    }

    private const string ContentTypes = """
        <?xml version="1.0" encoding="utf-8"?>
        <Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">
          <Default Extension="nuspec" ContentType="application/octet" />
          <Default Extension="dll" ContentType="application/octet" />
          <Default Extension="json" ContentType="application/octet" />
          <Default Extension="md" ContentType="application/octet" />
          <Default Extension="cs" ContentType="application/octet" />
          <Default Extension="png" ContentType="application/octet" />
          <Default Extension="svg" ContentType="application/octet" />
          <Default Extension="lock" ContentType="application/octet" />
        </Types>
        """;
}
