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
/// <para><b>The consumer's LANDING gate is the <c>minMeshVersion</c> FLOOR, not an MVID.</b> A
/// module is a plain assembly binding by simple name; its contract is API compatibility, so the
/// bundle records the platform floor the module requires (absent = no constraint) and the consumer
/// lands anything whose floor it satisfies — one bundle serves every compatible platform build, and
/// nothing needs rebundling per CI build. MVID equality is still never a landing gate here.</para>
///
/// <para>🚨 <b>But the framework identity is REQUIRED, and a bundle that cannot state one is not
/// written at all</b> (#3211). It stopped being diagnostic the moment #3154 merged: a module's
/// version encodes its CONTENT only, so a rebuild of unchanged source against a new platform
/// republishes under the SAME version, and <c>ModuleUpdateDecision.Decide</c> now compares
/// <b>(version, framework identity)</b> to tell that rebuild from a no-op. A bundle stating no
/// identity puts every consumer of it permanently in the skip-and-say-so branch — the comparison
/// exists with nothing to compare, which is Plugins#931 arriving from the producing end. So the
/// identity of the anchor assembly (<c>MeshWeaver.Compiler.dll</c>, #1707) is resolved from
/// <c>--framework-mvid</c> or <c>--graph-dll</c>, and its absence is an exit-2 naming both, where
/// it used to be a warning and an omitted field.</para>
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
                                              the flag name predates the anchor move). Its stamped
                                              identity (else MVID) becomes the bundle's
                                              frameworkMvid
                  --framework-mvid <id>       state the built-against framework identity DIRECTLY,
                                              for a build whose anchor assembly is not in the
                                              output folder (the container/MeshWeaverRefs lanes:
                                              the platform is the IMAGE, so the anchor is in the
                                              extracted /app, not beside the module). Wins over
                                              --graph-dll when both are given.
                                              🚨 ONE of these two must yield an identity: a bundle
                                              that cannot state what it was built against is
                                              REFUSED (exit 2), never written with the field
                                              omitted — #3154 compares it on every consumer's
                                              update decision, and an unstated one can never be
                                              healed from the serving side (#3211)
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
        string? statedFrameworkMvid = null;
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
                case "--framework-mvid" when i + 1 < args.Length:
                    statedFrameworkMvid = args[++i].Trim();
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

        // ───────── THE BUILT-AGAINST FRAMEWORK IDENTITY — REQUIRED, and refused when absent ─────────
        // 🚨 #3211. This used to be diagnostic-and-optional: a missing anchor warned and the field
        // was omitted. #3154 made it an INPUT TO A DECISION on every installation — a module's
        // version encodes content only, so a rebuild of unchanged source against a new platform
        // republishes under the same version, and `ModuleUpdateDecision.Decide` now compares
        // (version, framework identity) to tell that rebuild from a no-op. A bundle that states no
        // identity parks every consumer of it in the skip-and-say-so branch FOREVER: unlike an
        // unknown on the landed side, which one fetch heals, an unstated SERVED identity can never
        // be healed downstream. So the blind spot is closed where it is created — a bundle that
        // cannot say what it was built against is not written at all.
        //
        // Two ways to state it, because the anchor is not always beside the module: on the
        // container / MeshWeaverRefs lanes the platform IS the image, so MeshWeaver.Compiler.dll
        // lives in the extracted /app rather than in the module's output (measured on every one of
        // MeshWeaver.Plugins' 34 bundles, 2026-09-03: "built-against MVID (unrecorded)", both the
        // sdk and the container path). ReadIdentity (not ReadMvid) so a CI-built anchor records its
        // stamped commit identity — the value the runtime actually compares (#1660 WS3).
        graphDll ??= Path.Combine(moduleDirectory, FrameworkIdentity.IdentityAssembly + ".dll");
        var frameworkMvid = statedFrameworkMvid;
        if (string.IsNullOrWhiteSpace(frameworkMvid) && File.Exists(graphDll))
            frameworkMvid = FrameworkIdentity.ReadIdentity(graphDll);
        if (string.IsNullOrWhiteSpace(frameworkMvid))
        {
            Console.Error.WriteLine(
                "error: the bundle would state no built-against framework identity, and a bundle "
                + "that cannot say which platform build produced its bytes is not publishable "
                + "(#3211). Every consumer compares it against what it has landed "
                + "(ModuleUpdateDecision, #3154), and an unstated one can never be healed from the "
                + "serving side — it makes every reconcile answer 'up to date, identity could not "
                + "be checked'. Provide ONE of: --framework-mvid <identity> (the platform's own "
                + "reading — use it when the platform is an IMAGE), or --graph-dll <path to "
                + $"{FrameworkIdentity.IdentityAssembly}.dll> for the platform this module was "
                + $"compiled against. Probed and not found: {graphDll}");
            return 2;
        }
        if (frameworkMvid.Any(char.IsWhiteSpace))
        {
            Console.Error.WriteLine(
                $"error: --framework-mvid '{frameworkMvid}' contains whitespace — the identity is a "
                + "single token (s<hash>, g<sha> or a 32-hex MVID), and a value that has to be "
                + "trimmed downstream is a value two readers can disagree about.");
            return 2;
        }

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
        // — the exclusions the retired node-package packer applied (PluginPacker, removed 2026-08-30).
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
                // The exact platform build behind these bytes — never null by the time we get here
                // (#3211 refuses above). The consumer's LANDING gate is still the module section's
                // minMeshVersion floor below; this is what its UPDATE decision compares (#3154).
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
            // "framework", not "MVID": an MVID is only ONE of the three shapes FrameworkBuildIdentity
            // resolves — a manifest-bearing host answers a surface identity `s<hash>`, a CI build
            // answers its stamped commit identity `g<sha>`, and the anchor's raw MVID is the last
            // fallback. A CI-packed bundle therefore almost never carries an MVID, and a log line
            // calling it one sends the next reader looking for the wrong string.
            + $"built against framework {frameworkMvid}");
        return 0;
    }
}
