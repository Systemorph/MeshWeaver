using System.Collections.Immutable;
using System.IO.Compression;
using System.Security;
using System.Text;
using System.Text.Json;

using MeshWeaver.Plugin.Packaging;

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

        var entries = new List<NuGetPackageWriter.Entry>();
        var assemblies = new List<object>();

        foreach (var unit in units)
        {
            var dll = Path.Combine(buildOutputRoot, unit.UnitName, "bin", "Release", "net10.0", unit.UnitName + ".dll");
            if (!File.Exists(dll))
                throw new InvalidOperationException(
                    $"missing assembly for {unit.NodePath} at {dll} — pack requires a completed build; " +
                    "a package with a partial assembly set is worse than none, because a consumer " +
                    "resolves the gap as a runtime TypeLoadException instead of a compile error");

            var path = dll;
            entries.Add(new NuGetPackageWriter.Entry(
                $"{NuGetPackageWriter.AssemblyFolder}/{unit.UnitName}.dll",
                () => File.OpenRead(path)));
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
            var path = file;
            entries.Add(new NuGetPackageWriter.Entry(
                $"{NuGetPackageWriter.ContentFolder}/{relative}", () => File.OpenRead(path)));
        }

        var manifestJson = JsonSerializer.Serialize(
            new
            {
                plugin = manifest.Name,
                version = manifest.Version,
                frameworkVersion,
                // 🚨 The identity an installer must check before seeding these bytes into the
                // assembly store. The runtime compares MVIDs, not version strings.
                frameworkMvid = FrameworkIdentity.ResolveFrameworkMvid(frameworkVersion),
                assemblies,
            },
            new JsonSerializerOptions { WriteIndented = true });

        using (var output = File.Create(packagePath))
            NuGetPackageWriter.Write(output, manifest, frameworkVersion, entries, manifestJson);

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

        // 🚨 A MINIMUM, not an exact pin. The bare form is NuGet's ">= this version".
        //
        // The version the package was built against is a floor, not a requirement: the bake
        // recompiles the plugin's source against whatever the consumer resolves, so a NEWER
        // framework satisfies it. Pinning it exactly (`[x]`) would make every framework bump
        // require republishing all ~29 plugins at new versions — and since a module's PATCH is
        // derived from its CONTENT hash, those republishes would carry the same version as the
        // unchanged tree, which the immutability rule forbids. The package would be unshippable
        // without inventing a version the repo never released.
        //
        // The prebuilt assemblies remain an OPTIMISATION on top: PrebuiltAssemblySeeder adopts them
        // only when the framework MVID matches exactly, and compiles when it does not. So a floor
        // here never risks loading ABI-incompatible bytes — that gate is separate and stricter.
        sb.AppendLine($"""        <dependency id="MeshWeaver.Graph" version="{SecurityElement.Escape(frameworkVersion)}" />""");

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
