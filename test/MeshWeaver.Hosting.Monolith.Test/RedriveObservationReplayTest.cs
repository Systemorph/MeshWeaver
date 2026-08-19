using System;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Threading.Tasks;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Reactive.Assertions;
using Xunit;

using MeshWeaver.Compiler;
namespace MeshWeaver.Hosting.Monolith.Test;

/// <summary>
/// 🚨 THE NON-VACUITY PROOF for <see cref="NeverCompiledFailureRedriveTest"/>'s bounded assertions.
///
/// <para><b>Why this file has to exist.</b> A negative assertion that already passes for the wrong
/// reason still passes after it is fixed, so "the suite is green" is evidence of nothing. The only
/// way to know an assertion means something is to CONSTRUCT the state it must reject and watch it
/// reject it — and to watch the shape it replaces ACCEPT it. Both halves are here, on the real
/// predicates (<see cref="RedriveObservation"/>), against the real ledger
/// (<see cref="NodeTypeCompileParkRegistry"/>).</para>
///
/// <para><b>The replay is not a metaphor — it is the mirror.</b> <c>MeshNodeStreamCache</c> backs
/// every <c>GetMeshNodeStream(path)</c> entry with <c>new ReplaySubject&lt;MeshNode&gt;(1)</c>, so
/// these tests use exactly that type. A new subscription is handed the one snapshot the mirror
/// holds; everything before it is gone.</para>
///
/// <para>No mesh, no hub, no Roslyn: deterministic and sub-second, which is what lets it be the
/// evidence for a suite whose own timing is the thing under suspicion.</para>
/// </summary>
public class RedriveObservationReplayTest(ITestOutputHelper output)
{
    private const string TypePath = "type/RedriveProbe";
    private const string Inputs = "fw=1.0|mods=abc|src=42";

    private static MeshNode Node(
        long version, CompilationStatus status, string? failedInputs, string? error = null) =>
        MeshNode.FromPath(TypePath) with
        {
            NodeType = MeshNode.NodeTypePath,
            State = MeshNodeState.Active,
            Version = version,
            Content = new NodeTypeDefinition
            {
                Configuration = "config => config",
                CompilationStatus = status,
                CompilationError = error,
                FailedBuildInputs = failedInputs,
            }
        };

    private static NodeTypeDefinition Def(MeshNode node) => (NodeTypeDefinition)node.Content!;

    // ── half 1 + 2 for the FORGE BARRIER ────────────────────────────────────────────────────

    /// <summary>
    /// 🚨 THE BUG, DEMONSTRATED. <c>ForgeNeverCompiledFailure</c>'s barrier waited for
    /// <c>n.Version &gt;= forged.Version</c>, where <c>forged</c> is what
    /// <c>GetMeshNodeStream(path).Update(...)</c> returned. That node is <c>UpdateRemote</c>'s
    /// OPTIMISTIC LOCAL snapshot — <c>update(current)</c> — and the forge lambda rewrites only
    /// <c>Content</c>, so it carries the PRE-write version. The bound is therefore a version the
    /// mirror is already sitting on, and the <c>Replay(1)</c> hand-off satisfies it with the
    /// PRE-FORGE snapshot: the barrier passes whether or not the write ever landed, and every
    /// assertion downstream of it is anchored before the forge.
    /// </summary>
    [Fact]
    public async Task TheOldForgeBarrier_IsSatisfiedByTheReplayedPreForgeSnapshot()
    {
        var mirror = new ReplaySubject<MeshNode>(1);
        var preForge = Node(version: 7, CompilationStatus.Error, failedInputs: Inputs,
            error: "CS0103: The name 'ThisSymbolDoesNotExist' does not exist");
        mirror.OnNext(preForge);

        // What Update hands back: the same node with forged Content — and the SAME Version,
        // because the lambda never touches it and the owner's mint is not in this emission.
        var forgedOptimistic = preForge with
        {
            Content = Def(preForge) with
            {
                CompilationStatus = CompilationStatus.Error,
                CompilationError = "FORGED-marker",
                FailedBuildInputs = null,
                LatestAssemblyPath = null,
                CompiledFrameworkVersion = null,
            }
        };
        forgedOptimistic.Version.Should().Be(preForge.Version,
            "UpdateRemote emits update(current); the forge lambda rewrites Content only, so the "
            + "returned node carries the PRE-write version — the owner's mint is not in it");
        output.WriteLine(
            $"Update returned version {forgedOptimistic.Version}; the mirror already holds "
            + $"version {preForge.Version}. The old bound is '>= {forgedOptimistic.Version}'.");

        // ── the shape on main ──
        var accepted = await mirror.Should().Within(2.Seconds())
            .Match(n => n.Version >= forgedOptimistic.Version);

        // It accepted the PRE-FORGE snapshot: the forge's marker is absent and the pre-forge
        // FailedBuildInputs is still there — this node predates the write the barrier is for.
        accepted.Version.Should().Be(preForge.Version);
        Def(accepted).CompilationError.Should().NotBe("FORGED-marker",
            "the bound was satisfied by the replayed PRE-FORGE snapshot, not by the forge");
        Def(accepted).FailedBuildInputs.Should().Be(Inputs,
            "the forge nulls FailedBuildInputs — this record still carries the pre-forge stamp, so "
            + "it is the state the mirror was already holding");
        output.WriteLine(
            "OLD BARRIER ACCEPTED the replay: version " + accepted.Version
            + ", CompilationError='" + Def(accepted).CompilationError
            + "', FailedBuildInputs='" + Def(accepted).FailedBuildInputs + "' — no forge in it.");

        // ── the shape that replaces it: STRICTLY past the watermark ──
        var watermark = preForge.Version;
        Func<Task> rejects = async () => await mirror.Should().Within(500.Milliseconds())
            .Match(n => RedriveObservation.IsPastWatermark(n, watermark));
        await rejects.Should().ThrowAsync<AssertionException>(
            "the replayed pre-forge snapshot IS the watermark, so a strict bound must refuse it");
        output.WriteLine($"NEW BARRIER REJECTED the same replay (watermark v{watermark}).");

        // …and it still accepts the genuine case: the OWNER mints NextVersion(current).
        var ownerCommitted = Node(version: MeshNode.NextVersion(preForge.Version),
            CompilationStatus.Pending, failedInputs: Inputs);
        mirror.OnNext(ownerCommitted);
        var genuine = await mirror.Should().Within(5.Seconds())
            .Match(n => RedriveObservation.IsPastWatermark(n, watermark));
        genuine.Version.Should().Be(8);
        output.WriteLine($"NEW BARRIER ACCEPTED the genuine owner-minted revision v{genuine.Version}.");
    }

