using System;
using System.IO;
using System.Reactive;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Threading.Tasks;
using MeshWeaver.ContentCollections;
using MeshWeaver.Fixture;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// 🚨 CAUSE C of the <c>/api/content</c> 503 — <b>the owning node's hub had not finished
/// STARTING</b>. <see href="https://github.com/Systemorph/MeshWeaver/issues/3931">#3931</see>,
/// <i>"every COLD /api/content read 503s after ~10.2 s on memex.meshweaver.cloud"</i>.
///
/// <para><b>The mechanism.</b> <c>ContentFileResolver.Resolve</c> reads the owning node's collection
/// config with a <c>GetDataRequest</c> addressed at THAT NODE'S OWN per-node hub, bounded by
/// <c>ReadBudget.Default</c> (10 s). When the node has not been touched since the process started,
/// that request is what CREATES the hub, and <c>MessageService</c> parks every arriving delivery in
/// the deferred queue until the hub's last initialization gate opens. So the read does not measure
/// the owner's health — it measures the owner's START-UP, and when the start-up runs long the read
/// reports the owner as unreachable and the route answers a retryable <b>503</b> for a node that is
/// present, permitted and about to answer. Measured in production 2026-09-10: two consecutive reads
/// of <c>Doc/Architecture/content/platform-overview.svg</c> 503'd at ~10 s each and the third served
/// 200 in 0.2 s, ~24 s after the first — while five previously-untouched owning nodes served in
/// 0.14–0.20 s once the replica was quiet.</para>
///
/// <para>🚨 <b>Every bound on that start-up is LARGER than the budget waiting on it</b>, which is
/// the ordering <c>ReadBudget</c>'s own remarks forbid — <i>"a bound nested inside another bound
/// must be able to fire FIRST, because it is the only one that knows WHICH read starved"</i>:
/// <c>MessageService.DeferralTimeout</c> 30 s, <c>MessageHub.DefaultInitializationTimeout</c> 120 s,
/// <c>RoutingServiceBase</c> path resolution 30 s, <c>MessageHubGrain.FirstNodeResolutionTimeout</c>
/// 30 s. The reader's 10 s therefore ALWAYS fires first, so no inner bound can ever deliver its
/// diagnosis and every occurrence of Cause C wears Cause A's signature. Full elimination, and the
/// design decision this leaves open, in <c>Doc/Architecture/ContentRoute503</c>.</para>
///
/// <para><b>The repro.</b> No restart, no bake and no load are needed to pin the mechanism — only a
/// per-node hub whose start-up is genuinely in flight. One node's reactive initialization is parked
/// on an <see cref="AsyncSubject{T}"/> the test completes (matched by address, so nothing else in
/// the mesh is affected), which holds its initialization gate exactly as a slow start-up does. With
/// the gate held, the content read must say what it actually observed; once it opens, the SAME read
/// resolves — which is what proves the 503 was a false negative rather than a measurement.</para>
///
/// <para>🚨 <b>What this does NOT claim.</b> It does not make a per-node hub start any faster, and
/// it does not decide whether an interactive read may pay a cold activation at all — that is the
/// open decision recorded on the page. It pins the mechanism and the diagnosis: a read that gave up
/// on a STARTING owner must say so, because the two sessions that read <i>"the owning hub never
/// answered"</i> as evidence both eliminated the one cause that was live.</para>
/// </summary>
public class ContentReadPaysTheOwningHubsStartupTest(ITestOutputHelper output)
    : MonolithMeshTestBase(output)
{
    private const string ProbeNodeType = "ContentRoute503StartupProbe";
    private const string ColdNodeId = "ColdProbe";
    private const string ColdNodePath = $"{TestPartition}/{ColdNodeId}";
    private const string WarmNodeId = "WarmProbe";
    private const string WarmNodePath = $"{TestPartition}/{WarmNodeId}";
    private const string ProbeFileName = "probe.bin";

    /// <summary>The collection's backing directory — per test CLASS instance, so nothing is shared.</summary>
    private readonly string _contentRoot = Path.Combine(
        AppContext.BaseDirectory, "Files", "ContentRoute503Startup", Guid.NewGuid().ToString("N"));

    /// <summary>Producer → test: the parked initialization completes this once it is running.</summary>
    private readonly AsyncSubject<Unit> _parked = new();

    /// <summary>Test → the parked initialization: completing this opens the hub's gate.</summary>
    private readonly AsyncSubject<Unit> _release = new();

    /// <inheritdoc />
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
    {
        Directory.CreateDirectory(_contentRoot);
        return base.ConfigureMesh(builder)
            .AddMeshNodes(
                new MeshNode(ProbeNodeType)
                {
                    Name = "Content Route 503 Start-up Probe",
                    HubConfiguration = config => config
                        // The one thing the node type needs: a content collection, so the per-node
                        // hub answers the collection-config GetDataRequest the route issues.
                        .AddFileSystemContentCollection(
                            ContentCollectionsExtensions.DefaultCollectionName, _ => _contentRoot)
                        // The reactive init overload: the Initialize gate stays closed until this
                        // observable settles, so a delivery arriving meanwhile is DEFERRED — the
                        // production shape, reached here without a sleep or a timer.
                        .WithInitialization(ParkColdProbeStartup)
                },
                new MeshNode(ColdNodeId, TestPartition)
                {
                    Name = "Cold Probe",
                    NodeType = ProbeNodeType,
                },
                new MeshNode(WarmNodeId, TestPartition)
                {
                    Name = "Warm Probe",
                    NodeType = ProbeNodeType,
                });
    }

    /// <summary>
    /// Parks ONLY the cold probe's start-up. Every other hub of this type settles immediately, so
    /// the warm probe is a genuine positive control rather than a differently-timed copy of the
    /// same wait.
    /// </summary>
    private IObservable<Unit> ParkColdProbeStartup(IMessageHub hub)
        => string.Equals(hub.Address.ToString(), ColdNodePath, StringComparison.Ordinal)
            ? Observable.Defer(() =>
            {
                _parked.OnNext(Unit.Default);
                _parked.OnCompleted();
                return _release;
            })
            : Observable.Return(Unit.Default);

    /// <summary>
    /// 🚨 THE REGRESSION. A content read whose owning hub is still STARTING must report what it
    /// observed — and the same read must succeed the moment that hub starts, which is what makes
    /// the 503 a false negative rather than a measurement of the owner's health.
    /// </summary>
    [Fact(Timeout = 180_000)]
    public async Task ContentRead_OnAStartingOwner_SaysSo_AndSucceedsOnceItStarts()
    {
        await File.WriteAllBytesAsync(
            Path.Combine(_contentRoot, ProbeFileName), "probe bytes"u8.ToArray(),
            TestContext.Current.CancellationToken);

        var reference =
            $"{ColdNodePath}/{ContentCollectionsExtensions.DefaultCollectionName}/{ProbeFileName}";

        // Subscribing is what routes to the cold node and therefore what STARTS its hub — so the
        // read has to be in flight before the park can possibly be engaged.
        var coldRead = ContentFileResolver.Resolve(Mesh, reference)
            .Materialize()
            .Should().Within(TestTimeouts.Convergence)
            .Match(n => n.Kind == System.Reactive.NotificationKind.OnError,
                "the read must terminate rather than hang — before #3931 it errors after "
                + "ReadBudget.Default, which is the 503");

        await _parked.Should().Within(TestTimeouts.Convergence)
            .Emit("the owning hub's start-up must actually be in flight, or this test proves nothing");

        try
        {
            var notification = await coldRead;

            notification.Exception.Should().BeOfType<HubUnreachableException>(
                "a lapsed read budget is reported as a retryable HubUnreachableException, which "
                + "BlazorHostingExtensions.ContentFailure maps to 503");
            var message = notification.Exception!.Message;
            message.Should().Contain(ColdNodePath,
                "the diagnostic must name the address that did not answer");
            message.Should().Contain("STILL STARTING",
                "the owner exists in this process and has not reached RunLevel=Started — a read "
                + "DEFERRED behind a start-up is not a lost reply, and saying 'the owning hub never "
                + "answered' is what made two sessions eliminate the one live cause (#3931)");
            message.Should().NotContain("NO LOCAL HUB",
                "the target probe must ask the MESH hub, which hosts every per-node hub — probing "
                + "the reader (portal/reads-{meshId}, which hosts nothing) answered this for every "
                + "read alike, so the clause carried no information at all");
        }
        finally
        {
            // 🚨 In a finally so a failing assertion cannot strand the parked start-up — a hub left
            // mid-initialization holds its gate until the 120 s buildup timeout, and teardown would
            // wait behind it. Releasing it is also the half that makes the diagnosis above a FALSE
            // NEGATIVE rather than a measurement: nothing about the node changed, only its hub
            // finished starting.
            _release.OnNext(Unit.Default);
            _release.OnCompleted();
        }

        var resolution = await ContentFileResolver.Resolve(Mesh, reference)
            .Should().Within(TestTimeouts.Convergence)
            .Emit("the identical read must resolve once the owning hub has started — the node was "
                + "present, permitted and serving all along");

        resolution.Reason.Should().BeNull("the collection config resolved");
        resolution.Resolution.Should().NotBeNull("the probe node serves a 'content' collection");
        resolution.Resolution!.FilePath.Should().Be(ProbeFileName);
    }

    /// <summary>
    /// POSITIVE CONTROL. The same reference shape on a node whose hub starts normally resolves —
    /// so the fact above cannot pass by making every content read fail.
    /// </summary>
    [Fact(Timeout = 180_000)]
    public async Task ContentRead_OnAnOwnerThatStartsNormally_Resolves()
    {
        await File.WriteAllBytesAsync(
            Path.Combine(_contentRoot, ProbeFileName), "probe bytes"u8.ToArray(),
            TestContext.Current.CancellationToken);

        var resolution = await ContentFileResolver.Resolve(
                Mesh,
                $"{WarmNodePath}/{ContentCollectionsExtensions.DefaultCollectionName}/{ProbeFileName}")
            .Should().Within(TestTimeouts.Convergence)
            .Emit("an owning hub that starts normally answers the collection-config read");

        resolution.Reason.Should().BeNull("the collection config resolved");
        resolution.Resolution.Should().NotBeNull("the probe node serves a 'content' collection");
        resolution.Resolution!.Collection.Name.Should().Be(
            ContentCollectionsExtensions.DefaultCollectionName);
        resolution.Resolution.FilePath.Should().Be(ProbeFileName);
    }

    /// <summary>
    /// CONTROL IN THE OTHER DIRECTION. An unresolvable path still answers "no matching node" — fast,
    /// and WITHOUT reaching the bounded read. That is the discrimination the production probe
    /// asserts (present vs absent), and it is what keeps a start-up 503 distinguishable from a 404.
    /// </summary>
    [Fact(Timeout = 180_000)]
    public async Task AnUnresolvablePath_StillAnswersNotFound_WithoutPayingTheBudget()
    {
        var result = await ContentFileResolver.Resolve(
                Mesh, "__probe_absent__/content/__absent__.svg")
            .Should().Within(TestTimeouts.Quick)
            .Emit("an unmatched path returns at path resolution and never reaches the collection-"
                + "config read, so it must answer well inside ReadBudget.Default");

        result.Resolution.Should().BeNull("no node matches this path");
        result.Reason.Should().NotBeNull(
            "the route needs a reason to put in its 404 — an absent asset must never look like an "
            + "unavailable one");
    }
}
