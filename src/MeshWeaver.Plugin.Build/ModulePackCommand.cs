using System.Text.Json;
using System.Text.RegularExpressions;
using MeshWeaver.Plugin.Packaging;

namespace MeshWeaver.Plugin.Build;

/// <summary>
/// The <c>module-pack</c> mode (#1664 Slice B): produces a MODULE bundle from a built module
/// project's output.
///
/// <para><b>Reusable from any node repo by design.</b> The maintainer's constraint on the bundle
/// lane is that SocialMedia (and every other satellite repo that owns a module) drives the same
/// packer the platform does — so this is a plain dotnet-tool invocation over a build output folder,
/// with no assumption about which repo's CI is calling:</para>
///
/// <code>
/// dotnet run --project src/MeshWeaver.Plugin.Build -- module-pack ./artifacts/modules/MeshWeaver.Social \
///     --plugin SocialMedia --package-version 1.2.0 --min-mesh-version 3.0.0 --out ./artifacts/bundles
/// </code>
///
/// <para><b>The closure is an explicit statement, never a scrape.</b> A publish output contains the
/// whole app closure — framework assemblies included — and bundling those would ship the platform
/// inside a module (and shadow it at the consumer, which is exactly what
/// <c>ModuleLandingService</c> refuses). So the bundle carries <c>&lt;name&gt;.dll</c> (+
/// <c>.pdb</c> when present) plus ONLY the files the caller names with <c>--with</c> — mirroring
/// the modules/&lt;Name&gt;/ layout rule that for most modules the DLL alone is the closure.</para>
///
/// <para><b>The consumer's gate is the <c>minMeshVersion</c> FLOOR, not an MVID.</b> A module is a
/// plain assembly binding by simple name; its contract is API compatibility, so the bundle records
/// the platform floor the module requires (absent = no constraint) and the consumer lands anything
/// whose floor it satisfies — one bundle serves every compatible platform build, and nothing needs
/// rebundling per CI build. The MVID of the identity anchor
/// (<c>MeshWeaver.Compiler.dll</c>, #1707) in the build output is still recorded when found, as
/// DIAGNOSTIC metadata naming the exact build behind the bytes; MVID equality remains the
/// NodeType (bake) lane's gate only.</para>
/// </summary>
public static class ModulePackCommand
{
    /// <summary>The CLI verb.</summary>
    public const string Verb = "module-pack";

    // 🚨 These values flow into FILE PATHS (the entry-DLL probe, the closure entries, the output
    // bundle name) and into the bundle manifest, so they are validated to a safe shape before any
    // path is composed — "../evil" as a module name must be a clear exit-2, never a probe outside
    // the module folder or a bundle written somewhere surprising.

    /// <summary>Assembly-/package-id shape: dot-separated segments of letters, digits, '_' and
    /// '-'. No path separators, no empty segments, so no "." / ".." / traversal by construction.</summary>
    private static readonly Regex IdentifierShape =
        new(@"^[A-Za-z0-9_-]+(\.[A-Za-z0-9_-]+)*$", RegexOptions.Compiled);

    /// <summary>SemVer-ish shape: numeric core (2–4 parts, matching what manifests carry) with
    /// optional pre-release / build-metadata tail. Path separators are impossible here.</summary>
    private static readonly Regex VersionShape =
        new(@"^\d+(\.\d+){1,3}(-[0-9A-Za-z.-]+)?(\+[0-9A-Za-z.-]+)?$", RegexOptions.Compiled);

    private static bool Invalid(string what, string? value, Regex shape, string constraint)
    {
        if (value is not null && shape.IsMatch(value))
            return false;
        Console.Error.WriteLine($"error: {what} '{value}' is invalid — {constraint}");
        return true;
    }

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
                  --min-mesh-version <v>      the platform FLOOR the module requires — the
                                              consumer's landing gate (API compatibility as a
                                              semver floor). Omit for no constraint; mirror the
                                              package's content.minMeshVersion when it declares one
                  --graph-dll <path>          the identity-anchor assembly the module was built
                                              against — MeshWeaver.Compiler.dll since #1707
                                              (default: <moduleOutputDir>/MeshWeaver.Compiler.dll;
                                              the flag name predates the anchor move). Its MVID is
                                              recorded as DIAGNOSTIC metadata — the exact build
                                              behind the bytes — never a gate; a missing DLL warns
                                              and records none
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
        string? minMeshVersion = null;
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
                case "--min-mesh-version" when i + 1 < args.Length:
                    minMeshVersion = args[++i];
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

        // Validate BEFORE composing a single path from these values (see the shapes above).
        const string identifierConstraint =
            "dot-separated segments of letters, digits, '_' and '-' (e.g. MeshWeaver.Social); "
            + "no path separators";
        const string versionConstraint =
            "a SemVer-shaped version (e.g. 1.2.0 or 1.2.0-rc1)";
        if (Invalid("--module-name", moduleName, IdentifierShape, identifierConstraint)
            || Invalid("--plugin", plugin, IdentifierShape, identifierConstraint)
            || Invalid("--package-version", packageVersion, VersionShape, versionConstraint)
            || (minMeshVersion is not null
                && Invalid("--min-mesh-version", minMeshVersion, VersionShape, versionConstraint)))
            return 2;

        var entryDll = Path.Combine(moduleDirectory, moduleName + ".dll");
        if (!File.Exists(entryDll))
        {
            Console.Error.WriteLine(
                $"error: entry assembly not found: {entryDll} — a module bundle without its entry "
                + "DLL could never load; check --module-name against the build output");
            return 2;
        }

        // The framework identity is DIAGNOSTIC metadata (which exact platform build produced these
        // bytes) — recorded when the restored identity-anchor DLL (MeshWeaver.Compiler.dll, #1707)
        // is at hand, warned-and-omitted when not. It is deliberately not required and never a
        // gate: the consumer lands on the minMeshVersion floor (API compatibility), and identity
        // equality stays with the NodeType bake lane. ReadIdentity (not ReadMvid) so a CI-built
        // anchor records its stamped commit identity — the value the runtime actually compares
        // (#1660 WS3).
        graphDll ??= Path.Combine(moduleDirectory, FrameworkIdentity.IdentityAssembly + ".dll");
        string? frameworkMvid = null;
        if (File.Exists(graphDll))
            frameworkMvid = FrameworkIdentity.ReadIdentity(graphDll);
        else
            Console.Error.WriteLine(
                $"warning: {FrameworkIdentity.IdentityAssembly}.dll not found at {graphDll} — the "
                + "bundle records no built-against framework MVID (diagnostic metadata only; the "
                + "landing gate is --min-mesh-version).");

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
                // Diagnostic: the exact platform build behind these bytes. The consumer's GATE is
                // the module section's minMeshVersion floor below.
                frameworkMvid,
                module = new { assemblyName = moduleName, assemblies = closure, minMeshVersion },
            },
            new JsonSerializerOptions
            {
                WriteIndented = true,
                DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
            });

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
            + $"{closure.Count} file(s), floor {minMeshVersion ?? "(none)"}, "
            + $"built-against MVID {frameworkMvid ?? "(unrecorded)"}");
        return 0;
    }
}
