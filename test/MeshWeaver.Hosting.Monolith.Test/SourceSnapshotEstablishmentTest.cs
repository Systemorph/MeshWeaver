using System;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Threading.Tasks;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using Xunit;

namespace MeshWeaver.Hosting.Monolith.Test;

/// <summary>
/// The pure, deterministic half of #1218 — no mesh, no Roslyn. These pin, at the exact seams that
/// make them, the decisions <see cref="SourceDiscoveryUnavailableTest"/> exercises end-to-end:
/// a source set that could not be ESTABLISHED never reaches Roslyn and never becomes a compile
/// verdict, while a real compile error is untouched.
/// </summary>
public class SourceSnapshotEstablishmentTest
{
    private static MeshNode Source(string path) =>
        new(path[(path.LastIndexOf('/') + 1)..], path[..path.LastIndexOf('/')])
        {
            Name = "Code",
            NodeType = "Code",
        };

    /// <summary>
    /// 🚨 THE defect, in one assertion. The direct probe issues one query per declared source
    /// group. One of them dies (a <c>shared=</c> query into a starved partition); the others
    /// return rows. The old shape swallowed the dead leg (<c>.Catch(_ =&gt; empty)</c>) and let the
    /// SURVIVING rows win the race — a set that was short by exactly the file the dead query owned,
    /// which is how <c>CS0246: 'ScopeLibrary' could not be found</c> came out of a healthy tree.
    /// A snapshot that knows a query died must report UNESTABLISHED, never its partial remainder.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task FailedSourceQuery_ReportsUnestablished_NeverItsPartialRemainder()
    {
        var ct = TestContext.Current.CancellationToken;
        var partial = new[] { Source("Acme/Scope/Source/self") };
        var probe = Observable.Return(SourceSnapshot.Unavailable(
            "query 'namespace:Shared/Library scope:subtree' → TimeoutException", partial));
        // The cached synced query cannot help either — it replays a latched-EMPTY set (the
        // stale-synced-query class), which has never been allowed to settle the snapshot (#612).
        var cachedFirst = Observable.Return(SourceSnapshot.Established(Array.Empty<MeshNode>()));

        var snapshot = await MeshNodeCompilationService.RaceSourceSnapshot(probe, cachedFirst)
            .FirstAsync().Timeout(TimeSpan.FromSeconds(10)).ToTask(ct);

        snapshot.IsEstablished.Should().BeFalse(
            "one of the source queries did not answer, so NOTHING is known about the source set — "
            + "and a compile against the surviving fragment produces diagnostics about code that "
            + "is perfectly fine (#1218)");
        snapshot.UnestablishedReason.Should().Contain("TimeoutException",
            "the reason must name what died — that is the whole point of making it knowable");
    }

    /// <summary>
    /// The counterweight, and the reason an unestablished report may never win the race outright:
    /// one dead probe leg must not VETO a perfectly healthy cached answer. Discovery has two
    /// independent legs precisely so either can carry the compile.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task FailedProbe_DoesNotVeto_AHealthyCachedAnswerThatArrivesLater()
    {
        var ct = TestContext.Current.CancellationToken;
        // The probe fails FIRST …
        var probe = Observable.Return(SourceSnapshot.Unavailable("query 'x' → TimeoutException"));
        // … the healthy cached query answers a little later.
        var cachedFirst = Observable.Timer(TimeSpan.FromMilliseconds(200))
            .Select(_ => SourceSnapshot.Established(new[] { Source("Acme/Scope/Source/self") }));

        var snapshot = await MeshNodeCompilationService.RaceSourceSnapshot(probe, cachedFirst)
            .FirstAsync().Timeout(TimeSpan.FromSeconds(10)).ToTask(ct);

        snapshot.IsEstablished.Should().BeTrue(
            "the healthy leg established the set — a failure on the OTHER leg must not turn a "
            + "compilable type into an unavailable one");
        snapshot.Sources.Should().ContainSingle();
    }

    /// <summary>
    /// The terminal stamp is where the distinction becomes durable state. Everything downstream —
    /// the instance overlay's wording, <c>EnsureCompileDispatched</c>'s re-dispatch, and the bake
    /// gate's bucket — keys off this one field.
    /// </summary>
    [Fact]
    public void UnestablishedSourceSet_StampsUnavailable_NeverError() =>
        NodeTypeCompilationHelpers.ApplyCompileFailure(
                new NodeTypeDefinition(),
                result: null,
                error: new SourceDiscoveryUnavailableException("query 'x' did not answer"),
                activityPath: null)
            .CompilationStatus.Should().Be(CompilationStatus.Unavailable,
                "the compile never ran, so nothing is known to be wrong with the source — "
                + "CompilationStatus.Unavailable is exactly that statement (#641, #1218)");

    /// <summary>Guard on the other side: nothing else becomes lenient.</summary>
    [Fact]
    public void EveryOtherCompileFailure_StillStampsError()
    {
        NodeTypeCompilationHelpers.ApplyCompileFailure(
                new NodeTypeDefinition(),
                result: null,
                error: new CompilationException("Acme/Type", "CS0246: The type or namespace name 'X' could not be found"),
                activityPath: null)
            .CompilationStatus.Should().Be(CompilationStatus.Error);

        NodeTypeCompilationHelpers.ApplyCompileFailure(
                new NodeTypeDefinition(),
                result: null,
                error: new InvalidOperationException("something else went wrong"),
                activityPath: null)
            .CompilationStatus.Should().Be(CompilationStatus.Error,
                "only an UNESTABLISHED SOURCE SET is a non-verdict — widening this to any "
                + "infrastructure fault would hand the gate a blind spot");
    }

    /// <summary>
    /// The three cases at the gate, side by side — the contract #1218 asks for, and the one a
    /// future change must not quietly collapse back into two.
    /// </summary>
    [Fact]
    public void TheThreeCases_GateDifferently()
    {
        // 1. Discovery FAILED — a non-verdict. Does not gate.
        var unreadable = new NodeTypeBakeGateState { GatesReadiness = true };
        unreadable.MarkOutcome(new PreWarmOutcome(
            "BusinessRules/Scope", PreWarmStatus.TimedOut, "source discovery could not be established")
        { WasHealthyBeforeBake = true });
        unreadable.Phase.Should().NotBe(BakePhase.Regressed);

        // 2. A REAL compile error on an established set — gates.
        var regressed = new NodeTypeBakeGateState { GatesReadiness = true };
        regressed.MarkOutcome(new PreWarmOutcome(
            "BusinessRules/Scope", PreWarmStatus.CompileError, "CS0246 …")
        { WasHealthyBeforeBake = true });
        regressed.Phase.Should().Be(BakePhase.Regressed);

        // 3. Genuinely zero matching sources with a healthy query (#1204) — does not gate.
        var contentBroken = new NodeTypeBakeGateState { GatesReadiness = true };
        contentBroken.MarkOutcome(new PreWarmOutcome(
            "KmuBasics/Buchungsjournal", PreWarmStatus.NoSources, "0 source nodes matched")
        { WasHealthyBeforeBake = true });
        contentBroken.Phase.Should().NotBe(BakePhase.Regressed);
        contentBroken.ContentBroken.Keys.Should().Contain("KmuBasics/Buchungsjournal");
    }
}
