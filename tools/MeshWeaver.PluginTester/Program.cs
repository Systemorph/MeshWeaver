using System.Globalization;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using MeshWeaver.PluginTester;

// mw-plugin-test <repo-root> [--compile-timeout <seconds>] [--render-timeout <seconds>]
//                            [--allow <file>]
//
// The MeshWeaver.Plugins PR gate: imports each node-repo package of the checkout into a fresh
// in-process mesh, waits for every NodeType to compile (Roslyn diagnostics on error), renders
// each type's default area, and EXECUTES each type's `Tests` layout area. Exit 0 = all green.
// --allow names a known-debt file (the compile-check.allow ratchet): listed failures are
// tolerated, new failures fail, and an entry whose check now passes is stale and fails.
//
// The one Task bridge lives HERE, at the console boundary — everything below Run() is reactive.

string? root = null;
var compileTimeout = TimeSpan.FromMinutes(5);
var renderTimeout = TimeSpan.FromMinutes(2);
var allowlist = GateAllowlist.Empty;
var allowApplied = false;

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
        // A value-taking option as the LAST argument would otherwise fall through to the default
        // case as "Unknown argument" — a misleading message for a missing value.
        case "--compile-timeout" or "--render-timeout" or "--allow":
            Console.Error.WriteLine($"Option '{args[i]}' requires a value. Try --help.");
            return 2;
        case "--help" or "-h":
            Console.WriteLine(
                "usage: mw-plugin-test <repo-root> [--compile-timeout <s>] [--render-timeout <s>] "
                + "[--allow <file>]");
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
if (!allowApplied)
{
    report.WriteSummary(Console.Out);
    return report.ExitCode;
}
var verdict = GateVerdict.Evaluate(report, allowlist);
report.WriteSummary(Console.Out, verdict);
return report.FatalError is null && verdict.Success ? 0 : 1;
