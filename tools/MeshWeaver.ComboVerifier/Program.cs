using System.Collections.Immutable;
using System.Globalization;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Text.Json;
using MeshWeaver.ComboVerifier;
using MeshWeaver.GitSync;
using MeshWeaver.Mesh.Threading;
using MeshWeaver.PluginCatalog;

// mw-combo-verify <combo.json> <candidate-image>
//     [--work-root <dir>] [--verdict <file>] [--tag <candidate-tag>]
//     [--allow-moving] [--allow-incomplete]
//     [--source <name>=<url>]… [--default-source <url>]
//     [--fetch-timeout <seconds>] [--gate-timeout <seconds>] [--token-env <VAR>]
//
// Step 3 of the Candidate Release Protocol's instance gate — the whole combo check as ONE ops/CI
// job: read the instance's combo (the file InstanceComboReader emits — step 1), materialise every
// module at its RECORDED ref (InstanceComboAssembler — step 2), run mw-plugin-test over that root
// INSIDE the candidate image (`docker run … --entrypoint /app/mw-plugin-test`, the same contract
// as the plugins repo's test-repos job), and fold the evidence into ONE verdict:
//
//     GREEN         every module compiles, renders, and its Tests area passes — exit 0.
//     RED           at least one module fails; every failing module is named — exit 1.
//     NOTVERIFIABLE the question could not be answered (moving refs, a fetch failure, no
//                   structured report) — exit 1, with the caveats naming every reason.
//
// The verdict is written as combo-verdict.json — a ComboVerification, ready to be landed on the
// instance's Admin/UpdatePolicy (content.comboVerifications) where the admin Updates tab renders
// it. The tool itself holds no portal credential; landing the verdict is the operator's / CD's
// authenticated step (e.g. the meshweaver MCP `patch` tool).
//
// The one Task bridge lives HERE, at the console boundary — everything below is reactive.

string? comboPath = null;
string? imageRef = null;
string? workRoot = null;
string? verdictPath = null;
string? candidateTag = null;
var allowMoving = false;
var allowIncomplete = false;
string? defaultSource = null;
var tokenEnv = "GITHUB_TOKEN";
var fetchTimeout = TimeSpan.FromMinutes(2);
var gateTimeout = TimeSpan.FromMinutes(45);
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
        case "--work-root" when i + 1 < args.Length:
            workRoot = args[++i];
            break;
        case "--verdict" when i + 1 < args.Length:
            verdictPath = args[++i];
            break;
        case "--tag" when i + 1 < args.Length:
            candidateTag = args[++i];
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
        case "--gate-timeout" when i + 1 < args.Length:
            gateTimeout = TimeSpan.FromSeconds(
                double.Parse(args[++i], CultureInfo.InvariantCulture));
            break;
        // A value-taking option as the LAST argument would otherwise fall through to the default
        // case as "Unknown argument" — a misleading message for a missing value.
        case "--work-root" or "--verdict" or "--tag" or "--source" or "--default-source"
            or "--token-env" or "--fetch-timeout" or "--gate-timeout":
            Console.Error.WriteLine($"Option '{args[i]}' requires a value. Try --help.");
            return 2;
        case "--help" or "-h":
            Console.WriteLine(
                "usage: mw-combo-verify <combo.json> <candidate-image> [--work-root <dir>] "
                + "[--verdict <file>] [--tag <candidate-tag>] [--allow-moving] "
                + "[--allow-incomplete] [--source <name>=<url>]... [--default-source <url>] "
                + "[--fetch-timeout <s>] [--gate-timeout <s>] [--token-env <VAR>]");
            return 0;
        default:
            if (args[i].StartsWith('-') || imageRef is not null)
            {
                Console.Error.WriteLine($"Unknown argument '{args[i]}'. Try --help.");
                return 2;
            }
            if (comboPath is null)
                comboPath = args[i];
            else
                imageRef = args[i];
            break;
    }
}

if (comboPath is null || imageRef is null)
{
    Console.Error.WriteLine(
        "Both <combo.json> and <candidate-image> are required. Try --help.");
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

var explicitWorkRoot = workRoot is not null;
workRoot ??= Path.Combine(
    Path.GetTempPath(), $"mw-combo-verify-{Guid.NewGuid():N}"[..38]);
verdictPath ??= "combo-verdict.json";

Console.WriteLine(
    $"mw-combo-verify: {combo.Modules.Count} module(s) read at {combo.ReadAt:O} "
    + $"× candidate '{imageRef}' → '{Path.GetFullPath(workRoot)}'");

// The same fetch machinery GitSync + mw-combo-assemble run on: one shallow pack per (repo, ref)
// over the git protocol. An empty token = anonymous access to public repos.
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

var gate = new DockerImageGate(pools, gateTimeout, Console.Out);
var verifier = new InstanceComboVerifier(assembler, gate.Run);

var run = await verifier.Verify(combo, imageRef, workRoot, candidateTag).FirstAsync().ToTask();

run.WriteSummary(Console.Out);

var fullVerdictPath = Path.GetFullPath(verdictPath);
File.WriteAllText(
    fullVerdictPath, JsonSerializer.Serialize(run.Verdict, InstanceComboAssembler.Json));
Console.WriteLine();
Console.WriteLine($"verdict written to '{fullVerdictPath}'.");
Console.WriteLine(
    "To land it where the instance's admins look, merge it into Admin/UpdatePolicy on that "
    + "instance: content.comboVerifications, upsert by candidateTag (e.g. via the meshweaver "
    + "MCP: get @Admin/UpdatePolicy → merge → patch). The Updates settings tab renders it.");

// A throwaway work root is kept when the verdict is not green — the materialised tree and the
// gate report are exactly what an operator inspects next.
if (!explicitWorkRoot)
{
    if (run.ExitCode == 0)
        Directory.Delete(workRoot, recursive: true);
    else
        Console.WriteLine($"work root kept for inspection: '{Path.GetFullPath(workRoot)}'");
}

return run.ExitCode;
