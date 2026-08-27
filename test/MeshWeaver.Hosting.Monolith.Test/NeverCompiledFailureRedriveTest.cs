using System;
using System.Reactive.Linq;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

using MeshWeaver.Compiler;
namespace MeshWeaver.Hosting.Monolith.Test;

/// <summary>
/// 🚨 Repro + regression pin for issue #1793: a NodeType that has NEVER compiled successfully and
/// settles at <see cref="CompilationStatus.Error"/> was unreachable by EVERY automatic re-drive.
///
/// <para><b>Why every path skipped it.</b> <c>ApplyCompileFailure</c> stamps neither
/// <see cref="NodeTypeDefinition.LatestAssemblyCollection"/> /
/// <see cref="NodeTypeDefinition.LatestAssemblyPath"/> nor
/// <see cref="NodeTypeDefinition.CompiledFrameworkVersion"/>, so for such a type all three stay
/// null forever — and the first-build kickoff needs a <c>null</c> status, the recovery kickoff
/// needs <c>Compiling</c>, the framework-stale kickoff needs those very coordinates, the release
/// watcher needs a human to move <see cref="NodeTypeDefinition.RequestedReleaseAt"/>, and the
/// sources watcher's parked auto-retry needs the IN-MEMORY park registry to still hold the path
/// (a failure that predates the process is not in it). Only a human pressing Compile got the node
/// out; a redeploy, a framework bump, a module update and a fix to the failing code all reached
/// none of them. <c>NodeCompileShaping.AnchorIncludePath</c> was written for fifteen types parked
/// on memex-cloud and, five days later, #1786 found near enough the same list still parked.</para>
///
/// <para><b>What is pinned here.</b> Three properties, each of which is a way the fix could be
/// wrong: it must RECOVER a never-compiled failure without any user action; it must NOT loop on a
/// type whose source genuinely cannot compile (an unbounded re-drive on the per-NodeType hub's
/// single-threaded action block is the recompile-storm wedge); and its own bookkeeping must not
/// re-arm its own trigger (the write-cycle shape of the 257,000-version <c>_Policy</c> storm).</para>
///
/// <para>The pure half — the verdict-inputs token, the predicate, the ledger — is
/// <c>FailedVerdictRedriveTest</c> in MeshWeaver.Graph.Test.</para>
///
/// <para>🚨 <b>The bounded assertions here are not self-certifying.</b> Every observation runs over
/// <c>GetMeshNodeStream</c>, a <c>Replay(1)</c> mirror that hands each new subscription the snapshot
/// it already holds and keeps nothing before it — so an assertion can be satisfied by a REPLAY of
/// state that predates the thing it is claiming about, and it then passes for the wrong reason
/// forever. <see cref="RedriveObservation"/> is the vocabulary that closes it (a version watermark,
/// with the process ledger as the primary bound); <c>RedriveObservationReplayTest</c> is what proves
/// it, by constructing the replay and pinning BOTH halves — the shape this file used to have
/// accepting it, and this one refusing it while still accepting the genuine case.</para>
/// </summary>
public class NeverCompiledFailureRedriveTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    protected override bool ShareMeshAcrossTests => true;

    // Real Roslyn compiles (a failing one, then the automatic rebuild) — same shape and budget as
    // FrameworkStaleProactiveRebuildTest.
    protected override TimeSpan TestSoftDeadline => TimeSpan.FromSeconds(90);
    protected override TimeSpan TestHardDeadline => TimeSpan.FromSeconds(180);

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder).AddGraph();

    private IMeshService MeshService => Mesh.ServiceProvider.GetRequiredService<IMeshService>();
    private NodeTypeCompileParkRegistry ParkRegistry =>
        Mesh.ServiceProvider.GetRequiredService<NodeTypeCompileParkRegistry>();

    /// <summary>
    /// 🚨 THE #1793 REPRO — the exact record shape #1786 measured: a NodeType sitting at
    /// <c>Error</c> with no assembly coordinates, no framework stamp, and no record of the inputs
    /// the verdict was formed under (a compile that failed on another machine, an export that baked
    /// the verdict into a file, or simply a failure that predates this field).
    ///
    /// <para>No instance is activated, no Compile is clicked, no release is requested. The type's
    /// OWN hub must re-drive it exactly once and it must come back green.</para>
    /// </summary>
    [Fact(Timeout = 180_000)]
    public async Task NeverCompiledErrorRecord_IsReDrivenAutomatically_AndRecovers()
    {
        var (typePath, baselineSucceededAt) = await CompileBaselineType("RedriveRecovers", GoodSource);
        var workspace = Mesh.GetWorkspace();

        var forgeWatermark = await ForgeNeverCompiledFailure(typePath);

        // The OWNER-side re-drive must rebuild it: back to Ok, with real assembly coordinates and a
        // STRICTLY NEWER success timestamp (a replayed old Ok would prove nothing) at a revision
        // STRICTLY past the forge, so the mirror's Replay(1) hand-off of the pre-forge snapshot
        // cannot satisfy it either. Before the fix nothing re-drives this record at all, so this
        // times out.
        var recovered = await workspace.GetMeshNodeStream(typePath)
            .Should().Within(90.Seconds())
            .Match(n => RedriveObservation.IsPastWatermark(n, forgeWatermark)
                && n.Content is NodeTypeDefinition d
                && d.CompilationStatus == CompilationStatus.Ok
                && !string.IsNullOrEmpty(d.LatestAssemblyPath)
                && d.CompiledFrameworkVersion == NodeTypeCompilationHelpers.FrameworkVersion
                && d.LastCompileSucceededAt is { } s && s > baselineSucceededAt);
        Output.WriteLine(
            $"Automatically re-driven and recovered: {typePath} → Ok at "
            + $"{((NodeTypeDefinition)recovered.Content!).LastCompileSucceededAt:O}");

        // 🚨 …and it converged. A success clears the failure stamp (a stale one would make a LATER
        // failure look like it had already had its attempt), and nothing re-drives a healthy type.
        ((NodeTypeDefinition)recovered.Content!).FailedBuildInputs.Should().BeNull(
            "a success retires the failure verdict, so its inputs must go with it");
        await AssertNoFurtherCompileIsDriven(typePath, recovered.Version, 15.Seconds(),
            "a recovered type must not be re-driven again — the re-drive's own bookkeeping is what "
            + "makes its trigger false, and a trigger that survives its own write is a write cycle");
    }

    /// <summary>
    /// 🚨 THE BOUND. The same record shape as above, but on a type whose source CANNOT compile: it
    /// gets its one automatic attempt, that attempt fails, and then it is left alone. An unbounded
    /// retry on a genuinely broken type saturates the per-NodeType hub's single-threaded action
    /// block — the wedge the park registry exists to prevent — so "recoverable when it can be" must
    /// not cost "loudly stuck when it cannot".
    /// </summary>
    [Fact(Timeout = 180_000)]
    public async Task ABrokenSource_IsReDrivenExactlyOnce_AndThenLeftAlone()
    {
        var typePath = await CreateType("RedriveBounded", BrokenSource);
        var workspace = Mesh.GetWorkspace();

        // The first-build kickoff runs and Roslyn rejects it. The failure now RECORDS the inputs it
        // was formed under, so the re-drive declines: it is the same framework, the same modules and
        // the same sources that just failed.
        await workspace.GetMeshNodeStream(typePath)
            .Should().Within(90.Seconds())
            .Match(n => n.Content is NodeTypeDefinition d
                && d.CompilationStatus == CompilationStatus.Error
                && !string.IsNullOrEmpty(d.FailedBuildInputs));
        Output.WriteLine($"{typePath} settled at Error with its verdict inputs recorded.");

        ParkRegistry.GetFailureRedriveCount(typePath).Should().Be(0,
            "a verdict formed under exactly the live inputs has already had its attempt — "
            + "re-driving it would reproduce the identical failure");

        // Now stage the pre-fix record (no stamp) so the re-drive DOES fire, and prove it fires
        // exactly once: the attempt fails again, and nothing schedules another.
        var forgeWatermark = await ForgeNeverCompiledFailure(typePath);

        // 🚨 ANCHORED on the forge. Without the version clause this Match is satisfied by the
        // mirror's Replay(1) hand-off of the PRE-forge record — which, for a type that cannot
        // compile, is byte-identical to the record the re-drive re-settles at (Error, inputs
        // stamped). The test would then believe it had watched the re-drive fire and re-fail having
        // observed only state that predates the forge, and every bound below it would be anchored
        // in the past. The forge nulls FailedBuildInputs, so a record carrying it again at a
        // revision past the forge can only be the re-drive's own verdict.
        var reFailed = await workspace.GetMeshNodeStream(typePath)
            .Should().Within(90.Seconds())
            .Match(n => RedriveObservation.IsPastWatermark(n, forgeWatermark)
                && n.Content is NodeTypeDefinition d
                && d.CompilationStatus == CompilationStatus.Error
                && !string.IsNullOrEmpty(d.FailedBuildInputs));
        var redrives = ParkRegistry.GetFailureRedriveCount(typePath);
        redrives.Should().Be(1,
            "exactly one automatic attempt per distinct set of compile inputs — the flip to Pending "
            + "stamps those inputs in the same write, so the trigger cannot re-arm itself");
        Output.WriteLine(
            $"{typePath} re-driven {redrives}× and re-failed at v{reFailed.Version} "
            + $"(forge watermark v{forgeWatermark}) — now bounded.");

        await AssertNoFurtherCompileIsDriven(typePath, reFailed.Version, 20.Seconds(),
            "a type whose source cannot compile must back off after its one attempt instead of "
            + "storming Roslyn on the hub's action block");
        ParkRegistry.GetFailureRedriveCount(typePath).Should().Be(1,
            "…and the ledger must still show exactly one, which is the observable proof of the bound");
        ParkRegistry.IsParked(typePath).Should().BeTrue(
            "the re-failed attempt re-parks the type, so its cached error is served without recompiling");
    }

    /// <summary>
    /// 🚨 "a fix to the failing code" — the case the issue names, and the one no existing path
    /// covers once the failure predates this PROCESS. The park registry's source-change auto-retry
    /// is in-memory, so a restart empties it and a later edit re-drives nothing; here that state is
    /// staged by un-parking the type before the edit lands.
    /// </summary>
    [Fact(Timeout = 180_000)]
    public async Task AFixedSource_ReDrivesAFailureThatPredatesThisProcess()
    {
        var typePath = await CreateType("RedriveSourceFix", BrokenSource);
        var workspace = Mesh.GetWorkspace();
        var sourcePath = $"{typePath}/Source/code";

        await workspace.GetMeshNodeStream(typePath)
            .Should().Within(90.Seconds())
            .Match(n => n.Content is NodeTypeDefinition d && d.CompilationStatus == CompilationStatus.Error);

        // 🚧 Stage "the failure predates this process": the in-memory park is what the sources
        // watcher's ShouldRetryForSourceChange consults, and after a restart it holds nothing. With
        // it cleared, the ONLY thing that can notice the fix is the durable verdict-inputs stamp.
        ParkRegistry.Unpark(typePath);
        ParkRegistry.IsParked(typePath).Should().BeFalse(
            "the in-process park must be gone, or this test would pass through the pre-existing "
            + "same-process retry path and pin nothing");

        var typeId = typePath.Split('/')[^1];
        await workspace.GetMeshNodeStream(sourcePath)
            .Update(curr => curr is null ? curr! : curr with
            {
                Content = new CodeConfiguration { Code = GoodSource(typeId), Language = "csharp" }
            })
            .Should().Within(30.Seconds()).Emit();
        Output.WriteLine($"Fixed the source at {sourcePath} — nothing else was touched.");

        await workspace.GetMeshNodeStream(typePath)
            .Should().Within(90.Seconds())
            .Match(n => n.Content is NodeTypeDefinition d
                && d.CompilationStatus == CompilationStatus.Ok
                && !string.IsNullOrEmpty(d.LatestAssemblyPath));
        Output.WriteLine($"{typePath} recovered from the source fix with no Compile click.");
    }

    // ── staging ─────────────────────────────────────────────────────────────────────────────

    private static string GoodSource(string typeId) => $$"""
        public record {{typeId}} { public string Title { get; init; } = string.Empty; }
        """;

    /// <summary>Source Roslyn must reject — an unresolvable name, the CS0103 shape every parked
    /// type on memex-cloud carried.</summary>
    private static string BrokenSource(string typeId) => $$"""
        public record {{typeId}} { public string Title { get; init; } = ThisSymbolDoesNotExist; }
        """;

    private async Task<string> CreateType(string prefix, Func<string, string> source)
    {
        var typeId = $"{prefix}{Guid.NewGuid():N}";
        var typePath = $"type/{typeId}";

        await MeshService.CreateNode(MeshNode.FromPath(typePath) with
        {
            Name = typeId,
            NodeType = MeshNode.NodeTypePath,
            State = MeshNodeState.Active,
            Content = new NodeTypeDefinition
            {
                Configuration = $"config => config.WithContentType<{typeId}>()"
            }
        }).Should().Emit();
        await MeshService.CreateNode(new MeshNode("code", $"{typePath}/Source")
        {
            NodeType = "Code",
            Name = "code",
            State = MeshNodeState.Active,
            Content = new CodeConfiguration { Code = source(typeId), Language = "csharp" }
        }).Should().Emit();
        return typePath;
    }

    private async Task<(string Path, DateTimeOffset SucceededAt)> CompileBaselineType(
        string prefix, Func<string, string> source)
    {
        var typePath = await CreateType(prefix, source);
        await RequestHub.Observe(new GetCompilationPathRequest(), o => o.WithTarget(new Address(typePath)))
            .Should().Within(90.Seconds()).Emit();
        var okNode = await Mesh.GetWorkspace().GetMeshNodeStream(typePath)
            .Should().Within(60.Seconds())
            .Match(n => n.Content is NodeTypeDefinition d
                && d.CompilationStatus == CompilationStatus.Ok
                && d.LastCompileSucceededAt is not null
                && !string.IsNullOrEmpty(d.LatestAssemblyPath));
        var def = (NodeTypeDefinition)okNode.Content!;
        Output.WriteLine(
            $"Baseline compile Ok for {typePath} at {def.LastCompileSucceededAt!.Value:O}.");
        return (typePath, def.LastCompileSucceededAt!.Value);
    }

    /// <summary>
    /// Stamps the record shape a never-compiled failure leaves behind — and the shape #1786 found
    /// baked into 8 shipped node files: <c>Error</c>, no assembly coordinates, no framework stamp,
    /// and no record of the inputs the verdict was formed under. Nothing else is touched, and no
    /// instance of the type is ever activated, so only the OWNER-side re-drive can act on it.
    /// </summary>
    /// <returns>
    /// The WATERMARK every later observation must be anchored on: the version the forge write was
    /// based on. Any emission at or below it is the mirror replaying pre-forge state.
    /// </returns>
    private async Task<long> ForgeNeverCompiledFailure(string typePath)
    {
        var workspace = Mesh.GetWorkspace();
        // The park is in-memory and per-process; a record that arrived from elsewhere carries no
        // park with it, so clear it here too or the staged state would not be the staged state.
        ParkRegistry.Unpark(typePath);
        // Read what the mirror is currently replaying, purely as EVIDENCE: it is logged next to the
        // version Update hands back, and on every run the two are the same number. That equality is
        // the whole defect in #1850's `>=` bound, measured on the real mesh rather than argued.
        var beforeForge = await workspace.GetMeshNodeStream(typePath)
            .Should().Within(30.Seconds()).Emit();
        var marker = $"FORGED-{Guid.NewGuid():N}";
        var forged = await workspace.GetMeshNodeStream(typePath)
            .Update(curr => curr.Content is NodeTypeDefinition d
                ? curr with
                {
                    Content = d with
                    {
                        CompilationStatus = CompilationStatus.Error,
                        CompilationError = marker,
                        CompilationDiagnostics = null,
                        LatestAssemblyCollection = null,
                        LatestAssemblyPath = null,
                        CompiledFrameworkVersion = null,
                        CompiledSources = null,
                        FailedBuildInputs = null,
                    }
                }
                : curr)
            .Should().Within(30.Seconds()).Emit();
        // 🚧 Barrier: GetMeshNodeStream replays its latest snapshot, so without confirming the
        // forge LANDED a later convergence Match could match the pre-forge state and pass with no
        // re-drive at all — masking a disabled kickoff.
        //
        // 🚨 The barrier waits on the VERSION, not on seeing the forged content. The forged record
        // is what TRIGGERS the re-drive, so it is transient by construction: the kickoff can
        // recompile and overwrite it — marker gone, FailedBuildInputs filled — before this
        // subscription observes it, and then the barrier times out on a forge that landed
        // perfectly. That is a race the test loses more often the FASTER the product is, and it is
        // load-sensitive on CI: this helper is used by all three tests in this file, which is why
        // they fail in rotation (#1843 — observed on main and on three unrelated PRs).
        //
        // 🚨 …and the bound is STRICT (>), which #1850's `>=` was not — the deeper race, and it is
        // not a race at all but a structural vacuity. `Update` on a foreign path is UpdateRemote,
        // which emits `update(current)`: the OPTIMISTIC LOCAL snapshot, carrying the PRE-write
        // Version, because the forge lambda rewrites Content only. `>= forged.Version` is therefore
        // a bound the mirror is ALREADY sitting on, and Replay(1) satisfies it instantly with the
        // pre-forge snapshot — the barrier passed whether or not the write landed, and every
        // observation downstream of it was anchored before the forge. RedriveObservationReplayTest
        // constructs that replay and pins both halves: `>=` accepting it, `>` refusing it.
        //
        // 🚨 WHOSE version is this? The OWNER's, and it is a strict LOWER BOUND, not a receipt.
        // MeshNode.Version is minted by the owning per-node hub for EVERY writer
        // (MeshNode.NextVersion = current + 1) — #1833 is a test that flaked on assuming it tracks
        // the caller's own write. So `> watermark` claims exactly one thing: the owner has
        // committed at least one revision past the state this write was diffed against, so no
        // later emission can be that state replayed. It deliberately does NOT claim "my forge
        // landed" — the write's own completion above establishes that, and that the RE-DRIVE fired
        // is established by the ledger, never by catching a state designed to be replaced.
        var watermark = forged.Version;
        var landed = await workspace.GetMeshNodeStream(typePath)
            .Should().Within(20.Seconds())
            .Match(n => RedriveObservation.IsPastWatermark(n, watermark));
        Output.WriteLine(
            $"Forged the never-compiled Error record on {typePath} (marker {marker}); "
            + $"the mirror was replaying v{beforeForge.Version}, Update handed back v{watermark} "
            + $"(the SAME revision — so the old '>= {watermark}' bound was already satisfied), "
            + $"owner then moved to v{landed.Version}.");
        return watermark;
    }

    /// <summary>
    /// The sanctioned bounded NEGATIVE observation for "nothing re-drives this", with TWO
    /// independent witnesses — see <see cref="RedriveObservation"/> for why one is not enough.
    ///
    /// <para><b>Primary: the LEDGER.</b> <c>NodeTypeCompileParkRegistry</c> counts events —
    /// <c>RecordFailureRedrive</c> per automatic kickoff, <c>RecordAttempt</c> per real Roslyn
    /// dispatch. Counters cannot be replayed or conflated, and — decisively for a type that cannot
    /// compile — they can tell a re-drive that ran and re-failed apart from the record it started
    /// from, which are byte-identical.</para>
    ///
    /// <para><b>Corroboration: the stream</b>, anchored at <paramref name="watermark"/> so the
    /// mirror's <c>Replay(1)</c> hand-off of the snapshot the window OPENED on cannot read as a
    /// fresh drive. Unanchored, this half was satisfiable by a replay in both directions: tripped
    /// by a stale <c>Pending</c>, and passed by a whole <c>Pending → Compiling → Error</c> cycle
    /// that fitted between the claim and <c>NotEmit</c>'s pool-scheduled attach.</para>
    /// </summary>
    private async Task AssertNoFurtherCompileIsDriven(
        string typePath, long watermark, TimeSpan window, string because)
    {
        var observed = await RedriveObservation
            .OpenWindow(ParkRegistry, typePath, watermark)
            .AssertNothingWasDriven(Mesh.GetWorkspace().GetMeshNodeStream(typePath), window, because);
        Output.WriteLine($"No further compile was driven — {observed}.");
    }
}
