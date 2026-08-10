using System.Collections.Immutable;
using System.Globalization;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Text.Json;
using MeshWeaver.GitSync;
using MeshWeaver.Mesh.Threading;
using MeshWeaver.PluginCatalog;

// mw-combo-assemble <combo.json> <work-root>
//     [--allow-moving] [--allow-incomplete]
//     [--source <name>=<url>]… [--default-source <url>]
//     [--fetch-timeout <seconds>] [--token-env <VAR>]
//
// Step 2 of the Candidate Release Protocol's instance gate: reads an InstanceCombo (what an
// instance actually runs, as `InstanceComboReader` states it — step 1), materialises every
// module's files at its RECORDED ref into `<work-root>/<ModuleId>/`, and writes the manifest
// `combo-assembly.json` (module → resolved ref → content hash) so the gate's verdict can name
// its exact input. The resulting directory is the repo root `mw-plugin-test` verifies (step 3).
//
// Moving/unrecorded refs are REFUSED by default — a gate run on a moving ref is not evidence —
// and --allow-moving is the explicit opt-in. Exit 0 = every module materialised.
//
// The one Task bridge lives HERE, at the console boundary — everything below Assemble() is
// reactive.

string? comboPath = null;
string? workRoot = null;
var allowMoving = false;
var allowIncomplete = false;
string? defaultSource = null;
var tokenEnv = "GITHUB_TOKEN";
var fetchTimeout = TimeSpan.FromMinutes(2);
var sources = ImmutableDictionary<string, string>.Empty
    .WithComparers(StringComparer.OrdinalIgnoreCase);

for (var i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--allow-moving":
            allowMoving = true;
            break;
        case "--allow-incomplete":
            allowIncomplete = true;
            break;
        case "--source" when i + 1 < args.Length:
            var pair = args[++i];
            var eq = pair.IndexOf('=');
            if (eq <= 0 || eq == pair.Length - 1)
            {
                Console.Error.WriteLine(
                    $"--source expects <name>=<url>, got '{pair}'. Try --help.");
                return 2;
            }
            sources = sources.SetItem(pair[..eq], pair[(eq + 1)..]);
            break;
        case "--default-source" when i + 1 < args.Length:
            defaultSource = args[++i];
            break;
        case "--token-env" when i + 1 < args.Length:
            tokenEnv = args[++i];
            break;
        case "--fetch-timeout" when i + 1 < args.Length:
            fetchTimeout = TimeSpan.FromSeconds(
                double.Parse(args[++i], CultureInfo.InvariantCulture));
            break;
        // A value-taking option as the LAST argument would otherwise fall through to the default
        // case as "Unknown argument" — a misleading message for a missing value.
        case "--source" or "--default-source" or "--token-env" or "--fetch-timeout":
            Console.Error.WriteLine($"Option '{args[i]}' requires a value. Try --help.");
            return 2;
        case "--help" or "-h":
            Console.WriteLine(
                "usage: mw-combo-assemble <combo.json> <work-root> [--allow-moving] "
                + "[--allow-incomplete] [--source <name>=<url>]... [--default-source <url>] "
                + "[--fetch-timeout <s>] [--token-env <VAR>]");
            return 0;
        default:
            if (args[i].StartsWith('-') || workRoot is not null)
            {
                Console.Error.WriteLine($"Unknown argument '{args[i]}'. Try --help.");
                return 2;
            }
            if (comboPath is null)
                comboPath = args[i];
            else
                workRoot = args[i];
            break;
    }
}

if (comboPath is null || workRoot is null)
{
    Console.Error.WriteLine(
        "Both <combo.json> and <work-root> are required. Try --help.");
    return 2;
}
if (!File.Exists(comboPath))
{
    Console.Error.WriteLine($"Combo file '{comboPath}' does not exist.");
    return 2;
}

InstanceCombo? combo;
try
{
    combo = JsonSerializer.Deserialize<InstanceCombo>(
        File.ReadAllText(comboPath), InstanceComboAssembler.Json);
}
catch (JsonException ex)
{
    Console.Error.WriteLine($"'{comboPath}' is not a valid InstanceCombo: {ex.Message}");
    return 2;
}
if (combo is null)
{
    Console.Error.WriteLine($"'{comboPath}' deserialized to nothing.");
    return 2;
}

Console.WriteLine(
    $"mw-combo-assemble: {combo.Modules.Count} module(s) read at {combo.ReadAt:O} "
    + $"→ '{Path.GetFullPath(workRoot)}'");

// The same fetch machinery GitSync + the plugin registry run on: one shallow pack per
// (repo, ref) over the git protocol, REST only where the wire protocol cannot serve
// (abbreviated shas). An empty token = anonymous access to public repos.
using var pools = new IoPoolRegistry();
var octokit = new OctokitGitHubRepoClient(pools);
var client = new GitProtocolRepoClient(octokit, new GitCli(pools), pools);

var assembler = new InstanceComboAssembler(
    client.Fetch,
    pools.Get(IoPoolNames.FileSystem),
    new ComboAssemblyOptions
    {
        AllowMoving = allowMoving,
        AllowIncomplete = allowIncomplete,
        FetchTimeout = fetchTimeout,
        AccessToken = Environment.GetEnvironmentVariable(tokenEnv) ?? "",
        SourceRepositories = sources,
        DefaultSourceRepository = defaultSource,
        Output = Console.Out,
    });

var report = await assembler.Assemble(combo, workRoot).FirstAsync().ToTask();
report.WriteSummary(Console.Out);
return report.ExitCode;
