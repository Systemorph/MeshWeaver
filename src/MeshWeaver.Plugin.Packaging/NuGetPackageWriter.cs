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

    /// <summary>
    /// Where a module's STATIC WEB ASSETS ride. Separate from <see cref="ModuleFolder"/> because
    /// module files are flat while assets keep their relative path: a view pack's
    /// <c>wwwroot/leaflet/leaflet.js</c> has to land under the same relative path or the URLs its
    /// own components request (<c>_content/&lt;pack&gt;/…</c>) resolve to nothing.
    /// </summary>
    public const string ModuleAssetFolder = "meshweaver/moduleassets";

    /// <summary>The bundle entry path for one static asset, by its module-relative path.</summary>
    public static string ModuleAssetEntryPathFor(string relativePath) =>
        $"{ModuleAssetFolder}/{relativePath}";

    /// <summary>
    /// Where a module's RID-specific NATIVE payloads ride (#4126) — a section of its own, declared
    /// in the manifest like the other two.
    ///
    /// <para>🚨 It cannot be <see cref="ModuleFolder"/>. Every consumer of that folder filters to
    /// FLAT entries by construction — <c>ServedModuleBytes</c> and <c>PublishedBundleCatalogue</c>
    /// both require the remainder after the prefix to contain no <c>/</c> — so a native written
    /// under <c>meshweaver/modules/runtimes/…</c> would be SILENTLY skipped, not laid out. And it
    /// cannot be <see cref="ModuleAssetFolder"/> either: that folder means "static WEB assets", the
    /// landing service's own logs and docs say wwwroot, and conflating a loadable binary with a
    /// served file would make every future rule about one apply to the other.</para>
    ///
    /// <para>Unlike module files, and like static assets, the relative path is PRESERVED: the
    /// loader probes <c>&lt;moduleDir&gt;/runtimes/&lt;rid&gt;/native/&lt;lib&gt;</c>, so the path
    /// IS the contract.</para>
    /// </summary>
    public const string ModuleNativeFolder = "meshweaver/modulenatives";

    /// <summary>The bundle entry path for one native payload, by its module-relative path
    /// (<c>runtimes/&lt;rid&gt;/native/&lt;file&gt;</c>).</summary>
    /// <param name="relativePath">The module-relative path, <c>/</c>-separated.</param>
    public static string ModuleNativeEntryPathFor(string relativePath) =>
        $"{ModuleNativeFolder}/{relativePath}";

    /// <summary>
    /// 🚨 <b>The EXACT layout a carried native must have</b> — <c>runtimes/&lt;rid&gt;/native/&lt;file&gt;</c>,
    /// four segments, no more and no fewer. ONE spelling, shared by the derivation, the packer and
    /// the reader, so the three cannot drift.
    ///
    /// <para><c>ModuleNativeAssets.CandidatePaths</c> composes the probe from exactly those four
    /// parts and has no recursive walk, so a payload at <c>runtimes/&lt;rid&gt;/other/native/x.so</c>
    /// — which a substring test for <c>/native/</c> accepts — is carried and then never looked at:
    /// bytes that read as shipped and behave as absent, the one outcome a native section exists to
    /// prevent.</para>
    ///
    /// <para>It also rejects traversal and empty segments BEFORE anything resolves the path against
    /// a directory: the packer reads these from disk and a lander writes them to disk, so
    /// <c>runtimes/../../native/x.so</c> must never reach either.</para>
    /// </summary>
    /// <param name="relativePath">The module-relative path, <c>/</c>-separated.</param>
    /// <returns><c>true</c> when the path is exactly the probed layout.</returns>
    public static bool IsModuleNativeLayout(string? relativePath)
    {
        if (string.IsNullOrEmpty(relativePath) || relativePath.Contains('\\'))
            return false;
        var segments = relativePath.Split('/');
        if (segments.Length != 4)
            return false;
        if (!string.Equals(segments[0], "runtimes", StringComparison.Ordinal)
            || !string.Equals(segments[2], "native", StringComparison.Ordinal))
            return false;
        // The RID and the file name: non-empty, and neither a traversal nor a self-reference.
        foreach (var segment in new[] { segments[1], segments[3] })
            if (string.IsNullOrEmpty(segment) || segment == "." || segment == "..")
                return false;
        return true;
    }

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
          <!-- Native payloads (#4126). A [Content_Types].xml that does not declare an extension
               makes the part undeclared for a strict OPC reader; these are the three shapes a
               loadable native takes on the platforms this ships to. -->
          <Default Extension="so" ContentType="application/octet" />
          <Default Extension="dylib" ContentType="application/octet" />
          <Default Extension="pdb" ContentType="application/octet" />
        </Types>
        """;
}
