using System.Text.Json;
using MeshWeaver.Plugin.Packaging;

namespace MeshWeaver.Plugin.Build;

/// <summary>
/// The <c>module-pack</c> mode (#1664 Slice B): produces a MODULE bundle from a built module
/// project's output, keyed to the framework MVID the module was actually compiled against.
///
/// <para><b>Reusable from any node repo by design.</b> The maintainer's constraint on the bundle
/// lane is that SocialMedia (and every other satellite repo that owns a module) drives the same
/// packer the platform does — so this is a plain dotnet-tool invocation over a build output folder,
/// with no assumption about which repo's CI is calling:</para>
///
/// <code>
/// dotnet run --project src/MeshWeaver.Plugin.Build -- module-pack ./artifacts/modules/MeshWeaver.Social \
///     --plugin SocialMedia --package-version 1.2.0 --out ./artifacts/bundles
/// </code>
///
/// <para><b>The closure is an explicit statement, never a scrape.</b> A publish output contains the
/// whole app closure — framework assemblies included — and bundling those would ship the platform
/// inside a module (and shadow it at the consumer, which is exactly what
/// <c>ModuleLandingService</c> refuses). So the bundle carries <c>&lt;name&gt;.dll</c> (+
/// <c>.pdb</c> when present) plus ONLY the files the caller names with <c>--with</c> — mirroring
/// the modules/&lt;Name&gt;/ layout rule that for most modules the DLL alone is the closure.</para>
///
/// <para><b>The MVID is read, never assumed.</b> The framework identity is the MVID of the
/// <c>MeshWeaver.Graph.dll</c> the module restored against (its copy rides the build output);
/// there is no default and no version-string fallback, because a bundle keyed to the wrong MVID is
/// declined everywhere and a bundle keyed to a GUESSED one would land and fault at the next boot.</para>
/// </summary>
public static class ModulePackCommand
{
    /// <summary>The CLI verb.</summary>
    public const string Verb = "module-pack";

    /// <summary>Runs the command; returns the process exit code.</summary>
    public static int Run(string[] args)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help")
        {
            Console.WriteLine("""
                usage: meshweaver-plugin-build module-pack <moduleOutputDir> [options]

                  <moduleOutputDir>           the built module's output folder (bin/Release/net10.0,
                                              a publish folder, or a curated modules/<Name>/ layout)
                  --module-name <name>        the module's entry-assembly name without extension
                                              (default: the folder name)
                  --plugin <id>               the package id the bundle belongs to (default: the
                                              module name) — must match the repo's plugin folder
                  --package-version <v>       REQUIRED — the module package's released SemVer (the
                                              manifest.lock version); there is no default, because
                                              a bundle at an invented version collides with the
                                              repo's immutable release numbering
                  --graph-dll <path>          the MeshWeaver.Graph.dll the module was built against
                                              (default: <moduleOutputDir>/MeshWeaver.Graph.dll);
                                              its MVID keys the bundle
                  --with <fileName>           an additional closure file from <moduleOutputDir>
                                              (repeatable). <name>.dll is always included, and its
                                              .pdb rides along when present.
                  --out <dir>                 where to write the bundle (default: current directory)
                """);
            return 0;
        }

        var moduleDirectory = Path.GetFullPath(args[0]);
        if (!Directory.Exists(moduleDirectory))
        {
            Console.Error.WriteLine($"error: module output directory not found: {moduleDirectory}");
            return 2;
        }

        var moduleName = Path.GetFileName(moduleDirectory.TrimEnd(Path.DirectorySeparatorChar));
        string? plugin = null;
        string? packageVersion = null;
        string? graphDll = null;
        var extras = new List<string>();
        var outputDirectory = Environment.CurrentDirectory;

