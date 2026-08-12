using System.Globalization;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Text.Json;
using MeshWeaver.PluginCatalog;
using MeshWeaver.PluginTester;

// mw-plugin-test <repo-root> [--compile-timeout <seconds>] [--render-timeout <seconds>]
//                            [--allow <file>] [--report <file>]
//
// The MeshWeaver.Plugins PR gate: imports each node-repo package of the checkout into a fresh
// in-process mesh, waits for every NodeType to compile (Roslyn diagnostics on error), renders
// each type's default area, and EXECUTES each type's `Tests` layout area. Exit 0 = all green.
// --allow names a known-debt file (the compile-check.allow ratchet): listed failures are
// tolerated, new failures fail, and an entry whose check now passes is stale and fails.
// --report writes the structured GateRunReport JSON (the combo verifier's wire contract) —
// written for every completed run, red or green, BEFORE the allowlist verdict is applied.
//
// The one Task bridge lives HERE, at the console boundary — everything below Run() is reactive.

string? root = null;
var compileTimeout = TimeSpan.FromMinutes(5);
var renderTimeout = TimeSpan.FromMinutes(2);
var allowlist = GateAllowlist.Empty;
var allowApplied = false;
string? reportPath = null;

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
        case "--allow" when i + 1 < args.Length:
            allowlist = GateAllowlist.Load(args[++i]);
            allowApplied = true;
            break;
        case "--report" when i + 1 < args.Length:
            reportPath = args[++i];
            break;
        // A value-taking option as the LAST argument would otherwise fall through to the default
        // case as "Unknown argument" — a misleading message for a missing value.
        case "--compile-timeout" or "--render-timeout" or "--allow" or "--report":
            Console.Error.WriteLine($"Option '{args[i]}' requires a value. Try --help.");
            return 2;
        case "--help" or "-h":
            Console.WriteLine(
                "usage: mw-plugin-test <repo-root> [--compile-timeout <s>] [--render-timeout <s>] "
                + "[--allow <file>] [--report <file>]");
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