    // ── half 1 + 2 for the NEGATIVE ASSERTION ───────────────────────────────────────────────

    /// <summary>
    /// 🚨 THE BUG, DEMONSTRATED — the negative assertion this time.
    /// <c>AssertNoFurtherCompileIsDriven</c> claimed "nothing re-drove this type" on the strength
    /// of one thing: no <c>Pending</c>/<c>Compiling</c> value arriving on the mirror. But the
    /// window the test MEANS opens where the caller stands; the window a bare <c>NotEmit</c>
    /// OBSERVES opens when its <c>SubscribeOn(TaskPoolScheduler)</c> attach lands — and
    /// <c>Replay(1)</c> collapses everything in between into a single snapshot. A complete
    /// re-drive (<c>Pending → Compiling → Error</c>) fits in that gap, and for a type whose source
    /// cannot compile the state it re-settles at is byte-identical to the state the window opened
    /// on. The assertion then passes having watched the exact thing it forbids.
    /// </summary>
    [Fact]
    public async Task TheOldNegativeAssertion_IsSatisfiedByAReplayThatHidesAWholeReDrive()
    {
        var ledger = new NodeTypeCompileParkRegistry();
        var mirror = new ReplaySubject<MeshNode>(1);

        // The type has already had its one bounded attempt: Error, inputs stamped, parked.
        ledger.RecordFailureRedrive(TypePath, Inputs);
        ledger.RecordAttempt(TypePath);
        var settled = Node(version: 9, CompilationStatus.Error, failedInputs: Inputs,
            error: "CS0103: The name 'ThisSymbolDoesNotExist' does not exist");
        mirror.OnNext(settled);

        // t0 — the window OPENS. This is where the mesh test stands: it has just been handed
        // `settled` by its Match and is about to claim nothing further is driven.
        var window = RedriveObservation.OpenWindow(ledger, TypePath, settled.Version);

        // t0+ε — a COMPLETE, UNBOUNDED re-drive: the kickoff records it, RunCompile dispatches
        // Roslyn, the node runs Pending → Compiling → Error. Exactly what the assertion forbids.
        // All of it lands before either subscriber attaches, so Replay(1) keeps only the last —
        // and the last is indistinguishable from `settled`.
        ledger.RecordFailureRedrive(TypePath, Inputs);
        ledger.RecordAttempt(TypePath);
        mirror.OnNext(Node(10, CompilationStatus.Pending, failedInputs: Inputs));
        mirror.OnNext(Node(11, CompilationStatus.Compiling, failedInputs: Inputs));
        var reSettled = Node(12, CompilationStatus.Error, failedInputs: Inputs,
            error: "CS0103: The name 'ThisSymbolDoesNotExist' does not exist");
        mirror.OnNext(reSettled);

        // ── the shape on main: the state filter alone, no anchor, no ledger ──
        await mirror
            .Where(n => n?.Content is NodeTypeDefinition d
                && d.CompilationStatus is CompilationStatus.Pending or CompilationStatus.Compiling)
            .Should().NotEmit(1.Seconds(),
                "the shape on main — this PASSES, and a full re-drive just happened");
        output.WriteLine(
            "OLD NEGATIVE ASSERTION PASSED while the ledger recorded re-drive "
            + $"{ledger.GetFailureRedriveCount(TypePath)} and attempt "
            + $"{ledger.GetCompileAttemptCount(TypePath)} — a FALSE GREEN on the bound that keeps a "
            + "permanently-broken NodeType out of an unbounded retry loop.");

        // ── the shape that replaces it, over the SAME mirror in the SAME state ──
        Func<Task> rejects = () => window.AssertNothingWasDriven(
            mirror, 1.Seconds(), "a re-failed type must be left alone");
        var thrown = await rejects.Should().ThrowAsync<AssertionException>(
            "the ledger moved inside the window, and the ledger is the primary bound");
        output.WriteLine("NEW NEGATIVE ASSERTION REJECTED the same replay: " + thrown.Which.Message);
    }

