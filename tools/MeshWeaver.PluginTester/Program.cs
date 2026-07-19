using System.Globalization;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using MeshWeaver.PluginTester;

// mw-plugin-test <repo-root> [--compile-timeout <seconds>] [--render-timeout <seconds>]
//
// The MeshWeaver.Plugins PR gate: imports each node-repo package of the checkout into a fresh
// in-process mesh, waits for every NodeType to compile (Roslyn diagnostics on error), renders
// each type's default area, and EXECUTES each type's `Tests` layout area. Exit 0 = all green.
//
// The one Task bridge lives HERE, at the console boundary — everything below Run() is reactive.

string? root = null;
var compileTimeout = TimeSpan.FromMinutes(5);
var renderTimeout = TimeSpan.FromMinutes(2);

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
        case "--help" or "-h":
            Console.WriteLine(
                "usage: mw-plugin-test <repo-root> [--compile-timeout <s>] [--render-timeout <s>]");
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
var report = await PluginGateRunner.Run(options).FirstAsync().ToTask();
report.WriteSummary(Console.Out);
return report.ExitCode;
