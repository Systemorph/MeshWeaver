using System.Globalization;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Text.Json;
using MeshWeaver.PluginCatalog;
using MeshWeaver.PluginTester;

using MeshWeaver.Compiler;
using MeshWeaver.Messaging;
// mw-plugin-test <repo-root> [--compile-timeout <seconds>] [--render-timeout <seconds>]
//                            [--allow <file>] [--report <file>] [--seed <dir>]
//                            [--bake-output <dir>] [--source-sha <sha>] [--module <dll>]...
//
// The MeshWeaver.Plugins PR gate: imports each node-repo package of the checkout into a fresh
// in-process mesh, waits for every NodeType to compile (Roslyn diagnostics on error), renders
// each type's default area, and EXECUTES each type's `Tests` layout area. Exit 0 = all green.
// --allow names a known-debt file (the compile-check.allow ratchet): listed failures are
// tolerated, new failures fail, and an entry whose check now passes is stale and fails.
// --report writes the structured GateRunReport JSON (the combo verifier's wire contract) —
// written for every completed run, red or green, BEFORE the allowlist verdict is applied.
// --bake-output persists the run's compiled assemblies as prebuilt-assembly bundles (one
// <package>.zip per package + framework-mvid.txt) into <dir> — the artifact half of #1660 WS1:
// the same compile that proves the content produces the bytes a consumer loads instead of
// re-compiling. --source-sha records the synced commit in each bundle manifest.
// --seed CONSUMES a bake instead of producing one (#1763): the bundles a `compile` run wrote are
// adopted for every NodeType the gate installs, so what the gate renders and runs `Tests` on is
// the assembly that ships. The directory is read and ADDRESS-CHECKED before the mesh boots, and
// the run goes red if the bake was declared but not consumed — a gate that silently compiled the
// tree itself passes identically to one that consumed the bake, so the shortfall has to be a
// verdict rather than something nobody can observe.
//
// The one Task bridge lives HERE, at the console boundary — everything below Run() is reactive.

// 🚨 THE `compile` VERB — the compiler-driven bake (#1763). It resolves NodeType sources straight
// from the git tree, compiles them with MeshWeaver.Compiler and emits DLL + PDB into the existing
// bundle format. There is NO MeshBuilder, NO AddGraph(), NO content import and NO hub anywhere in
// its path: producing an assembly is a build step, and the mesh's job is to CONSUME a bake.
//
//     mw-compiler compile <checkout-root> --output <dir> [--allow <file>] [--source-sha <sha>]
//
// Everything BELOW this block is the GATE (`mw-plugin-test <root>`), which legitimately stands up a
// mesh because rendering a layout area and executing a `Tests` area are runtime behaviours. The two
// used to be one code path wearing two names, which is how the mesh-driven bake stayed invisible.
//
// 🚨 NOTHING MAY THROW OUT OF `Main` — an escaping exception does not end this process, it SPINS it
// (#1741). Every consumer runs this binary as a container's PID 1 (`docker run … --entrypoint
// /app/mw-plugin-test`). The runtime's unhandled-exception path prints the trace and then calls
// `abort()`, which `raise()`s SIGABRT at the process — but a PID-namespace init with SIG_DFL for
// that signal is `SIGNAL_UNKILLABLE`, so the kernel DISCARDS it (kernel/signal.c,
// `sig_task_ignored`). `raise()` returns, `abort()` falls through to its `ABORT_INSTRUCTION` trap,
// the runtime's own SIGTRAP handler is still installed and returns to the very instruction that
// trapped — and the main thread re-traps forever. Measured 2026-08-17: two containers "Up" for 36
// and 57 minutes at ~100% CPU each, having printed one FileNotFoundException in their first second;
// the same image run with `--init` (so the tool is PID 2) exits 134 immediately.
//
// So every failure below turns into a MESSAGE and an EXIT CODE. This guard is the backstop for the
// ones nobody anticipated — it prints the whole exception, so nothing is hidden, and returns
// non-zero so the container dies instead of burning a CI runner until the job timeout fires and
// reports a "hang" rather than a bad argument. (The consumers ALSO pass `docker run --init` now,
// which covers the crashes a `catch` cannot reach — a stack overflow, an OOM abort, an unhandled
// throw on a background thread.)
try
{
    if (args.Length > 0 && args[0] == "compile")
        return RunCompile(args[1..]);
    if (args.Length > 0 && args[0] == CascadeBuild.Verb)
        return await RunBuild(args[1..]);
    if (args.Length > 0 && args[0] == ProjectBuild.Verb)
        return await RunBuildProject(args[1..]);
    if (args.Length > 0 && args[0] == "framework-identity")
        return RunFrameworkIdentity(args[1..]);
    return await RunGate(args);
}
catch (Exception ex)
{
    Console.Error.WriteLine($"mw-plugin-test: FATAL — {ex}");
    return 70; // EX_SOFTWARE: distinct from 0 (green), 1 (gate red) and 2 (bad usage).
}

