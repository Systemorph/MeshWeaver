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

    /// <summary>
    /// Reads <c>content.includeSource</c> off the package's ROOT node (<c>index.json</c>) — the
    /// package's own declaration of whether its C# ships.
    ///
    /// <para>Read as JSON rather than through <c>PluginContent</c>: that type is defined by the
    /// Store package in the plugins repo and compiled ON A MESH, so this tool cannot reference it.
    /// One property by name is the whole coupling.</para>
    ///
    /// <para>Absent root, unreadable root, or absent property all mean FALSE. A publishing decision
    /// with an IP consequence must not be something a malformed file can turn on.</para>
    /// </summary>
    private static bool ReadIncludeSourceFromRoot(string contentDirectory)
    {
        var root = Path.Combine(contentDirectory, "index.json");
        if (!File.Exists(root))
            return false;
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllBytes(root));
            return document.RootElement.TryGetProperty("content", out var content)
                   && content.TryGetProperty("includeSource", out var flag)
                   && flag.ValueKind is JsonValueKind.True;
        }
        catch (JsonException)
        {
            return false;
        }
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
                  --content <dir>             the package's NODE DEFINITION tree (its index.json,
                                              NodeType nodes, markdown). Shipped verbatim so a
                                              consumer can use this package as an upstream WITHOUT
                                              cloning it. Omit for an assemblies-only bundle, which
                                              can stamp existing nodes but cannot stand in for the
                                              package.
                  --include-source [true|false]
                                              whether the C# SOURCE ships with the tree. DEFAULT
                                              FALSE, and the package decides: the root index.json's
                                              `content.includeSource`. This flag overrides it (for a
                                              tree with no root node). Source is needed only by a
                                              consumer that COMPILES against this package —
                                              `shared=@<pkg>/…` pulls these files into ITS
                                              compilation. A consumer that merely installs and runs
                                              needs the assemblies, which the bundle already carries.
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
                  --own-platform <names>      semicolon-separated MeshWeaver.* assemblies that are
                                              MODULE-OWNED (their source lives in the module's own
                                              repo, not the platform) — the deps-closure walk
                                              bundles them instead of stopping: they are nowhere
                                              in /app, so a stop would ship a module that faults
                                              on its first sibling (Import's DataSetReader family)
                  --deps-closure              derive the module's PRIVATE dependency closure from
                                              <name>.deps.json beside the entry DLL and bundle it:
                                              assemblies reachable from the module's own package
                                              references and NOT from its MeshWeaver.* references
                                              (those ship in the consumer's /app). Requires the
                                              module to be built with
                                              CopyLocalLockFileAssemblies=true so the files are in
                                              the output folder.
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
        var ownPlatform = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var depsClosure = false;
        var outputDirectory = Environment.CurrentDirectory;

        string? contentDirectory = null;
        bool? includeSourceOverride = null;

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
                case "--own-platform" when i + 1 < args.Length:
                    ownPlatform.UnionWith(args[++i]
                        .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
                    break;
                case "--deps-closure":
                    depsClosure = true;
                    break;
                case "--out" when i + 1 < args.Length:
                    outputDirectory = Path.GetFullPath(args[++i]);
                    break;
                case "--content" when i + 1 < args.Length:
                    contentDirectory = Path.GetFullPath(args[++i]);
                    break;
                // Bare `--include-source` means true; an explicit `--include-source false` wins.
                // Only CONSUMED as a value when it parses as a bool, so a following option is not
                // swallowed.
                case "--include-source":
                    if (i + 1 < args.Length && bool.TryParse(args[i + 1], out var explicitInclude))
                    {
                        includeSourceOverride = explicitInclude;
                        i++;
                    }
                    else
                    {
                        includeSourceOverride = true;
                    }

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

        // The closure: entry DLL (+ symbols when present) + the derived private dependency
        // closure when asked for + exactly the files the caller named.
        var closure = new List<string> { moduleName + ".dll" };
        var entryPdb = moduleName + ".pdb";
        if (File.Exists(Path.Combine(moduleDirectory, entryPdb)))
            closure.Add(entryPdb);

        // 🚨 --deps-closure: the module's own package dependencies ride WITH it. Entry-DLL-only
        // bundles landed modules that faulted at first use on their first private dependency
        // (Microsoft.Extensions.AI.OpenAI, Microsoft.Graph — the 2026-08-19/20 memex outage);
        // hand-naming them with --with is whack-a-mole across every module. Derived from the
        // build's own deps.json — see DepsClosure for the platform/own split.
        if (depsClosure)
        {
            var depsPath = Path.Combine(moduleDirectory, moduleName + ".deps.json");
            if (!File.Exists(depsPath))
            {
                Console.Error.WriteLine(
                    $"error: --deps-closure needs {depsPath} — build the module project (the SDK "
                    + "emits it beside the entry DLL)");
                return 2;
            }
            DepsClosure.Result derived;
            try
            {
                derived = DepsClosure.Derive(File.ReadAllText(depsPath), moduleName, ownPlatform);
            }
            catch (Exception e) when (e is InvalidDataException or JsonException)
            {
                Console.Error.WriteLine($"error: --deps-closure could not read {depsPath}: {e.Message}");
                return 2;
            }
            foreach (var warning in derived.Warnings)
                Console.Error.WriteLine($"warning: {warning}");
            var present = derived.Files
                .Where(f => File.Exists(Path.Combine(moduleDirectory, f)))
                .ToList();
            var missing = derived.Files.Except(present, StringComparer.OrdinalIgnoreCase).ToList();

            // A derived file ABSENT from a folder that materializes package assets was
            // FRAMEWORK-TRIMMED: the SDK resolved that package to the shared framework
            // (Microsoft.Extensions.* riding in Microsoft.AspNetCore.App), so the consumer's
            // runtime provides it and the bundle need not. Skipped with a line each, never
            // silently. The one case that must STAY an error is a folder that never had package
            // assets at all — a plain classlib build output — where skipping would pack the
            // entry-only bundle this flag exists to abolish. The witness is the PACKAGE UNIVERSE
            // (every non-MeshWeaver package runtime file in the whole graph, platform-reachable
            // ones included): a publish folder contains some of it even when the module's OWN
            // dependencies are entirely framework-trimmed, because the MeshWeaver references drag
            // their package deps in — so an empty intersection cleanly means "wrong folder",
            // never "everything was framework-resolved". (Checking only the module's own derived
            // set got this wrong both ways on CI, 2026-08-20.)
            if (missing.Count > 0
                && !derived.PackageUniverse.Any(f => File.Exists(Path.Combine(moduleDirectory, f))))
            {
                Console.Error.WriteLine(
                    "error: --deps-closure derived files and the folder holds NO package assets at "
                    + "all — pack from `dotnet publish` output (or a CopyLocalLockFileAssemblies "
                    + "build), not a plain build folder. "
                    + $"Missing: {string.Join(", ", missing)}");
                return 2;
            }
            foreach (var file in missing)
                Console.WriteLine($"deps-closure: excluded (framework-resolved): {file}");
            foreach (var file in present)
                if (!closure.Contains(file, StringComparer.OrdinalIgnoreCase))
                    closure.Add(file);
            Console.WriteLine(
                $"deps-closure: bundling {present.Count} private dependency file(s); "
                + $"excluded {derived.ExcludedPlatformCarried.Count} platform-carried, "
                + $"{missing.Count} framework-resolved");
        }

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

        // The package tree, walked in a stable order so two packs of the same tree produce
        // byte-identical manifests. obj/bin are a developer's build residue, never package content
        // — the same exclusions PluginPacker applies.
        // 🚨 STATIC WEB ASSETS ride the bundle. A view pack's CSS/JS reaches the browser through
        // the host's build-time static-web-assets graph — i.e. through the ProjectReference the
        // module lane removes — so without this a landed view pack renders unstyled and its
        // collocated JS 404s. A standalone RCL publish lays them under wwwroot/ (its own assets at
        // the root, dependencies' already namespaced under wwwroot/_content/<Dep>/), which is the
        // exact shape MeshModuleStaticAssetExtensions serves from modules/<Name>/wwwroot.
        var assetRoot = Path.Combine(moduleDirectory, "wwwroot");
        var staticAssets = Directory.Exists(assetRoot)
            ? Directory.EnumerateFiles(assetRoot, "*", SearchOption.AllDirectories)
                .Select(f => "wwwroot/" + Path.GetRelativePath(assetRoot, f)
                    .Replace(Path.DirectorySeparatorChar, '/'))
                .OrderBy(r => r, StringComparer.Ordinal)
                .ToList()
            : [];

        var contentFiles = new List<string>();
        var includeSource = false;
        if (contentDirectory is not null)
        {
            if (!Directory.Exists(contentDirectory))
            {
                Console.Error.WriteLine($"error: content directory not found: {contentDirectory}");
                return 2;
            }
            // 🚨 THE PACKAGE DECIDES, and the default is NOT to ship source. Source is the one
            // part of a package that a consumer does not need in order to USE it — the compiled
            // assemblies are right here in the same bundle — and shipping it is therefore a
            // deliberate act, not a side effect of publishing. Defaulting the other way would have
            // every package's C# leave the building the first time anyone packed it with a tree.
            includeSource = includeSourceOverride ?? ReadIncludeSourceFromRoot(contentDirectory);

            contentFiles.AddRange(Directory
                .EnumerateFiles(contentDirectory, "*", SearchOption.AllDirectories)
                .Select(f => Path.GetRelativePath(contentDirectory, f)
                    .Replace(Path.DirectorySeparatorChar, '/'))
                .Where(r => !r.StartsWith("obj/", StringComparison.Ordinal)
                            && !r.StartsWith("bin/", StringComparison.Ordinal)
                            && !r.StartsWith(".worktrees/", StringComparison.Ordinal))
                // A .cs file IS a Code node in this repo's node-per-file layout, so withholding
                // source and withholding those nodes are the same operation.
                .Where(r => includeSource || !r.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
                .OrderBy(r => r, StringComparer.Ordinal));

            if (contentFiles.Count == 0)
            {
                // Silence here would ship a bundle that LOOKS complete and cannot be consumed.
                Console.Error.WriteLine(
                    $"error: --content {contentDirectory} contains no files to ship"
                    + (includeSource ? "" : " (source is excluded — set content.includeSource"
                                             + " on the package root, or pass --include-source)"));
                return 2;
            }
        }

        var manifestJson = JsonSerializer.Serialize(
            new
            {
                plugin,
                version = packageVersion,
                // Diagnostic: the exact platform build behind these bytes. The consumer's GATE is
                // the module section's minMeshVersion floor below.
                frameworkMvid,
                module = new { assemblyName = moduleName, assemblies = closure, minMeshVersion,
                    staticAssets = staticAssets.Count > 0 ? staticAssets : null,
                },
                // DECLARED, so BundleReader.ReadContent stays manifest-driven — these files are
                // written into a consumer's working tree, and a glob would recreate anything a
                // future producer happens to drop in the folder.
                content = contentFiles.Count > 0 ? contentFiles : null,
                // DECLARED, never inferred from the file list: a consumer that cannot resolve a
                // shared=@ include must be able to tell "withheld" (its build cannot succeed) from
                // "this package has no C#" (nothing is wrong).
                sourceIncluded = contentFiles.Count > 0 ? includeSource : (bool?)null,
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

        entries.AddRange(staticAssets.Select(relative =>
        {
            var path = Path.Combine(moduleDirectory, relative.Replace('/', Path.DirectorySeparatorChar));
            return new NuGetPackageWriter.Entry(
                NuGetPackageWriter.ModuleAssetEntryPathFor(relative), () => File.OpenRead(path));
        }));

        entries.AddRange(contentFiles.Select(relative =>
        {
            var path = Path.Combine(contentDirectory!, relative.Replace('/', Path.DirectorySeparatorChar));
            return new NuGetPackageWriter.Entry(
                $"{NuGetPackageWriter.ContentFolder}/{relative}", () => File.OpenRead(path));
        }));

        Directory.CreateDirectory(outputDirectory);
        var bundlePath = Path.Combine(
            outputDirectory, $"{manifest.PackageId}.{packageVersion}.module.nupkg");
        if (File.Exists(bundlePath))
            File.Delete(bundlePath);
        using (var output = File.Create(bundlePath))
            NuGetPackageWriter.Write(output, manifest, packageVersion, entries, manifestJson);

        Console.WriteLine(
            $"packed {Path.GetFileName(bundlePath)} — module {moduleName}, "
            + $"{closure.Count} file(s), {contentFiles.Count} node file(s) "
            + $"({(includeSource ? "with" : "WITHOUT")} source), "
            + $"floor {minMeshVersion ?? "(none)"}, "
            + $"built-against MVID {frameworkMvid ?? "(unrecorded)"}");
        return 0;
    }
}
