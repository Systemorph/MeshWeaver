using System.Collections.Immutable;
using System.Globalization;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Text.Json;
using MeshWeaver.ComboVerifier;
using MeshWeaver.GitSync;
using MeshWeaver.Messaging;
using MeshWeaver.Mesh.Threading;
using MeshWeaver.PluginCatalog;

// mw-combo-verify <combo.json> <candidate-image> --host <gate-host-dir>
//     [--work-root <dir>] [--verdict <file>] [--tag <candidate-tag>]
//     [--allow-moving] [--allow-incomplete]
//     [--source <name>=<url>]… [--default-source <url>]
//     [--fetch-timeout <seconds>] [--gate-timeout <seconds>] [--token-env <VAR>]
//     [--platform <os/arch>]
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
//     (no verdict)  the gate host is not runnable (no tester CLI, runtimeconfig or portal
//                   manifest in it) — the INSTRUMENT is missing, so no question was asked and no
//                   verdict file is written — exit 3.
//
// <candidate-image> is the candidate PORTAL image (memex-portal-ai:<tag>); its own `dotnet` starts
// the gate. --host is the COMPOSED GATE HOST for that candidate: the portal's /app with the
// candidate set's tester CLI beside it, built by .github/scripts/compose-gate-host.sh — the same
// host every node-repo lane runs since MeshWeaver#3022. The portal image ships no tester, and the
// tester image's /app is a strict subset of the portal's, so neither image alone is the gate
// (#3848: every run exited 127 inside the portal image and was landed as NotVerifiable).
//
// 🚨 The run is PINNED to --platform (default linux/amd64, what the fleet runs). A candidate tag
// is a multi-arch manifest list whose variants carry different bytes and different framework build
// identities, and docker reports ONE list digest for both — so an unpinned run on an arm64 host
// produces a Green about arm64 that is indistinguishable from a Green about the amd64 the fleet
// actually serves. The verdict records the platform it ran (verifiedPlatform).
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
string? hostDir = null;
var allowMoving = false;
var allowIncomplete = false;
string? defaultSource = null;
var tokenEnv = "GITHUB_TOKEN";
var platform = DockerImageGate.FleetPlatform;
var fetchTimeout = TimeSpan.FromMinutes(2);
var gateTimeout = TimeSpan.FromMinutes(45);
var sources = ImmutableDictionary<string, string>.Empty
    .WithComparers(StringComparer.OrdinalIgnoreCase);
var moduleBundleDirs = ImmutableList<string>.Empty;

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
        case "--platform" when i + 1 < args.Length:
            platform = args[++i];
            break;
        case "--host" when i + 1 < args.Length:
            hostDir = args[++i];
            break;
        // The combo's MODULE half (MeshWeaver#4413): a directory of sealed module bundles for the
        // candidate's framework identity. Repeatable. See ComboModuleSet.
        case "--module-bundles" when i + 1 < args.Length:
            moduleBundleDirs = moduleBundleDirs.Add(args[++i]);
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
            or "--token-env" or "--fetch-timeout" or "--gate-timeout" or "--platform" or "--host"
            or "--module-bundles":
            Console.Error.WriteLine($"Option '{args[i]}' requires a value. Try --help.");
            return 2;
        case "--help" or "-h":
            Console.WriteLine(
                "usage: mw-combo-verify <combo.json> <candidate-image> --host <gate-host-dir> [--work-root <dir>] "
                + "[--verdict <file>] [--tag <candidate-tag>] [--allow-moving] "
                + "[--allow-incomplete] [--source <name>=<url>]... [--default-source <url>] "
                + "[--fetch-timeout <s>] [--gate-timeout <s>] [--token-env <VAR>] "
                + "[--module-bundles <dir>]... "
                + $"[--platform <os/arch>, default {DockerImageGate.FleetPlatform}]\n"
                + "  --module-bundles <dir>: sealed module bundles for the candidate's framework "
                + "identity. Required when the combo's packages declare compiled modules: the "
                + "tester ships none, so without them every Skill/Agent install is refused.");
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
if (hostDir is null)
{
    Console.Error.WriteLine(
        "--host <gate-host-dir> is required: the candidate portal's /app with the candidate set's "
        + "tester CLI beside it (.github/scripts/compose-gate-host.sh). Try --help.");
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
    + $"× candidate '{imageRef}' ({platform}) → '{Path.GetFullPath(workRoot)}'");

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

