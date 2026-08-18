using System.Globalization;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Text.Json;
using MeshWeaver.PluginCatalog;
using MeshWeaver.PluginTester;

using MeshWeaver.Compiler;
// mw-plugin-test <repo-root> [--compile-timeout <seconds>] [--render-timeout <seconds>]
//                            [--allow <file>] [--report <file>]
//                            [--bake-output <dir>] [--source-sha <sha>]
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
//
// The one Task bridge lives HERE, at the console boundary — everything below Run() is reactive.

// 🚨 THE `compile` VERB — the compiler-driven bake (#1763). It resolves NodeType sources straight
// from the git tree, compiles them with MeshWeaver.Compiler and emits DLL + PDB into the existing
// bundle format. There is NO MeshBuilder, NO AddGraph(), NO content import and NO hub anywhere in
// its path: producing an assembly is a build step, and the mesh's job is to CONSUME a bake.
//
//     mw-compiler compile <checkout-root> --output <dir> [--source-sha <sha>]
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
    return await RunGate(args);
}
catch (Exception ex)
{
    Console.Error.WriteLine($"mw-plugin-test: FATAL — {ex}");
    return 70; // EX_SOFTWARE: distinct from 0 (green), 1 (gate red) and 2 (bad usage).
}

static int RunCompile(string[] args)
{
    string? compileRoot = null;
    string? outputDirectory = null;
    string? compileSourceSha = null;
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
            case "--output" or "--source-sha":
                Console.Error.WriteLine($"Option '{args[i]}' requires a value.");
                return 2;
            case "--help" or "-h":
                Console.WriteLine(
                    "usage: mw-compiler compile <checkout-root> --output <dir> [--source-sha <sha>]");
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
    });
    if (bake.FatalError is not null)
        Console.Error.WriteLine($"compile: FATAL — {bake.FatalError}");
    foreach (var failed in bake.Types.Where(t => !t.Success))
        Console.Error.WriteLine($"compile: RED {failed.NodePath} — {failed.Error}");
    Console.WriteLine(
        $"compile: {bake.Types.Count(t => t.Success)}/{bake.Types.Length} NodeType(s) compiled, "
        + $"{bake.Bundles.Length} bundle(s), framework={bake.FrameworkIdentity}");
    return bake.ExitCode;
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
            case "--source-sha" when i + 1 < args.Length:
                sourceSha = args[++i];
                break;
            // A value-taking option as the LAST argument would otherwise fall through to the default
            // case as "Unknown argument" — a misleading message for a missing value.
            case "--compile-timeout" or "--render-timeout" or "--allow" or "--report"
                or "--bake-output" or "--source-sha":
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
                    "usage: mw-plugin-test <repo-root> [--compile-timeout <s>] [--render-timeout <s>] "
                    + "[--allow <file>] [--report <file>] [--bake-output <dir>] [--source-sha <sha>] "
                    + "[--print-framework-identity]");
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
    };

    Console.WriteLine($"mw-plugin-test: gating node repos under '{Path.GetFullPath(options.RepoRoot)}'");
    if (allowApplied)
        Console.WriteLine($"known-debt allowlist: {allowlist.Entries.Count} entr(ies)");
    var report = await PluginGateRunner.Run(options).FirstAsync().ToTask();
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