    /// <summary>
    /// …and the replacement still ACCEPTS the genuine case — a quiet window over a mirror that is
    /// replaying a settled snapshot and a ledger that does not move. Without this half the fix
    /// could be "assert false", which rejects the replay and everything else with it.
    /// </summary>
    [Fact]
    public async Task TheNewNegativeAssertion_AcceptsAQuietWindow()
    {
        var ledger = new NodeTypeCompileParkRegistry();
        var mirror = new ReplaySubject<MeshNode>(1);

        ledger.RecordFailureRedrive(TypePath, Inputs);
        ledger.RecordAttempt(TypePath);
        var settled = Node(version: 12, CompilationStatus.Error, failedInputs: Inputs);
        mirror.OnNext(settled);

        var window = RedriveObservation.OpenWindow(ledger, TypePath, settled.Version);
        var summary = await window.AssertNothingWasDriven(
            mirror, 1.Seconds(), "a re-failed type must be left alone");
        output.WriteLine("NEW NEGATIVE ASSERTION ACCEPTED the quiet window: " + summary);
    }

    /// <summary>
    /// The anchor is not decoration either: a re-drive that DOES reach the mirror inside the window
    /// is caught by the stream half on its own, so the two witnesses are genuinely independent
    /// (a drive that never made it to the ledger would still be caught).
    /// </summary>
    [Fact]
    public async Task TheNewNegativeAssertion_CatchesADriveThatOnlyTheMirrorSaw()
    {
        var ledger = new NodeTypeCompileParkRegistry();
        var mirror = new ReplaySubject<MeshNode>(1);
        var settled = Node(version: 12, CompilationStatus.Error, failedInputs: Inputs);
        mirror.OnNext(settled);

        var window = RedriveObservation.OpenWindow(ledger, TypePath, settled.Version);
        // A fresh drive lands on the node and NOTHING is recorded in the ledger.
        mirror.OnNext(Node(13, CompilationStatus.Pending, failedInputs: null));

        Func<Task> rejects = () => window.AssertNothingWasDriven(
            mirror, 1.Seconds(), "a re-failed type must be left alone");
        var thrown = await rejects.Should().ThrowAsync<ObservableAssertionException>(
            "the stream half must catch a drive the ledger never saw");
        output.WriteLine("Stream half REJECTED an unledgered drive: " + thrown.Which.Message);
    }

    /// <summary>
    /// …and the anchor does not make the stream half blind: the version clause rejects only the
    /// replay of the snapshot the window OPENED on, never a genuinely newer one. Pinned by feeding
    /// the predicate both directly.
    /// </summary>
    [Fact]
    public void TheWatermarkRejectsOnlyTheReplay()
    {
        var settled = Node(version: 12, CompilationStatus.Compiling, failedInputs: null);
        RedriveObservation.IsFreshCompileDrive(settled, watermark: 12).Should().BeFalse(
            "this IS the snapshot the window opened on, handed back by Replay(1)");
        RedriveObservation.IsFreshCompileDrive(
            Node(13, CompilationStatus.Compiling, failedInputs: null), watermark: 12)
            .Should().BeTrue("a strictly newer revision is a genuine fresh drive");
        RedriveObservation.IsFreshCompileDrive(
            Node(13, CompilationStatus.Error, failedInputs: Inputs), watermark: 12)
            .Should().BeFalse("a newer revision that is not a compile drive is not one");
    }
}