static async Task<int> RunBuild(string[] args)
{
    // build <repo-root> [<package>… | all] [--module <dll>]… [--out <dir>] [--report <file>]
    //       [--max-parallel <n>] [--case-timeout <s>] [--no-tests] [--source-sha <sha>]
    // The new build process (2026-08-30): compile + run tests per package, as a dependency
    // cascade, from the checkout on disk, against this image's /app — no mesh import anywhere.
    string? root = null;
    var packages = new List<string>();
    var modules = new List<string>();
    string? outDir = null;
    string? reportPath = null;
    string? sourceSha = null;
    var maxParallel = Math.Max(1, Environment.ProcessorCount);
    var caseTimeout = TimeSpan.FromSeconds(60);
    var runTests = true;
    for (var i = 0; i < args.Length; i++)
    {
        switch (args[i])
        {
            case "--module" when i + 1 < args.Length:
                modules.Add(args[++i]);
                break;
            case "--out" when i + 1 < args.Length:
                outDir = args[++i];
                break;
            case "--report" when i + 1 < args.Length:
                reportPath = args[++i];
                break;
            case "--source-sha" when i + 1 < args.Length:
                sourceSha = args[++i];
                break;
            case "--max-parallel" when i + 1 < args.Length:
                if (!int.TryParse(args[++i], NumberStyles.Integer, CultureInfo.InvariantCulture, out maxParallel) || maxParallel < 1)
                {
                    Console.Error.WriteLine("mw-plugin-test build: --max-parallel takes a positive integer");
                    return 2;
                }
                break;
            case "--case-timeout" when i + 1 < args.Length:
                if (!double.TryParse(args[++i], NumberStyles.Float, CultureInfo.InvariantCulture, out var s) || s <= 0)
                {
                    Console.Error.WriteLine("mw-plugin-test build: --case-timeout takes seconds > 0");
                    return 2;
                }
                caseTimeout = TimeSpan.FromSeconds(s);
                break;
            case "--no-tests":
                runTests = false;
                break;
            case "-h" or "--help":
                Console.WriteLine(BuildUsage());
                return 0;
            case var flag when flag.StartsWith("--", StringComparison.Ordinal):
                Console.Error.WriteLine($"mw-plugin-test build: unknown option {flag}");
                Console.Error.WriteLine(BuildUsage());
                return 2;
            default:
                if (root is null)
                    root = args[i];
                else
                    packages.Add(args[i]);
                break;
        }
    }
    if (root is null)
    {
        Console.Error.WriteLine(BuildUsage());
        return 2;
    }
    var report = await CascadeBuild.Run(new CascadeBuild.Options
    {
        RepoRoot = root,
        Packages = packages,
        ModuleAssemblyPaths = modules,
        OutputDirectory = outDir,
        ReportPath = reportPath,
        MaxParallel = maxParallel,
        CaseTimeout = caseTimeout,
        RunTests = runTests,
        SourceSha = sourceSha,
    }).Await(CancellationToken.None);
    return report.ExitCode;
}