        for (var i = 1; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--module-name" when i + 1 < args.Length:
                    moduleName = args[++i];
                    break;
                case "--plugin" when i + 1 < args.Length:
                    plugin = args[++i];
                    break;
                case "--package-version" when i + 1 < args.Length:
                    packageVersion = args[++i];
                    break;
                case "--graph-dll" when i + 1 < args.Length:
                    graphDll = Path.GetFullPath(args[++i]);
                    break;
                case "--with" when i + 1 < args.Length:
                    extras.Add(args[++i]);
                    break;
                case "--out" when i + 1 < args.Length:
                    outputDirectory = Path.GetFullPath(args[++i]);
                    break;
                default:
                    Console.Error.WriteLine($"error: unrecognised argument '{args[i]}'");
                    return 2;
            }
        }

        plugin ??= moduleName;

        // 🚨 Required, no default: the version names an immutable release (manifest.lock /
        // <Module>/vX.Y.Z tag), and minting one here would fork the version namespace the repo's
        // tagging exists to keep single.
        if (string.IsNullOrWhiteSpace(packageVersion))
        {
            Console.Error.WriteLine(
                "error: --package-version is required (the module package's released SemVer — "
                + "its manifest.lock `version`)");
            return 2;
        }

        var entryDll = Path.Combine(moduleDirectory, moduleName + ".dll");
        if (!File.Exists(entryDll))
        {
            Console.Error.WriteLine(
                $"error: entry assembly not found: {entryDll} — a module bundle without its entry "
                + "DLL could never load; check --module-name against the build output");
            return 2;
        }

        graphDll ??= Path.Combine(moduleDirectory, FrameworkIdentity.IdentityAssembly + ".dll");
        if (!File.Exists(graphDll))
        {
            Console.Error.WriteLine(
                $"error: {FrameworkIdentity.IdentityAssembly}.dll not found at {graphDll} — the "
                + "bundle is keyed to the framework MVID the module was BUILT against, and there "
                + "is no default: a guessed identity lands bytes that fault at the next boot. "
                + "Point --graph-dll at the restored framework assembly (it rides the module's "
                + "build output when CopyLocal is on).");
            return 2;
        }

        var frameworkMvid = FrameworkIdentity.ReadMvid(graphDll);

        // The closure: entry DLL (+ symbols when present) + exactly the files the caller named.
        var closure = new List<string> { moduleName + ".dll" };
        var entryPdb = moduleName + ".pdb";
        if (File.Exists(Path.Combine(moduleDirectory, entryPdb)))
            closure.Add(entryPdb);
        foreach (var extra in extras)
        {
            if (Path.GetFileName(extra) != extra)
            {
                Console.Error.WriteLine(
                    $"error: --with takes a plain file name inside the module folder, got '{extra}'");
                return 2;
            }
            if (!File.Exists(Path.Combine(moduleDirectory, extra)))
            {
                Console.Error.WriteLine(
                    $"error: --with file not found: {Path.Combine(moduleDirectory, extra)} — "
                    + "packing a partial closure would land a module that faults at first use");
                return 2;
            }
            if (!closure.Contains(extra, StringComparer.OrdinalIgnoreCase))
                closure.Add(extra);
        }

        var manifest = new PluginManifest(
            plugin, PluginManifest.IdPrefix + plugin, packageVersion, plugin, null, []);

        var manifestJson = JsonSerializer.Serialize(
            new
            {
                plugin,
                version = packageVersion,
                // 🚨 The identity a consumer verifies before landing a single byte. The runtime
                // compares MVIDs, never version strings.
                frameworkMvid,
                module = new { assemblyName = moduleName, assemblies = closure },
            },
            new JsonSerializerOptions { WriteIndented = true });

        var entries = closure
            .Select(fileName =>
            {
                var path = Path.Combine(moduleDirectory, fileName);
                return new NuGetPackageWriter.Entry(
                    NuGetPackageWriter.ModuleEntryPathFor(fileName), () => File.OpenRead(path));
            })
            .ToList();

        Directory.CreateDirectory(outputDirectory);
        var bundlePath = Path.Combine(
            outputDirectory, $"{manifest.PackageId}.{packageVersion}.module.nupkg");
        if (File.Exists(bundlePath))
            File.Delete(bundlePath);
        using (var output = File.Create(bundlePath))
            NuGetPackageWriter.Write(output, manifest, packageVersion, entries, manifestJson);

        Console.WriteLine(
            $"packed {Path.GetFileName(bundlePath)} — module {moduleName}, "
            + $"{closure.Count} file(s), framework MVID {frameworkMvid}");
        return 0;
    }
}