// 🚨 THE MODULE HALF OF THE COMBO (MeshWeaver#4413). The instance runs every installed package's
// compiled module; the tester's image ships none. A gate run without them registers no Skill or
// Agent type, refuses the first install that needs one, and cascades into a red that blames every
// package's CONTENT. So a combo that declares compiled modules with no module bytes handed over is
// a missing INSTRUMENT, refused here with no verdict written — never a run whose red would be
// landed on the instance as a statement about the candidate.
var declaredModules = ComboModuleSet.DeclaredBy(combo);
ComboModuleComposition? moduleSet = null;
if (declaredModules.Length > 0 && moduleBundleDirs.Count == 0)
{
    Console.Error.WriteLine(
        $"mw-combo-verify: the combo's packages declare {declaredModules.Length} compiled module(s) "
        + $"({string.Join(", ", declaredModules)}) and no --module-bundles was given, so the gate "
        + "would run without the modules this instance lands — the INSTRUMENT is incomplete, "
        + "nothing about the candidate was asked, and no verdict is written. Fetch the sealed "
        + "module set for the candidate's framework identity (.github/scripts/"
        + "compose-sealed-modules.sh) and pass its directory.");
    return DockerImageGate.TesterMissingExitCode;
}
if (declaredModules.Length > 0)
{
    try
    {
        moduleSet = await pools.Get(IoPoolNames.FileSystem)
            .InvokeBlocking(_ => ComboModuleSet.Compose(
                declaredModules, moduleBundleDirs, Path.GetFullPath(workRoot) + ".modules"))
            .FirstAsync()
            .ObserveCompletion(ex => Console.Error.WriteLine(
                $"module composition faulted AFTER it was reported — reported, not orphaned: {ex}"));
    }
    catch (Exception ex) when (ex is InvalidOperationException or IOException or InvalidDataException)
    {
        Console.Error.WriteLine($"mw-combo-verify: the module set could not be composed: {ex.Message}");
        return DockerImageGate.TesterMissingExitCode;
    }
    Console.WriteLine(
        $"modules: {moduleSet!.Modules.Length} of {declaredModules.Length} declared compiled "
        + $"module(s) composed from the sealed set: {string.Join(", ", moduleSet.Modules)}");
    if (moduleSet.NotSealed.Length > 0)
        Console.WriteLine(
            $"modules: {moduleSet.NotSealed.Length} declared but carried by no fetched seal, so NOT "
            + $"loaded (the producing repository's own gate does not load them either): "
            + string.Join(", ", moduleSet.NotSealed));
    if (moduleSet.VersionDivergence(combo) is { Length: > 0 } diverged)
        Console.WriteLine(
            $"modules: {diverged.Length} composed at a version other than the installed one: "
            + string.Join("; ", diverged));
    if (moduleSet.NotDeclared.Length > 0)
        Console.WriteLine(
            $"modules: {moduleSet.NotDeclared.Length} sealed but not installed on this instance, so "
            + $"NOT loaded: {string.Join(", ", moduleSet.NotDeclared)}");
}

var gate = new DockerImageGate(pools, gateTimeout, Console.Out, platform, hostDir, moduleSet);

// 🚨 THE INSTRUMENT FIRST (#3848). A gate with no tester to start exits 127 with no report, which
// folds into a NotVerifiable that reads as a statement about the candidate — and was landed on live
// instances as one. A missing tester is not a verdict: say what is missing, write no verdict file,
// and exit with the code that means "the instrument is broken".
var testerMissing = await gate.ProbeHost().FirstAsync()
    .ObserveCompletion(ex => Console.Error.WriteLine(
        $"tester probe faulted AFTER it was reported — reported, not orphaned: {ex}"));
if (testerMissing is not null)
{
    Console.Error.WriteLine($"mw-combo-verify: {testerMissing}");
    return DockerImageGate.TesterMissingExitCode;
}

var verifier = new InstanceComboVerifier(assembler, gate.Run);

// 🚨 ObserveCompletion, never Rx's own observable-to-Task bridge (maintainer, 2026-08-30:
// "no ToTask ever"). Rx's bridge resumes this top-level continuation INLINE on whichever
// thread the verifier's last stage signalled from, still inside Rx's trampoline — and every
// later `await` in this file then schedules onto that same captured scheduler.
var run = (await verifier.Verify(combo, imageRef, workRoot, candidateTag).FirstAsync()
    .ObserveCompletion(ex => Console.Error.WriteLine(
        $"combo verify faulted AFTER the run was reported — reported, not orphaned: {ex}")))!;

run.WriteSummary(Console.Out);

// The verdict names its exact input, the module half included: a declared module no seal carried
// ran unloaded, and a failure that needs it must be attributable from the verdict alone.
var moduleCaveats = ImmutableList<string>.Empty;
if (moduleSet is { NotSealed.Length: > 0 })
    moduleCaveats = moduleCaveats.Add(
        $"{moduleSet.NotSealed.Length} compiled module(s) this instance declares were carried by "
        + "no sealed publication for the candidate's framework identity and were NOT loaded: "
        + string.Join(", ", moduleSet.NotSealed));
if (moduleSet?.VersionDivergence(combo) is { Length: > 0 } divergent)
    moduleCaveats = moduleCaveats.Add(
        $"{divergent.Length} module(s) ran at the version sealed for the candidate's framework "
        + "identity — what the instance lands once it rolls there — which is not the version it has "
        + $"installed today: {string.Join("; ", divergent)}");
var verdictToWrite = moduleCaveats.IsEmpty
    ? run.Verdict
    : run.Verdict with { Caveats = run.Verdict.Caveats.AddRange(moduleCaveats) };
var fullVerdictPath = Path.GetFullPath(verdictPath);
File.WriteAllText(
    fullVerdictPath, JsonSerializer.Serialize(verdictToWrite, InstanceComboAssembler.Json));
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