// 🚨 THE `build-project` VERB — compile a .csproj with NO dotnet SDK and NO NuGet restore, against
// the assemblies of the container this process runs in (maintainer, 2026-08-30: "the platform builds
// dll completely without any external dotnet kit or nuget"). The evaluator, the reference set and
// the cascade live in ProjectFile / ContainerReferenceSet / ProjectBuild; this is only the argv.
static async Task<int> RunBuildProject(string[] args)
{
    // build-project <csproj|dir> [--output <dir>] [--app <dir>] [--extra-refs <dir>]...
    //               [--accept <construct>]... [--allow-warnings | --no-warn=false] [--max-parallel <n>]
    string? entry = null;
    string? outDir = null;
    var app = ContainerReferenceSet.DefaultAppDirectory;
    var extraRefs = new List<string>();
    var generatorPaths = new List<string>();
    string? razorGenerators = null;
    var accept = new List<string>();
    var allowWarnings = false;
    var maxParallel = Math.Max(1, Environment.ProcessorCount);
    for (var i = 0; i < args.Length; i++)
    {
        switch (args[i])
        {
            case "--output" or "-o" when i + 1 < args.Length:
                outDir = args[++i];
                break;
            case "--app" when i + 1 < args.Length:
                app = args[++i];
                break;
            case "--extra-refs" when i + 1 < args.Length:
                extraRefs.Add(args[++i]);
                break;
            case "--generators" when i + 1 < args.Length:
                generatorPaths.Add(args[++i]);
                break;
            // The RAZOR generator is found automatically beside the builder (the image ships it in
            // razor-generators/); this names a different copy — a newer SDK's, or a mount.
            case "--razor-generators" when i + 1 < args.Length:
                razorGenerators = args[++i];
                break;
            case "--accept" when i + 1 < args.Length:
                accept.Add(args[++i]);
                break;
            // The no-warn policy is ON by default; both spellings of the opt-out are accepted
            // because both are documented, and a flag that silently means nothing is worse than a
            // flag that does not exist.
            case "--allow-warnings" or "--no-warn=false" or "--no-warn=False":
                allowWarnings = true;
                break;
            case "--no-warn" or "--no-warn=true":
                allowWarnings = false;
                break;
            case "--max-parallel" when i + 1 < args.Length:
                if (!int.TryParse(args[++i], NumberStyles.Integer, CultureInfo.InvariantCulture, out maxParallel)
                    || maxParallel < 1)
                {
                    Console.Error.WriteLine("mw-plugin-test build-project: --max-parallel takes a positive integer");
                    return 2;
                }
                break;
            case "-h" or "--help":
                Console.WriteLine(BuildProjectUsage());
                return 0;
            case var flag when flag.StartsWith("--", StringComparison.Ordinal):
                Console.Error.WriteLine($"mw-plugin-test build-project: unknown option {flag}");
                Console.Error.WriteLine(BuildProjectUsage());
                return 2;
            default:
                if (entry is not null)
                {
                    Console.Error.WriteLine(
                        $"mw-plugin-test build-project: one project at a time — got '{entry}' and '{args[i]}'.");
                    return 2;
                }
                entry = args[i];
                break;
        }
    }
    if (entry is null)
    {
        Console.Error.WriteLine(BuildProjectUsage());
        return 2;
    }
    var projectReport = await ProjectBuild.Run(new ProjectBuild.Options
    {
        EntryProject = entry,
        OutputDirectory = outDir,
        AppDirectory = app,
        ExtraReferenceDirectories = extraRefs,
        GeneratorPaths = generatorPaths,
        RazorGeneratorDirectory = razorGenerators,
        Accept = accept,
        AllowWarnings = allowWarnings,
        MaxParallel = maxParallel,
    }).Await(CancellationToken.None);
    return projectReport.ExitCode;
}

static string BuildProjectUsage() =>
    "usage: mw-plugin-test build-project <csproj|dir> [--output <dir>] [--app <dir>] "
    + "[--extra-refs <dir>]... [--generators <dir|dll>]... [--razor-generators <dir>] "
    + "[--accept <construct>]... [--allow-warnings] [--max-parallel <n>]\n"
    + "  Compiles a .NET project with NO dotnet SDK and NO NuGet restore: the .csproj is evaluated "
    + "without MSBuild, every reference is resolved from this container's /app (and its .deps.json), "
    + "ProjectReferences inside the source root are built first in dependency order, and Roslyn runs "
    + "with the SDK's diagnostic standard (nullable analysis, DocumentationMode.Diagnose). Warnings "
    + "fail the build unless --allow-warnings. A construct the evaluator cannot reproduce fails the "
    + "run by name; --accept <construct> acknowledges one. .razor/.cshtml are compiled by the Razor "
    + "source generator the image ships in razor-generators/ beside this builder; --razor-generators "
    + "<dir> names another copy, and a project with Razor files and no generator FAILS rather than "
    + "quietly emitting an assembly with no components in it.";

