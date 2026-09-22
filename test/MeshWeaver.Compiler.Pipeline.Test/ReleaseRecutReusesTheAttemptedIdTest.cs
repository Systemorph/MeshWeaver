using System;
using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Linq;
using System.Reactive;
using System.Reactive.Concurrency;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// 🚨 Issue #5057 — <b>the release re-cut mints the SAME id its first attempt was minting</b>, so a
/// first attempt that lands after its bound expired is ADOPTED rather than duplicated, and a build
/// that ends with no release is STAMPED as such rather than reading healthy from every field.
///
/// <para><b>The root, as measured.</b> The settle's own release create is bounded by
/// <see cref="NodeTypeBuildState.CreateBound"/>, and that bound stops this process WAITING — not the
/// create, whose message is already on the bus, so the owning hub writes the node whether or not
/// anyone is still listening. Over <c>Hosting/InstanceRequest/Release/*</c> on the control instance
/// (200 nodes, a floor) 8 of 200 creates landed AFTER the bound, out to 17.8 s. When the wait gave
/// up, <c>ReleasePostCondition</c> re-cut — at a FRESH id, because the id encodes the SECOND it was
/// minted in. A fresh id could do neither of the two things a re-cut has to do: it could not adopt
/// the late landing (the collision adoption of #3407 needs the SAME id), and it was not idempotent
/// with the create still in flight — it was a second create racing the first at the same slow owner,
/// which is why it typically expired too (the samples' "Re-cutting…" and "…could not be re-cut" lines
/// sit exactly one bound apart). A pair that both landed left TWO release nodes for identical bytes
/// (<c>20260917173651-dU1GWMZG</c> / <c>20260917173701-dU1GWMZG</c>, both landed, ten seconds
/// apart), and the type went on advertising a build whose release existed unpointed-at.</para>
///
/// <para><b>The control, on a real mesh.</b> The FIRST attempt is driven through the production
/// seam <see cref="NodeTypeBuildState.Bounded"/> on a <see cref="HistoricalScheduler"/>, over a REAL
/// <c>CreateNode</c> whose landing is delivered to the waiter only after the clock has expired the
/// bound — exactly the production shape: the wait gives up, the create lands anyway. Then the
/// production remedy, <see cref="ReleasePostCondition.Restore"/>, is handed that outcome. It must
/// answer with the FIRST attempt's path and leave EXACTLY ONE release node behind. On the code
/// before this change <c>Restore</c> minted a fresh id, so it answered with a different path and a
/// second node existed — measured red against exactly that: reverting the re-cut to
/// <c>reusePath: null</c> fails both assertions.</para>
/// </summary>
public class ReleaseRecutReusesTheAttemptedIdTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private static readonly DateTimeOffset Requested = new(2026, 9, 21, 20, 6, 43, TimeSpan.Zero);

    private IMeshService MeshService => Mesh.ServiceProvider.GetRequiredService<IMeshService>();

    private static ImmutableDictionary<string, long> Sources(long version) =>
        ImmutableDictionary<string, long>.Empty.Add("MyAi/Panel/Source/AiSettingsAreas", version);

    /// <summary>The node as the compile watcher observed it at dispatch: a CONSUMED request and a
    /// release cut for the PREVIOUS build — the incident's <c>MyAi/Panel</c> shape.</summary>
    private static NodeTypeDefinition Consumed() => new()
    {
        Configuration = "config => config",
        CompilationStatus = CompilationStatus.Compiling,
        RequestedReleaseAt = Requested,
        LastReleaseRequestHandledAt = Requested,
        LastCompiledVersion = 3319,
        LatestAssemblyCollection = "local",
        LatestAssemblyPath = "MyAi_Panel/v3319-sdc4cbaa-dc97978288e7.dll",
        LatestReleasePath = "MyAi/Panel/Release/20260920072917-Djt66iSB",
        CompiledSources = Sources(3319),
    };

    /// <summary>The compile that just succeeded — store version 3326, new bytes.</summary>
    private static NodeCompilationResult Built(string typePath) => new(
        AssemblyLocation: $"/cache/{typePath.Replace('/', '_')}/Panel.dll",
        NodeTypeConfigurations: [],
        CompiledSources: Sources(3326),
        Collection: "local",
        ContentPath: $"{typePath.Replace('/', '_')}/v3326-sdc4cbaa-dc97978288e7.dll",
        Version: 3326);

    private static MeshNode ReleaseNodeAt(string releasePath, string typePath, NodeCompilationResult result)
    {
        var releaseNamespace = $"{typePath}/{GraphNodeTypeNames.ReleaseSegment}";
        var version = releasePath[(releaseNamespace.Length + 1)..];
        return new MeshNode(version, releaseNamespace)
        {
            Name = $"Release {version}",
            NodeType = GraphNodeTypeNames.Release,
            MainNode = typePath,
            State = MeshNodeState.Active,
            Content = new NodeTypeRelease
            {
                Path = releasePath,
                NodeTypePath = typePath,
                Release = version[15..],
                Version = version,
                FrameworkVersion = "3.0.0.0",
                CreatedAt = DateTimeOffset.UtcNow,
                AssemblyCollection = result.Collection,
                AssemblyContentPath = result.ContentPath,
                AssemblyStoreVersion = result.Version,
                Status = "Succeeded",
            },
        };
    }

    private async Task<MeshNode> SeedTypeAsync(string typePath)
    {
        var typeNode = MeshNode.FromPath(typePath) with
        {
            Name = typePath,
            NodeType = MeshNode.NodeTypePath,
            State = MeshNodeState.Active,
            Content = Consumed(),
        };
        await MeshService.CreateNode(typeNode)
            .Should().Within(TestTimeouts.Convergence)
            .Emit("the NodeType whose release is re-cut must exist", cancellationToken: TestContext.Current.CancellationToken);
        return typeNode;
    }

    /// <summary>Every release node under <paramref name="typePath"/>, by LISTING — the same
    /// existence idiom the platform uses; a point read of an absent node opens the storm-breaker.</summary>
    private IObservable<IReadOnlyCollection<MeshNode>> ReleasesOf(string typePath) =>
        MeshService
            .Query<MeshNode>(MeshQueryRequest.FromQuery(
                $"path:{typePath}/{GraphNodeTypeNames.ReleaseSegment} scope:children nodeType:{GraphNodeTypeNames.Release}"))
            .Where(c => c.ChangeType == QueryChangeType.Initial)
            .Select(c => (IReadOnlyCollection<MeshNode>)c.Items.ToArray())
            .Take(1);

    /// <summary>
    /// 🚨 THE CASE, end to end on a real mesh: the first attempt's wait expires while its create
    /// lands; the re-cut answers with the FIRST attempt's path; exactly one release node exists.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task AFirstAttemptThatLandsAfterItsBound_IsAdoptedByTheRecut_AtTheSameId()
    {
        var typePath = $"{TestPartition}/RecutReuse{Guid.NewGuid().ToString("N")[..8]}";
        var typeNode = await SeedTypeAsync(typePath);
        var result = Built(typePath);

        // The FIRST attempt's id, minted exactly as TryCreateReleaseNode mints it — the second stamp
        // and the content hash of THESE bytes — and stamped one CreateBound in the PAST, because that
        // is when the first attempt was minted relative to the re-cut: the re-cut runs after the
        // bound expired. 🚨 This is what makes the control a control. Minted in the same second as
        // the re-cut, a fresh id would COLLIDE with it by accident (the #3407 same-second case) and
        // the pre-fix code would pass — measured: with the re-cut reverted to a fresh id this test
        // stayed green until the stamp was moved back a bound.
        var mintedAt = DateTime.UtcNow - NodeTypeBuildState.CreateBound;
        var first = $"{typePath}/{GraphNodeTypeNames.ReleaseSegment}/{mintedAt:yyyyMMddHHmmss}-"
                    + NodeTypeBuildState.ContentHashOf(result);

        // A REAL create at that path. Its landing is captured on `landed`, and delivered to the
        // waiter only through `gate` — which the test opens after the clock has expired the bound.
        // That is the production shape in miniature: the bound stops the WAIT, not the create.
        var landed = new AsyncSubject<Unit>();
        using var creating = MeshService.CreateNode(ReleaseNodeAt(first, typePath, result))
            .Take(1)
            .Subscribe(_ => { landed.OnNext(Unit.Default); landed.OnCompleted(); }, landed.OnError);
        var gate = new AsyncSubject<Unit>();
        var clock = new HistoricalScheduler();
        NodeTypeBuildState.ReleaseCreateOutcome? firstAttempt = null;
        using var waiting = NodeTypeBuildState
            .Bounded(gate.SelectMany(_ => landed), first, clock, logger: null)
            .Subscribe(o => firstAttempt = o, _ => { });
        clock.AdvanceBy(NodeTypeBuildState.CreateBound.Add(TimeSpan.FromTicks(1)));

        firstAttempt.Should().NotBeNull("the bound expired on the clock, so the chain must have answered");
        firstAttempt!.Succeeded.Should().BeFalse("the wait gave up before the landing was delivered");
        firstAttempt.AttemptedPath.Should().Be(first,
            "a failed attempt must still name the id it was minting — it is the only thing a re-cut "
            + "can adopt a late landing at");
        firstAttempt.Failure.Should().Contain(NodeTypeBuildState.CreateBound.ToString());

        // The create outlives the wait: it lands.
        gate.OnNext(Unit.Default);
        gate.OnCompleted();
        await landed.Should().Within(TestTimeouts.Convergence)
            .Emit("the first attempt's create must land — that is what makes this the late-landing shape",
                cancellationToken: TestContext.Current.CancellationToken);

        // THE REMEDY, exactly as the settle path invokes it.
        var settle = await ReleasePostCondition
            .Restore(Mesh, typePath, result, typeNode, activityPath: null, firstAttempt, logger: null)
            .Should().Within(TestTimeouts.Convergence)
            .Emit("Restore always answers exactly once", cancellationToken: TestContext.Current.CancellationToken);

        settle.ReleasePath.Should().Be(first,
            "the re-cut must mint the SAME id as the attempt that expired, so the late landing is "
            + "met as NodeAlreadyExists and adopted — a fresh id answers with a different path and "
            + "leaves the first node unpointed-at, which is the incident");
        settle.UnreleasedBuildPath.Should().BeNull("a release exists for these bytes");
        settle.Diagnosis.Should().NotBeNull("a repaired violation is reported on the activity");
        settle.Diagnosis!.Message.Should().Contain($"Restored at {first}");

        // EXACTLY ONE node under {type}/Release, and it is the first attempt's. The listing is
        // eventually consistent, so wait for it to name the first attempt (which landed) — then
        // the count is a fact about the store, not about the index's lag.
        var releases = await Observable.Interval(TimeSpan.FromMilliseconds(100)).StartWith(0L)
            .SelectMany(_ => ReleasesOf(typePath))
            .Where(items => items.Any(n => string.Equals(n.Path, first, StringComparison.OrdinalIgnoreCase)))
            .Should().Within(TestTimeouts.Convergence)
            .Emit("the listing must name the release that landed", cancellationToken: TestContext.Current.CancellationToken);
        releases.Select(n => n.Path).Should().Equal([first],
            "one attempt, one node: a re-cut at a fresh id would have left a SECOND release node for "
            + "the same bytes beside this one");
    }

    /// <summary>
    /// The guard's other side, on the real mesh: a path whose hash names OTHER bytes is never
    /// reused — the re-cut mints a fresh id for its own bytes and says so.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task APathForOtherBytes_IsNotReused_AndTheRefusalIsLogged()
    {
        var typePath = $"{TestPartition}/RecutForeign{Guid.NewGuid().ToString("N")[..8]}";
        var typeNode = await SeedTypeAsync(typePath);
        var result = Built(typePath);
        var foreign = $"{typePath}/{GraphNodeTypeNames.ReleaseSegment}/20260917173651-dU1GWMZG";

        var sink = new ConcurrentQueue<(LogLevel Level, string Message)>();
        var outcome = await NodeTypeBuildState
            .TryCreateReleaseNode(Mesh, typePath, result, typeNode, activityPath: null,
                new CapturingLogger(sink), reusePath: foreign)
            .Should().Within(TestTimeouts.Convergence)
            .Emit("the create always answers", cancellationToken: TestContext.Current.CancellationToken);

        outcome.Succeeded.Should().BeTrue("nothing stops a fresh create on a healthy mesh");
        outcome.ReleasePath.Should().NotBe(foreign, "a path for other bytes must never be adopted");
        outcome.ReleasePath!.Should().EndWith("-" + NodeTypeBuildState.ContentHashOf(result),
            "the fresh id names THIS compile's bytes");
        sink.Should().Contain(entry => entry.Level == LogLevel.Warning && entry.Message.Contains(foreign),
            "a refused reuse is said out loud, naming the path that was offered");
    }

    // ───────────── the guard, pure ─────────────

    private const string Namespace = "MyAi/Panel/Release";

    [Theory]
    [InlineData("MyAi/Panel/Release/20260922090152-I55LNPmo", "I55LNPmo", true)]
    [InlineData("MyAi/Panel/Release/20260917173651-dU1GWMZG", "dU1GWMZG", true)]
    // a hash that itself contains the dash the id uses — the split is positional, never "last dash"
    [InlineData("MyAi/Panel/Release/20260917173651-a-b_c-d1", "a-b_c-d1", true)]
    [InlineData("MyAi/Panel/Release/20260922090152-I55LNPmo", "dU1GWMZG", false)]
    [InlineData("Store/Plugin/Release/20260922090152-I55LNPmo", "I55LNPmo", false)]
    [InlineData("MyAi/Panel/Release/2026092209015-I55LNPmo", "I55LNPmo", false)]
    [InlineData("MyAi/Panel/Release/2026092209015x-I55LNPmo", "I55LNPmo", false)]
    [InlineData("MyAi/Panel/Release/20260922090152_I55LNPmo", "I55LNPmo", false)]
    [InlineData("", "I55LNPmo", false)]
    [InlineData(null, "I55LNPmo", false)]
    public void APathIsReusable_OnlyForItsOwnBytes(string? attempted, string hash, bool reusable)
        => NodeTypeBuildState.IsReusableAttempt(attempted, Namespace, hash).Should().Be(reusable);

    /// <summary>The hash is the durable content reference, so two replicas compiling the same store
    /// version agree on it — which is what makes cross-attempt reuse name the same bytes.</summary>
    [Fact]
    public void TheContentHash_IsStableOverTheDurableReference_AndMovesWithIt()
    {
        var a = NodeTypeBuildState.ContentHashOf(Built("MyAi/Panel"));
        var b = NodeTypeBuildState.ContentHashOf(Built("MyAi/Panel"));
        var other = NodeTypeBuildState.ContentHashOf(Built("MyAi/Panel") with { ContentPath = "MyAi_Panel/v3343-s6f66941-e1c412a4a43a.dll" });
        a.Should().Be(b).And.HaveLength(8);
        other.Should().NotBe(a, "different store coordinates are different bytes");
    }

    // ───────────── the outcome carries the path it was minting ─────────────

    [Fact]
    public void AFailedAttempt_CarriesThePathItWasMinting_AndALandedOneToo()
    {
        var failed = NodeTypeBuildState.ReleaseCreateOutcome.Failed("the bound expired", Namespace + "/20260922090152-I55LNPmo");
        failed.Succeeded.Should().BeFalse();
        failed.AttemptedPath.Should().Be(Namespace + "/20260922090152-I55LNPmo");

        var landed = NodeTypeBuildState.ReleaseCreateOutcome.Landed(Namespace + "/20260922090152-I55LNPmo");
        landed.AttemptedPath.Should().Be(landed.ReleasePath);

        NodeTypeBuildState.ReleaseCreateOutcome.NotAttempted.AttemptedPath.Should().BeNull();
        NodeTypeBuildState.ReleaseCreateOutcome.Failed("could not be composed").AttemptedPath.Should().BeNull(
            "a failure before any id existed has none to carry");
    }

    /// <summary>Through the real chain: an expired bound hands the path out with the reason.</summary>
    [Fact]
    public void AnExpiredBound_HandsThePathOut_ThroughTheRealChain()
    {
        var path = Namespace + "/20260922090152-I55LNPmo";
        var clock = new HistoricalScheduler();
        NodeTypeBuildState.ReleaseCreateOutcome? seen = null;
        using var subscription = NodeTypeBuildState
            .Bounded(new Subject<Unit>(), path, clock, logger: null)
            .Subscribe(o => seen = o, _ => { });
        clock.AdvanceBy(NodeTypeBuildState.CreateBound.Add(TimeSpan.FromTicks(1)));

        seen.Should().NotBeNull();
        seen!.AttemptedPath.Should().Be(path);
        seen.Failure.Should().Contain(path);
    }

    // ───────────── the stamped state ─────────────

    /// <summary>
    /// 🚨 A build that ends with no release is STAMPED as such. Before this the settle wrote
    /// <c>LatestReleasePath = previous</c> and nothing else, so the incident's node read healthy from
    /// every field (<c>get @Hosting/InstanceRequest</c>: status Ok, sources current, an assembly
    /// built, a release path present — cut the previous day).
    /// </summary>
    [Fact]
    public void ASettleWithNoRelease_StampsThePathAndTheReason()
    {
        var path = Namespace + "/20260922090152-I55LNPmo";
        var stamped = NodeTypeCompilationHelpers.ApplyCompileSuccess(
            Consumed(), Built("MyAi/Panel"), currentNodeVersion: 3420, activityPath: null, releasePath: null,
            unreleasedBuildPath: path, unreleasedBuildReason: "the create did not land within 00:00:10");

        stamped.LatestReleasePath.Should().Be("MyAi/Panel/Release/20260920072917-Djt66iSB",
            "the previous release is kept — that part was always right");
        stamped.UnreleasedBuildPath.Should().Be(path, "…but the node now SAYS this build has no release, and where to look");
        stamped.UnreleasedBuildReason.Should().Be("the create did not land within 00:00:10");
    }

    /// <summary>A landed release clears the stamp — and so does a settle with nothing to say, because
    /// the stamp describes THIS build, never an earlier one.</summary>
    [Fact]
    public void ALandedRelease_ClearsTheStamp_AndSoDoesASettleWithNothingToSay()
    {
        var path = Namespace + "/20260922090152-I55LNPmo";
        var standing = Consumed() with { UnreleasedBuildPath = path, UnreleasedBuildReason = "expired" };

        var landed = NodeTypeCompilationHelpers.ApplyCompileSuccess(
            standing, Built("MyAi/Panel"), 3420, activityPath: null,
            releasePath: Namespace + "/20260922093344-I55LNPmo",
            unreleasedBuildPath: path, unreleasedBuildReason: "expired");
        landed.UnreleasedBuildPath.Should().BeNull("a release exists for this build");
        landed.UnreleasedBuildReason.Should().BeNull();

        var silent = NodeTypeCompilationHelpers.ApplyCompileSuccess(
            standing, Built("MyAi/Panel"), 3420, activityPath: null, releasePath: null);
        silent.UnreleasedBuildPath.Should().BeNull("the stamp describes THIS build, and this settle said nothing about it");
        silent.UnreleasedBuildReason.Should().BeNull("the reason never outlives the path");
    }

    /// <summary>The two members are mesh-owned compile state: masked by the sync seams and mirrored
    /// on the compile-state satellite, like every other release pointer.</summary>
    [Fact]
    public void TheStampedMembers_AreMaskedAndMirrored()
    {
        NodeTypeOperationalContent.MemberNames.Should().Contain("unreleasedBuildPath");
        NodeTypeOperationalContent.MemberNames.Should().Contain("unreleasedBuildReason");
        var state = NodeTypeCompileState.FromDefinition(
            Consumed() with { UnreleasedBuildPath = "p", UnreleasedBuildReason = "r" })!;
        state.UnreleasedBuildPath.Should().Be("p");
        state.UnreleasedBuildReason.Should().Be("r");
    }

    private sealed class CapturingLogger(ConcurrentQueue<(LogLevel Level, string Message)> sink) : ILogger
    {
        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => sink.Enqueue((logLevel, formatter(state, exception)));

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();
            public void Dispose() { }
        }
    }
}
