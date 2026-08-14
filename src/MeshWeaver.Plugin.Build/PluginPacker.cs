using System.Collections.Immutable;
using System.IO.Compression;
using System.Security;
using System.Text;
using System.Text.Json;

namespace MeshWeaver.Plugin.Build;

/// <summary>
/// Writes a plugin's <c>.nupkg</c>: the node content it already ships, plus the prebuilt assembly
/// for every compilation unit, plus a manifest binding one to the other.
/// </summary>
public static class PluginPacker
{
    /// <summary>
    /// 🚨 Assemblies go under <c>meshweaver/assemblies/</c>, NOT <c>lib/net10.0/</c>.
    ///
    /// <para>A plugin's units are compiled SEPARATELY at runtime and may legitimately declare the
    /// same type names — <c>TaskAssignmentService</c> exists in two UWDeepfield units. Under
    /// <c>lib/</c> NuGet would surface all of them as compile-time references of any consumer, so
    /// those duplicates would collide and the CLR identity the runtime deliberately keeps separate
    /// would be unified. These assemblies are payload for the assembly store, not a reference set.</para>
    /// </summary>
    private const string AssemblyFolder = "meshweaver/assemblies";

    /// <summary>Node content, shipped verbatim so install stays a copy rather than a re-render.</summary>
    private const string ContentFolder = "meshweaver/content";

    /// <summary>Packs the plugin and returns the written package path.</summary>
    /// <param name="pluginDirectory">The plugin root in a repo checkout.</param>
    /// <param name="manifest">Metadata projected from <c>index.json</c>.</param>
    /// <param name="units">The plugin's compilation units.</param>
    /// <param name="buildOutputRoot">Where <see cref="ProjectEmitter"/> wrote its projects.</param>
    /// <param name="frameworkVersion">The MeshWeaver version the assemblies were compiled against —
    /// recorded in the package manifest because it is what <c>HasUsableBuild</c> compares at
    /// activation. An assembly whose framework version cannot be established must be recompiled,
    /// never loaded on faith.</param>
    /// <param name="outputDirectory">Directory to write the <c>.nupkg</c> into.</param>
    public static string Pack(
        string pluginDirectory,
        PluginManifest manifest,
        ImmutableArray<PluginUnit> units,
        string buildOutputRoot,
        string frameworkVersion,
        string outputDirectory)
    {
        Directory.CreateDirectory(outputDirectory);
        var packagePath = Path.Combine(outputDirectory, $"{manifest.PackageId}.{manifest.Version}.nupkg");
        if (File.Exists(packagePath))
            File.Delete(packagePath);

        using var archive = ZipFile.Open(packagePath, ZipArchiveMode.Create);

        Write(archive, $"{manifest.PackageId}.nuspec", BuildNuspec(manifest, frameworkVersion));
        Write(archive, "[Content_Types].xml", ContentTypes);

        var assemblies = new List<object>();
        foreach (var unit in units)
        {
            var dll = Path.Combine(buildOutputRoot, unit.UnitName, "bin", "Release", "net10.0", unit.UnitName + ".dll");
            if (!File.Exists(dll))
                throw new InvalidOperationException(
                    $"missing assembly for {unit.NodePath} at {dll} — pack requires a completed build; " +
                    "a package with a partial assembly set is worse than none, because a consumer " +
                    "resolves the gap as a runtime TypeLoadException instead of a compile error");

            archive.CreateEntryFromFile(dll, $"{AssemblyFolder}/{unit.UnitName}.dll");
            assemblies.Add(new { nodePath = unit.NodePath, assembly = $"{unit.UnitName}.dll" });
        }

        foreach (var file in Directory.EnumerateFiles(pluginDirectory, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(pluginDirectory, file).Replace(Path.DirectorySeparatorChar, '/');
            // obj/bin are build residue from a developer's tree, never plugin content.
            if (relative.StartsWith("obj/", StringComparison.Ordinal)
                || relative.StartsWith("bin/", StringComparison.Ordinal)
                || relative.StartsWith(".worktrees/", StringComparison.Ordinal))
                continue;
            archive.CreateEntryFromFile(file, $"{ContentFolder}/{relative}");
        }

        Write(archive, "meshweaver/manifest.json", JsonSerializer.Serialize(
            new
            {
                plugin = manifest.Name,
                version = manifest.Version,
                frameworkVersion,
                assemblies,
            },
            new JsonSerializerOptions { WriteIndented = true }));

        return packagePath;
    }

    private static void Write(ZipArchive archive, string path, string content)
    {
        using var stream = archive.CreateEntry(path).Open();
        using var writer = new StreamWriter(stream, Encoding.UTF8);
        writer.Write(content);
    }

    private static string BuildNuspec(PluginManifest manifest, string frameworkVersion)
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

        // The framework dependency is what makes an ABI mismatch a RESOLVER error instead of a
        // TypeLoadException in an ALC at activation — the failure mode prebuilt assemblies would
        // otherwise introduce, and the one the runtime has no diagnostic for.
        sb.AppendLine($"""        <dependency id="MeshWeaver.Graph" version="[{SecurityElement.Escape(frameworkVersion)}]" />""");

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