static string BuildUsage() =>
    "usage: mw-plugin-test build <repo-root> [<package>... | all] [--module <dll>]... [--out <dir>] "
    + "[--report <file>] [--max-parallel <n>] [--case-timeout <s>] [--no-tests] [--source-sha <sha>]\n"
    + "  Compiles AND tests each selected package (plus its in-repo requirements) as a dependency "
    + "cascade: a package starts when its dependencies are green, is blocked when one is red. "
    + "'all' (default) rebuilds everything. Sources are read from disk; nothing is imported into a mesh.";

static int RunCompile(string[] args)
{
    string? compileRoot = null;
    string? outputDirectory = null;
    string? compileSourceSha = null;
    var compileAllow = GateAllowlist.Empty;
    var compileAllowApplied = false;
    var compileModules = new List<string>();
    for (var i = 0; i < args.Length; i++)
    {
        switch (args[i])
        {
            case "--output" when i + 1 < args.Length:
                outputDirectory = args[++i];
                break;
            case "--source-sha" when i + 1 < args.Length:
                compileSourceSha = args[++i];
                break;
            // 🚨 THE SAME RATCHET THE GATE READS. Splitting the bake out of the gate must not
            // split the VERDICT: a known-debt compile failure the gate tolerates has to be
            // tolerated here too, or the same ratchet file means two different things depending on
            // which half of the lane is looking at it — and the first legitimate allow entry
            // would break the bake as though the bake had regressed. Only `compile` entries can
            // apply here; render / tests / install / idempotence are runtime checks the bake does
            // not perform, and an entry for one of those is NOT reported stale by this verb
            // (the gate that does perform them owns that judgement).
            case "--allow" when i + 1 < args.Length:
            {
                var compileAllowPath = args[++i];
                if (!File.Exists(compileAllowPath))
                {
                    Console.Error.WriteLine(GateAllowlist.MissingFileMessage(compileAllowPath));
                    return 2;
                }
                try
                {
                    compileAllow = GateAllowlist.Load(compileAllowPath);
                }
                catch (FormatException ex)
                {
                    Console.Error.WriteLine(
                        $"compile: --allow file '{GateAllowlist.Describe(compileAllowPath)}' is "
                        + $"malformed — {ex.Message}");
                    return 2;
                }
                compileAllowApplied = true;
                break;
            }
            // 🚨 THE SAME MODULE SET THE GATE LOADS — and for a stronger reason here. The gate
            // needs a module to ACTIVATE its node types; the bake needs it to COMPILE against,
            // because a module under modules/<name>/ is absent from TRUSTED_PLATFORM_ASSEMBLIES.
            // Omit it and the bake resolves fewer types than the portal that consumes the bundles,
            // then reports the shortfall as content errors (#2563). Repeatable, validated here for
            // the same reason the gate validates it: a bake that quietly ran without a module it
            // was told to load blames the CONTENT for a missing reference.
            case "--module" when i + 1 < args.Length:
            {
                var compileModulePath = Path.GetFullPath(args[++i]);
                if (!File.Exists(compileModulePath))
                {
                    Console.Error.WriteLine(
                        $"compile: --module '{compileModulePath}' does not exist. Pass the module's "
                        + "ENTRY assembly (…/<Name>/<Name>.dll) and make sure it is mounted into "
                        + "the container.");
                    return 2;
                }
                compileModules.Add(compileModulePath);
                break;
            }
            case "--output" or "--source-sha" or "--allow" or "--module":
                Console.Error.WriteLine($"Option '{args[i]}' requires a value.");
                return 2;
            case "--help" or "-h":
                Console.WriteLine(
                    "usage: mw-compiler compile <checkout-root> --output <dir> [--allow <file>] "
                    + "[--source-sha <sha>] [--module <dll>]...");
                return 0;
            default:
                if (args[i].StartsWith('-') || compileRoot is not null)
                {
                    Console.Error.WriteLine($"Unknown argument '{args[i]}'. Try --help.");
                    return 2;
                }
                compileRoot = args[i];
                break;
        }
    }
    if (outputDirectory is null)
    {
        Console.Error.WriteLine(
            "compile: --output <dir> is required (the directory the bundles are written into).");
        return 2;
    }

    compileRoot ??= ".";
    Console.WriteLine(
        $"mw-compiler compile: baking node repos under '{Path.GetFullPath(compileRoot)}' "
        + $"→ '{Path.GetFullPath(outputDirectory)}' (no mesh)");
    var bake = TreeBake.Run(new TreeBake.Options
    {
        RepoRoot = compileRoot,
        OutputDirectory = outputDirectory,
        SourceSha = compileSourceSha,
        // Resolved HERE, at the CLI boundary: TesterModules is the one list the gate reads too, and
        // resolution names MeshBuilder — which MeshFreeBakePathTest forbids anywhere the bake can
        // reach. The bake gets paths; it never meets a mesh type.
        ModuleAssemblyPaths = TesterModules.ResolvedPaths(compileModules),
    });
    if (bake.FatalError is not null)
        Console.Error.WriteLine($"compile: FATAL — {bake.FatalError}");
    var newFailures = 0;
    var knownDebt = 0;
    foreach (var failed in bake.Types.Where(t => !t.Success))
    {
        if (compileAllow.Allows(failed.NodePath, "compile"))
        {
            knownDebt++;
            Console.Error.WriteLine(
                $"compile: RED [known-debt] {failed.NodePath} — {failed.Error}");
            continue;
        }
        newFailures++;
        Console.Error.WriteLine($"compile: RED {failed.NodePath} — {failed.Error}");
    }
    // A STALE entry fails, exactly as it does in the gate: the list may only shrink, and an entry
    // whose type now compiles is a line to delete rather than debt to carry.
    var stale = compileAllow.Entries
        .Where(e => string.Equals(e.Check, "compile", StringComparison.OrdinalIgnoreCase))
        .Where(e => bake.Types.Any(t => t.Success
            && string.Equals(t.NodePath, e.Scope, StringComparison.OrdinalIgnoreCase)))
        .ToList();
    foreach (var entry in stale)
        Console.Error.WriteLine(
            $"compile: STALE allow entry (now compiles — remove it): {entry}");
    Console.WriteLine(
        $"compile: {bake.Types.Count(t => t.Success)}/{bake.Types.Length} NodeType(s) compiled, "
        + $"{bake.Bundles.Length} bundle(s), framework={bake.FrameworkIdentity}"
        + (compileAllowApplied
            ? $" — {knownDebt} known-debt failure(s) allowed, {newFailures} new, "
              + $"{stale.Count} stale allow entr(ies)"
            : string.Empty));
    if (!compileAllowApplied)
        return bake.ExitCode;
    return bake.FatalError is null && newFailures == 0 && stale.Count == 0 ? 0 : 1;
}

