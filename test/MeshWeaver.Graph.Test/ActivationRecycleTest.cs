using System;
using System.Collections.Immutable;
using System.Linq;
using System.Reactive.Linq;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// Type-scoped recycle (<see cref="ActivationRecycle"/>): a request at <c>Admin/_Recycle/{id}</c>
/// makes every process dispose the live activations it HOSTS of the named NodeType — exactly those,
/// once each — and report what it did.
///
/// <para>Each case asserts the positive half next to the negative one: a recycle that disposed
/// NOTHING would pass every "U was spared" assertion, so the T activations' teardown is always
/// awaited first.</para>
/// </summary>
public class ActivationRecycleTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string TypeT = "RecycleTypeT";
    private const string TypeU = "RecycleTypeU";
    private const string T1 = $"{TestPartition}/RecycleT1";
    private const string T2 = $"{TestPartition}/RecycleT2";
    private const string U1 = $"{TestPartition}/RecycleU1";

    /// <inheritdoc />
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder)
            .AddMeshNodes(
                new MeshNode(TypeT) { Name = "Recycle Type T" },
                new MeshNode(TypeU) { Name = "Recycle Type U" },
                new MeshNode("RecycleT1", TestPartition) { Name = "T one", NodeType = TypeT },
                new MeshNode("RecycleT2", TestPartition) { Name = "T two", NodeType = TypeT },
                new MeshNode("RecycleU1", TestPartition) { Name = "U one", NodeType = TypeU },
                new MeshNode("Elsewhere") { Name = "Elsewhere", NodeType = "Markdown" },
                new MeshNode("RecycleT3", "Elsewhere") { Name = "T three", NodeType = TypeT });

    private const string T3 = "Elsewhere/RecycleT3";

    private HostedHubsCollection Hosted => Mesh.ServiceProvider.GetRequiredService<HostedHubsCollection>();

    /// <summary>Activates <paramref name="path"/> (a read routes to its hub) and returns the live hub the mesh hub hosts for it.</summary>
    private async Task<IMessageHub> Activate(string path)
    {
        var node = await ReadNode(path).FirstAsync().Await(TestContext.Current.CancellationToken);
        node.Should().NotBeNull($"{path} is seeded");
        var deadline = DateTime.UtcNow + TestTimeouts.Convergence;
        while (DateTime.UtcNow < deadline)
        {
            var hub = Hosted.Hubs.FirstOrDefault(h =>
                ActivationRecycle.PathOf(h) == path && h.RunLevel == MessageHubRunLevel.Started);
            if (hub is not null)
                return hub;
            await Task.Delay(50, TestContext.Current.CancellationToken);
        }
        throw new TimeoutException($"{path} never appeared among the mesh hub's hosted hubs as Started");
    }

    private static Task Disposed(IMessageHub hub, string because) =>
        hub.DisposalCompleted.Take(1).Should().Within(TestTimeouts.Convergence)
            .Emit(because, cancellationToken: TestContext.Current.CancellationToken);

    private async Task<ImmutableList<ActivationRecycleReport>> AwaitReports(string requestPath, int count)
    {
        var deadline = DateTime.UtcNow + TestTimeouts.Convergence;
        var reports = ImmutableList<ActivationRecycleReport>.Empty;
        while (DateTime.UtcNow < deadline)
        {
            reports = await ActivationRecycle.Reports(RequestHub, requestPath).FirstAsync().Await(TestContext.Current.CancellationToken);
            if (reports.Count >= count)
                return reports;
            await Task.Delay(100, TestContext.Current.CancellationToken);
        }
        return reports;
    }

    /// <summary>
    /// Two live activations of T and one of U: a T recycle disposes exactly the two, records it, and
    /// the next access re-activates each T address on a FRESH hub — which is what lets it bind the
    /// type's newest build.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task ATypeRecycle_DisposesExactlyTheLiveActivationsOfThatType_AndTheyReactivate()
    {
        var t1 = await Activate(T1);
        var t2 = await Activate(T2);
        var u1 = await Activate(U1);

        var ticket = await ActivationRecycle.Request(RequestHub, new ActivationRecycleRequest
        {
            NodeTypes = ImmutableList.Create(TypeT),
            Reason = "test: T published a new build",
            RequestedBy = "ActivationRecycleTest",
        }).FirstAsync().Await(TestContext.Current.CancellationToken);
        ticket.Refusal.Should().BeNull();
        ticket.Path.Should().StartWith(ActivationRecycleRequest.Namespace + "/");

        await Disposed(t1, "T1 is a live activation of T");
        await Disposed(t2, "T2 is a live activation of T");
        u1.RunLevel.Should().Be(MessageHubRunLevel.Started, "U1 is of type U, which nobody asked to recycle");

        var reports = await AwaitReports(ticket.Path!, 1);
        reports.Should().ContainSingle("a monolith is one process, and it reports once");
        reports[0].Matched.Should().Be(2);
        reports[0].Disposed.Should().Be(2);
        string.Join(",", reports[0].Paths.OrderBy(p => p, StringComparer.Ordinal)).Should().Be($"{T1},{T2}");

        var t1Again = await Activate(T1);
        t1Again.Should().NotBeSameAs(t1, "the next access must build a FRESH activation, not find the disposed one");
    }

    /// <summary>
    /// A request heard twice (a redelivered commit) disposes nothing the second time: the activation
    /// that came back after the first recycle is left alone.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task ARedeliveredRequest_DoesNotDisposeAnActivationTwice()
    {
        var t1 = await Activate(T1);
        var ticket = await ActivationRecycle.Request(RequestHub, new ActivationRecycleRequest
        {
            NodeTypes = ImmutableList.Create(TypeT),
            Reason = "test: redelivery",
        }).FirstAsync().Await(TestContext.Current.CancellationToken);
        await Disposed(t1, "the first delivery recycles T1");
        await AwaitReports(ticket.Path!, 1);

        var fresh = await Activate(T1);
        var feed = Mesh.ServiceProvider.GetRequiredService<IMeshChangeFeed>();
        var segments = ticket.Path!.Split('/');
        feed.Publish(new MeshChangeEvent(string.Join('/', segments[..^1]), segments[^1], ticket.Path!,
            MeshChangeKind.Created, ActivationRecycleRequest.NodeType, 1, DateTimeOffset.UtcNow));

        await Task.Delay(ActivationRecycle.BatchInterval * 4, TestContext.Current.CancellationToken);
        fresh.RunLevel.Should().Be(MessageHubRunLevel.Started,
            "the same request was already handled on this process — hearing it again must not recycle the activation that came back");
        (await ActivationRecycle.Reports(RequestHub, ticket.Path!).FirstAsync().Await(TestContext.Current.CancellationToken)).Should().ContainSingle();
    }

    /// <summary>A hub already tearing down is not selected, so it is never sent a second DisposeRequest. Pure.</summary>
    [Fact(Timeout = 120_000)]
    public async Task Select_SkipsAHubThatIsAlreadyTearingDown()
    {
        var t1 = await Activate(T1);
        var t2 = await Activate(T2);
        var down = t2.DisposalCompleted.Take(1);
        t2.Dispose();
        await down.Should().Within(TestTimeouts.Convergence).Emit("T2 is disposed by hand",
            cancellationToken: TestContext.Current.CancellationToken);

        var selected = ActivationRecycle.Select(new[] { t1, t2, t1 }, new ActivationRecycleRequest
        {
            NodeTypes = ImmutableList.Create(TypeT),
            Reason = "pure",
        });
        selected.Should().ContainSingle().Which.Should().BeSameAs(t1,
            "T2 is already down and T1 is listed twice — each live activation is selected exactly once");
    }

    /// <summary>An unknown type, a missing reason and the definition type itself are refused, and nothing is written.</summary>
    [Theory(Timeout = 120_000)]
    [InlineData("NoSuchRecycleType", "a reason", "unknown NodeType")]
    [InlineData(TypeT, "  ", "needs a reason")]
    [InlineData("NodeType", "a reason", "is refused")]
    [InlineData(ActivationRecycleRequest.NodeType, "a reason", "is refused")]
    [InlineData(ActivationRecycleReport.NodeType, "a reason", "is refused")]
    public async Task ARefusedRequest_WritesNothing(string type, string reason, string expected)
    {
        var before = await RequestCount();
        var ticket = await ActivationRecycle.Request(RequestHub, new ActivationRecycleRequest
        {
            NodeTypes = ImmutableList.Create(type),
            Reason = reason,
        }).FirstAsync().Await(TestContext.Current.CancellationToken);

        ticket.Accepted.Should().BeFalse();
        ticket.Refusal.Should().Contain(expected);
        (await RequestCount()).Should().Be(before, "a refused request writes no record and so recycles nothing");
    }

    private async Task<int> RequestCount() =>
        (await MeshQuery.Query<MeshNode>(MeshQueryRequest.FromQuery(
                $"path:{ActivationRecycleRequest.Namespace} scope:children nodeType:{ActivationRecycleRequest.NodeType}"))
            .FirstAsync().Await(TestContext.Current.CancellationToken)).Items.Count();

    /// <summary>Paths narrow a type recycle to those addresses: a live same-type sibling is spared.</summary>
    [Fact(Timeout = 120_000)]
    public async Task APathsNarrowedRecycle_SparesTheSameTypeSibling()
    {
        var ct = TestContext.Current.CancellationToken;
        var t1 = await Activate(T1);
        var t2 = await Activate(T2);
        var ticket = await ActivationRecycle.Request(RequestHub, new ActivationRecycleRequest
        {
            NodeTypes = ImmutableList.Create(TypeT),
            Paths = ImmutableList.Create("/" + T1.ToLowerInvariant() + "/"),
            Reason = "test: one address",
        }).FirstAsync().Await(ct);
        await Disposed(t1, "T1 is the named address (matched slash-trimmed and case-insensitively)");
        var reports = await AwaitReports(ticket.Path!, 1);
        reports[0].Disposed.Should().Be(1);
        t2.RunLevel.Should().Be(MessageHubRunLevel.Started, "T2 is the same type but not the named address");
    }

    /// <summary>UnderPath narrows a type recycle to one subtree: the same type outside it is spared.</summary>
    [Fact(Timeout = 120_000)]
    public async Task AnUnderPathNarrowedRecycle_SparesTheSameTypeOutsideThePath()
    {
        var ct = TestContext.Current.CancellationToken;
        var t1 = await Activate(T1);
        var t3 = await Activate(T3);
        var ticket = await ActivationRecycle.Request(RequestHub, new ActivationRecycleRequest
        {
            NodeTypes = ImmutableList.Create(TypeT),
            UnderPath = "Elsewhere",
            Reason = "test: one subtree",
        }).FirstAsync().Await(ct);
        await Disposed(t3, "T3 is under Elsewhere");
        var reports = await AwaitReports(ticket.Path!, 1);
        reports[0].Paths.Should().ContainSingle().Which.Should().Be(T3);
        t1.RunLevel.Should().Be(MessageHubRunLevel.Started, "T1 is the same type outside the path");
    }

    /// <summary>The id is a dedupe key: a second request with it answers the first and writes nothing.</summary>
    [Fact(Timeout = 120_000)]
    public async Task ARequestId_IsADedupeKey_AndAMalformedIdIsRefused()
    {
        var ct = TestContext.Current.CancellationToken;
        var request = new ActivationRecycleRequest { NodeTypes = ImmutableList.Create(TypeU), Reason = "test: dedupe" };
        var first = await ActivationRecycle.Request(RequestHub, request, "dedupe-1").FirstAsync().Await(ct);
        var count = await RequestCount();
        var second = await ActivationRecycle.Request(RequestHub, request, "dedupe-1").FirstAsync().Await(ct);
        second.Path.Should().Be(first.Path);
        (await RequestCount()).Should().Be(count, "the same id writes no second request");
        var malformed = await ActivationRecycle.Request(RequestHub, request, "dedupe.1").FirstAsync().Await(ct);
        malformed.Accepted.Should().BeFalse("'dedupe.1' would collapse onto 'dedupe-1' if it were rewritten");
    }

    /// <summary>
    /// 🚨 A request node NOT written by System — someone who can write Admin/_Recycle directly — is
    /// refused by every process: nothing is disposed and no report is written.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task ARequestNotWrittenBySystem_DisposesNothing()
    {
        var ct = TestContext.Current.CancellationToken;
        var t1 = await Activate(T1);
        var path = $"{ActivationRecycleRequest.Namespace}/forged-{Guid.NewGuid():N}"[..40];
        await NodeFactory.CreateNode(new MeshNode(path[(path.LastIndexOf('/') + 1)..], ActivationRecycleRequest.Namespace)
        {
            Name = "forged",
            NodeType = ActivationRecycleRequest.NodeType,
            Content = new ActivationRecycleRequest { NodeTypes = ImmutableList.Create(TypeT), Reason = "forged", RequestedAt = DateTimeOffset.UtcNow },
        }).FirstAsync().Await(ct);
        var written = await MeshQuery.Query<MeshNode>(MeshQueryRequest.FromQuery($"path:{path}").AsSystem())
            .Select(c => c.Items.Single()).FirstAsync().Await(ct);
        written.CreatedBy.Should().NotBe(WellKnownUsers.System, "the precondition: this write is NOT System's");

        await Task.Delay(ActivationRecycle.BatchInterval * 4, ct);
        t1.RunLevel.Should().Be(MessageHubRunLevel.Started, "a request System did not write is refused on every process");
        (await ActivationRecycle.Reports(RequestHub, path).FirstAsync().Await(ct)).Should().BeEmpty();
    }
}