// 🚨 THE `framework-identity` VERB — the ADDRESS CHECK (#1814).
//
//     mw-plugin-test framework-identity <app-dir> [--expect <identity>]
//
// Prints the framework build identity a host whose binaries live in <app-dir> resolves, reading
// that directory's meshweaver-surface.manifest and assemblies as FILES — nothing is loaded, so one
// container can answer the question for another image's /app.
//
// The identity is an ADDRESS: a bake publishes its bundles under the identity ITS host resolves and
// a portal only ever looks under the identity IT resolves. Before this verb nothing in the pipeline
// could observe that the two disagree — CD run 32063444385 baked release 3.0.0-rc4.ci.4201 under
// `sda0843abd6db4fc7e37cc3f838079265` while both prod pods asked for
// `s944d7fd0bbf81f4b40b85a7a74296263`, the bundles sat intact on the shared volume under an address
// nobody read, the bake job reported SUCCESS, and the first pod of every deploy recompiled 269
// types (10 m 29 s, +1598 MB working set) as though no bake had ever run. `--expect` turns that
// into a red step: it compares and, on a mismatch, names the canonical assemblies each side's
// manifest is missing — the actual defect in #1814 was eight of them, not a hash that "just
// differs".
//
// 🚨 SAME ARCHITECTURE ONLY. Reference-assembly bytes differ between the amd64 and arm64 legs of one
// multi-arch image, so an image carries two identities; extract both directories for ONE platform
// (CI pins --platform linux/amd64). That per-arch split is the second, independent way to mint an
// address nobody reads: memex.localhost is arm64 while the CI bake publishes linux/amd64.
static int RunFrameworkIdentity(string[] args)
{
    string? appDirectory = null;
    string? expected = null;
    for (var i = 0; i < args.Length; i++)
    {
        switch (args[i])
        {
            case "--expect" when i + 1 < args.Length:
                expected = args[++i].Trim();
                break;
            case "--expect":
                Console.Error.WriteLine($"Option '{args[i]}' requires a value.");
                return 2;
            case "--help" or "-h":
                Console.WriteLine(
                    "usage: mw-plugin-test framework-identity <app-dir> [--expect <identity>]");
                return 0;
            default:
                if (args[i].StartsWith('-') || appDirectory is not null)
                {
                    Console.Error.WriteLine($"Unknown argument '{args[i]}'. Try --help.");
                    return 2;
                }
                appDirectory = args[i];
                break;
        }
    }
    if (appDirectory is null)
    {
        Console.Error.WriteLine(
            "framework-identity: <app-dir> is required (the host's application directory — the one "
            + "holding meshweaver-surface.manifest beside its assemblies; a container's /app).");
        return 2;
    }

    var full = Path.GetFullPath(appDirectory);
    var (identity, problem) = FrameworkBuildIdentity.ResolveIdentityForDirectory(full);
    if (identity is null)
    {
        // 🚨 Never degrade to a fallback identity here. Two manifest-less directories built from the
        // same commit resolve the SAME fallback, so a comparison over them would report a match
        // having verified nothing — the "verification step that cannot fail" shape.
        Console.Error.WriteLine($"framework-identity: cannot resolve an identity for '{full}' — {problem}");
        return 1;
    }

    Console.WriteLine(identity);
    if (expected is null || expected.Length == 0)
        return 0;
    if (string.Equals(expected, identity, StringComparison.Ordinal))
    {
        Console.Error.WriteLine(
            $"framework-identity: MATCH — '{full}' resolves {identity}, the identity the bake "
            + "published under. Its bundles are addressed to this host.");
        return 0;
    }

    var pairs = FrameworkBuildIdentity.ParseSurfaceManifest(
        File.ReadAllText(Path.Combine(full, FrameworkBuildIdentity.SurfaceManifestFileName)));
    var absent = FrameworkBuildIdentity.CanonicalAssembliesAbsentFrom(pairs);
    Console.Error.WriteLine(
        $"framework-identity: MISMATCH — the bake published under '{expected}' but '{full}' "
        + $"resolves '{identity}'. Bundles published under an identity this host never asks for are "
        + "INERT: the pods compile everything at boot exactly as if no bake had run, and nothing "
        + "else in the pipeline reports it (issue #1814).");
    Console.Error.WriteLine(
        absent.Length == 0
            ? "  Both manifests record every canonical content-surface assembly, so the difference is "
              + "in the recorded HASHES — different binaries (a different architecture, or hosts built "
              + "from different commits). Check that both were taken with the same --platform and from "
              + "the same release."
            : "  This host's surface manifest does not record these canonical content-surface "
              + $"assemblies, so each hashes as '{FrameworkBuildIdentity.AbsentMarker}' here while the "
              + "bake host records a real surface hash for it:\n    - "
              + string.Join("\n    - ", absent)
              + "\n  A canonical assembly leaves a host's manifest when it leaves that host's COMPILE "
              + "reference graph (@(ReferencePathWithRefAssemblies)) — e.g. a ProjectReference removed "
              + "in favour of a runtime module lane. Either give this host the compile reference back "
              + "(Private=\"false\" keeps the bits out of its app closure) or remove the assembly from "
              + "FrameworkBuildIdentity.ContentSurfaceAssemblies — never leave the two hosts disagreeing.");
    return 1;
}

// The GATE verb (`mw-plugin-test <repo-root>`). A LOCAL FUNCTION rather than bare top-level
// statements so the guard above can wrap it: top-level statements are the body of `Main` itself,
// and anything thrown from them escapes the process (see the #1741 note above).
static async Task<int> RunGate(string[] args)
{
    string? root = null;
    var compileTimeout = TimeSpan.FromMinutes(5);
    var renderTimeout = TimeSpan.FromMinutes(2);
    var allowlist = GateAllowlist.Empty;
    var allowApplied = false;
    string? reportPath = null;
    string? bakeOutput = null;
    string? sourceSha = null;
    BakeSeed? seed = null;
    var externalModules = new List<string>();

    for (var i = 0; i < args.Length; i++)
    {
        switch (args[i])
        {
            case "--compile-timeout" when i + 1 < args.Length:
                compileTimeout = TimeSpan.FromSeconds(
                    double.Parse(args[++i], CultureInfo.InvariantCulture));
                break;
            case "--render-timeout" when i + 1 < args.Length:
                renderTimeout = TimeSpan.FromSeconds(
                    double.Parse(args[++i], CultureInfo.InvariantCulture));
                break;
            // A ratchet the gate cannot read is a CONFIGURATION error, and it is refused here —
            // before a mesh is built — with the flag, the resolved path and the two honest
            // spellings of "no known debt". See GateAllowlist.MissingFileMessage for why a missing
            // file is NOT silently read as an empty list even though that is the stricter verdict.
            case "--allow" when i + 1 < args.Length:
            {
                var allowPath = args[++i];
                if (!File.Exists(allowPath))
                {
                    Console.Error.WriteLine(GateAllowlist.MissingFileMessage(allowPath));
                    return 2;
                }
                try
                {
                    allowlist = GateAllowlist.Load(allowPath);
                }
                catch (FormatException ex)
                {
                    Console.Error.WriteLine(
                        $"mw-plugin-test: --allow file '{GateAllowlist.Describe(allowPath)}' is "
                        + $"malformed — {ex.Message}");
                    return 2;
                }
                allowApplied = true;
                break;
            }
            case "--report" when i + 1 < args.Length:
                reportPath = args[++i];
                break;
            case "--bake-output" when i + 1 < args.Length:
                bakeOutput = args[++i];
                break;
            // 🚨 THE OTHER HALF OF THE SPLIT (#1763): the gate CONSUMES a bake instead of
            // producing one. Read and address-checked BEFORE the mesh boots — a gate pointed at a
            // bake it cannot consume compiles the tree itself and passes, which is
            // indistinguishable from a gate that consumed it perfectly, so the failure has to be
            // refused here rather than discovered never.
            case "--seed" when i + 1 < args.Length:
            {
                var (readSeed, problem) = BakeSeed.Read(
                    args[++i], MeshWeaver.Graph.Configuration.PrebuiltAssemblySeeder.LiveFrameworkMvid);
                if (problem is not null)
                {
                    Console.Error.WriteLine($"mw-plugin-test: --seed — {problem}");
                    return 2;
                }
                seed = readSeed;
                break;
            }
            case "--source-sha" when i + 1 < args.Length:
                sourceSha = args[++i];
                break;
            // 🚨 A module built OUTSIDE this image — the seam that lets a node repo gate content
            // against a module whose source lives in that repo (the platform image cannot build
            // it). Repeatable. Refused HERE, before a mesh exists, for the same reason --seed is:
            // a gate that quietly ran without a module it was told to load would refuse every
            // install needing that module's node types and blame the CONTENT.
            case "--module" when i + 1 < args.Length:
            {
                var modulePath = Path.GetFullPath(args[++i]);
                if (!File.Exists(modulePath))
                {
                    Console.Error.WriteLine(
                        $"mw-plugin-test: --module '{modulePath}' does not exist. Pass the module's "
                        + "ENTRY assembly (…/<Name>/<Name>.dll) and make sure it is mounted into "
                        + "the container.");
                    return 2;
                }
                externalModules.Add(modulePath);
                break;
            }
            // A value-taking option as the LAST argument would otherwise fall through to the default
            // case as "Unknown argument" — a misleading message for a missing value.
            case "--compile-timeout" or "--render-timeout" or "--allow" or "--report"
                or "--bake-output" or "--seed" or "--source-sha" or "--module":
                Console.Error.WriteLine($"Option '{args[i]}' requires a value. Try --help.");
                return 2;
            // Diagnostic: print the framework build identity this process resolves — the exact value
            // the bake keys bundles to and the seeder gates on (#1660 WS3). One line, `identity=<id>
            // provenance=<g<sha> | (unstamped)>`, then exit 0. Lets CI steps and operators verify
            // "would this build's bake be adoptable by that image?" without standing up a mesh, and
            // is what the surface-identity proof script drives.
            case "--print-framework-identity":
            {
                var provenance = MeshWeaver.Compiler.FrameworkBuildIdentity
                    .StampedIdentityOf(typeof(MeshWeaver.Compiler.FrameworkBuildIdentity).Assembly);
                Console.WriteLine(
                    $"identity={MeshWeaver.Graph.Configuration.PrebuiltAssemblySeeder.LiveFrameworkMvid} "
                    + $"provenance={provenance ?? "(unstamped)"}");
                return 0;
            }
            case "--help" or "-h":
                Console.WriteLine(
                    "usage: mw-plugin-test build <repo-root> [<package>... | all] ...   (see build --help)\n       mw-plugin-test <repo-root> [--compile-timeout <s>] [--render-timeout <s>] "
                    + "[--allow <file>] [--report <file>] [--seed <dir>] [--bake-output <dir>] "
                    + "[--source-sha <sha>] [--module <dll>]... [--print-framework-identity]");
                return 0;
            default:
                if (args[i].StartsWith('-') || root is not null)
                {
                    Console.Error.WriteLine($"Unknown argument '{args[i]}'. Try --help.");
                    return 2;
                }
                root = args[i];
                break;
        }
    }

    var options = new GateOptions
    {
        RepoRoot = root ?? ".",
        CompileTimeout = compileTimeout,
        RenderTimeout = renderTimeout,
        BakeOutputDirectory = bakeOutput,
        SourceSha = sourceSha,
        Seed = seed,
        ExternalModules = externalModules,
    };

    Console.WriteLine($"mw-plugin-test: gating node repos under '{Path.GetFullPath(options.RepoRoot)}'");
    // Say which external modules are in play, always — a run that silently loaded none is
    // indistinguishable from one that loaded them, right up until an install is refused.
    foreach (var module in externalModules)
        Console.WriteLine($"external module: {module}");
    if (allowApplied)
        Console.WriteLine($"known-debt allowlist: {allowlist.Entries.Count} entr(ies)");
    // 🚨 ObserveCompletion, never Rx's own observable-to-Task bridge — see the ruling of
    // 2026-08-30 ("no ToTask ever") and ReactiveCompletion's remarks.
    var report = (await PluginGateRunner.Run(options).FirstAsync()
        .ObserveCompletion(ex => Console.Error.WriteLine(
            $"plugin gate faulted AFTER the report was produced — reported, not orphaned: {ex}")))!;
    if (reportPath is not null)
    {
        // Written for EVERY completed run — red, green, or fatal — and before any allowlist verdict:
        // the combo verifier folds the raw evidence itself, and a missing report must only ever mean
        // "the tester never completed", not "the run was red".
        var fullReportPath = Path.GetFullPath(reportPath);
        var reportDir = Path.GetDirectoryName(fullReportPath);
        if (!string.IsNullOrEmpty(reportDir))
            Directory.CreateDirectory(reportDir);
        File.WriteAllText(
            fullReportPath,
            JsonSerializer.Serialize(report.ToRunReport(), InstanceComboAssembler.Json));
        Console.WriteLine($"structured report written to '{fullReportPath}'");
    }
    if (!allowApplied)
    {
        report.WriteSummary(Console.Out);
        return report.ExitCode;
    }
    var verdict = GateVerdict.Evaluate(report, allowlist);
    report.WriteSummary(Console.Out, verdict);
    return report.FatalError is null && verdict.Success ? 0 : 1;
}
