using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Fixture;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Client;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// Two behaviours the NodeType pages rely on since they became templates, which the template arms
/// (<see cref="NodeTypeAndSettingsPagesAreTemplatesTest"/>) state but do not measure.
///
/// <para><b>1. A projection re-published per emission does not accumulate.</b> The Overview builds
/// its compile panel ONCE and embeds that same control in every emission of its node stream. A
/// control's buildup runs every time the control is rendered — there is no per-instance memo — so
/// <see cref="LayoutProjection.PublishingTo{TControl,T}"/> subscribes its projection again on each
/// emission. What keeps that at ONE live subscription is the renderer: before it re-renders an
/// observable view it disposes everything registered under that area's CHILD areas
/// (<c>LayoutAreaHost.RenderObservable</c> → <c>DisposeChildAreas</c>), and the panel registers its
/// subscription under its own (child) area. This measures it: N emissions open N subscriptions and
/// leave one.</para>
///
/// <para><b>2. The release history is newest-first by the release's own <c>CreatedAt</c>.</b> The
/// list is a query control whose query says <c>sort:CreatedAt-desc</c>. A <see cref="MeshNode"/>
/// has no <c>CreatedAt</c> (its field is <c>CreatedDate</c>), so the selector resolves in the
/// node's CONTENT — <see cref="NodeTypeRelease.CreatedAt"/> — on every backend
/// (<c>QueryEvaluator.ResolveRootSelector</c>; SQL's <c>n.content-&gt;&gt;</c> default). The
/// template test asserts only the query STRING; this runs it.</para>
/// </summary>
public class NodeTypePagesReviewedBehaviourTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string ProbeType = "ReemittingProbeType";
    private const string ProbeSpace = "ReemittingProbe";
    private const string ProbeNode = "ReemittingProbe/Page";
    private const string ProbeArea = "Reemitted";

    private readonly BehaviorSubject<int> emissions = new(0);
    private int opened;
    private int live;

    /// <inheritdoc />
    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration)
        => base.ConfigureClient(configuration).AddLayoutClient();

    /// <inheritdoc />
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder) => base.ConfigureMesh(builder)
        .AddMeshNodes(
            new MeshNode(ProbeType)
            {
                Name = "Re-emitting probe",
                HubConfiguration = config => config.AddLayout(layout => layout
                    .WithView(ProbeArea, (_, _) => ReemittedPage())),
            },
            new MeshNode(ProbeSpace) { NodeType = "Space", Name = ProbeSpace },
            new MeshNode("Page", ProbeSpace) { Name = "Page", NodeType = ProbeType });

    /// <summary>
    /// The Overview's shape: ONE panel instance, created outside the Select, embedded in every
    /// emission. The projection counts its own subscriptions.
    /// </summary>
    private IObservable<UiControl?> ReemittedPage()
    {
        var counted = Observable.Create<string>(observer =>
        {
            Interlocked.Increment(ref opened);
            Interlocked.Increment(ref live);
            var inner = Observable.Never<string>().StartWith("status").Subscribe(observer);
            return () =>
            {
                Interlocked.Decrement(ref live);
                inner.Dispose();
            };
        });
        var panel = Controls.Label("panel").PublishingTo("reemittedProbe", counted);
        return emissions.Select(tick => (UiControl?)Controls.Stack
            .WithView(Controls.Label($"tick {tick}"))
            .WithView(panel));
    }

    [Fact]
    public async Task AProjectionEmbeddedInEveryEmission_KeepsOneLiveSubscription()
    {
        var stream = GetClient().GetWorkspace()
            .GetRemoteStream<JsonElement, LayoutAreaReference>(new Address(ProbeNode), new LayoutAreaReference(ProbeArea));
        var ticks = Walk(stream, ProbeArea).OfType<LabelControl>()
            .Select(label => label.Data?.ToString())
            .Replay();
        using var connection = ticks.Connect();

        await ticks.Should().Within(TestTimeouts.Convergence).Match(t => t == "tick 0", "the first emission renders");
        for (var tick = 1; tick <= 3; tick++)
        {
            var expected = $"tick {tick}";
            emissions.OnNext(tick);
            await ticks.Should().Within(TestTimeouts.Convergence).Match(t => t == expected, "each re-emission renders");
        }

        Volatile.Read(ref opened).Should().Be(4,
            "the buildup runs on every render of the panel, so each of the four emissions subscribes the projection");
        Volatile.Read(ref live).Should().Be(1,
            "the renderer disposes the child areas' registrations before each re-render, so only the "
            + "newest emission's subscription lives — more than one means they accumulate for the life of the page");
    }

    [Fact]
    public async Task TheReleaseHistory_IsNewestFirst_ByTheReleasesOwnCreatedAt()
    {
        var ct = TestContext.Current.CancellationToken;
        var typeId = "ordered-" + Guid.NewGuid().ToString("N")[..8];
        var typePath = $"{TestPartition}/{typeId}";
        await NodeFactory.CreateNode(new MeshNode(typeId, TestPartition)
        {
            Name = typeId,
            NodeType = MeshNode.NodeTypePath,
            State = MeshNodeState.Active,
            Content = new NodeTypeDefinition(),
        }).Should().Within(TestTimeouts.Convergence).Emit(cancellationToken: ct);

        // Written OUT of chronological order, so neither creation order nor path order is the answer.
        var newest = new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero);
        var releases = new (string Id, DateTimeOffset CreatedAt)[]
        {
            ("2", newest.AddDays(-10)),
            ("3", newest),
            ("1", newest.AddDays(-20)),
        };
        var ns = $"{typePath}/{ReleaseNodeType.ReleaseSegment}";
        foreach (var (id, createdAt) in releases)
            await NodeFactory.CreateNode(new MeshNode(id, ns)
            {
                Name = $"Release {id}",
                NodeType = ReleaseNodeType.NodeType,
                State = MeshNodeState.Active,
                Content = new NodeTypeRelease
                {
                    Path = $"{ns}/{id}",
                    NodeTypePath = typePath,
                    Release = id,
                    FrameworkVersion = "test",
                    CreatedAt = createdAt,
                },
            }).Should().Within(TestTimeouts.Convergence).Emit(cancellationToken: ct);

        var query = (string?)NodeTypeLayoutAreas.ReleasesList(typePath).HiddenQuery;
        Assert.NotNull(query);

        var listed = await Mesh.ServiceProvider.GetRequiredService<IMeshService>()
            .Query<MeshNode>(MeshQueryRequest.FromQuery(query))
            .Select(change => (IReadOnlyList<string>)change.Items.Select(n => n.Id).ToList())
            .Should().Within(TestTimeouts.Convergence).Match(ids => ids.Count == 3,
                "the list's own query finds the three releases", cancellationToken: ct);

        listed.Should().Equal(["3", "2", "1"],
            "sort:CreatedAt-desc resolves in the release CONTENT (a MeshNode has no CreatedAt), so the "
            + "history is newest-first by the release's own time — not by creation or path order");
    }

    /// <summary>Every control rendered at <paramref name="area"/> and, recursively, in its sub-areas.</summary>
    private static IObservable<UiControl?> Walk(ISynchronizationStream<JsonElement> stream, string area)
        => stream.GetControlStream(area)
            .Select(control => control is IContainerControl container
                ? container.Areas
                    .Select(named => named.Area?.ToString())
                    .OfType<string>()
                    .Select(subArea => Walk(stream, subArea))
                    .Merge()
                    .StartWith(control)
                : Observable.Return(control))
            .Switch();
}

/// <summary>
/// The compile buttons of the NodeType pages answer the CLICK with what became of the release
/// request. The request checks <c>Permission.Compile</c> — a gate the Overview and Releases buttons
/// did not have while they wrote <c>RequestedReleaseAt</c> through a raw stream update — and a
/// refusal used to end in a hub-side log line: the viewer pressed an enabled button and nothing
/// visible happened. <see cref="NodeTypeLayoutAreas.ReleaseClick"/> turns the answered refusal into
/// the click's error, which the host refuses to the client with the reason
/// (<c>WithReactiveClickAction</c>, Doc/GUI/ButtonPendingState).
///
/// <para>This mesh grants nothing (<c>ConfigureMeshBase</c>): the default test mesh makes everyone
/// an administrator, and a refusal cannot be observed there.</para>
/// </summary>
public class CompileClickRefusalReachesTheClickerTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string TypeId = "RefusedCompileType";

    /// <inheritdoc />
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder) => ConfigureMeshBase(builder)
        .AddMeshNodes(new MeshNode(TypeId)
        {
            Name = "Refused compile",
            NodeType = MeshNode.NodeTypePath,
            Content = new NodeTypeDefinition(),
        });

    [Fact(Timeout = 60000)]
    public async Task WithoutCompile_TheClickFaults_WithTheRefusalsReason()
    {
        TestUsers.DevLogin(Mesh, new AccessContext { ObjectId = "no-compile", Name = "no-compile" });

        var fault = await Outcome(NodeTypeLayoutAreas.ReleaseClick(Mesh, TypeId))
            .Should().Within(TestTimeouts.Convergence).Emit(cancellationToken: TestContext.Current.CancellationToken);

        var refused = Assert.IsType<InvalidOperationException>(fault);
        refused.Message.Should().Contain("Compile permission",
            "the click's error carries the sentence the release request refused with, so the host can "
            + "show it to the person who clicked");
    }

    /// <summary>
    /// The control arm: the same click on the same type by an identity that may compile completes
    /// without an error — so the fault above is the refusal, not a click that can never succeed.
    /// </summary>
    [Fact(Timeout = 60000)]
    public async Task WithCompile_TheClickCompletes()
    {
        var fault = await Mesh.ServiceProvider.GetRequiredService<AccessService>()
            .RunAsSystem(() => Outcome(NodeTypeLayoutAreas.ReleaseClick(Mesh, TypeId)))
            .Should().Within(TestTimeouts.Convergence).Emit(cancellationToken: TestContext.Current.CancellationToken);

        fault.Should().BeNull("the release was requested, so the click is acknowledged");
    }

    /// <summary>The click's terminal: <c>null</c> when it completed, the exception when it faulted.</summary>
    private static IObservable<Exception?> Outcome(IObservable<Unit> click)
        => click.IgnoreElements()
            .Select(_ => (Exception?)null)
            .Catch((Exception ex) => Observable.Return<Exception?>(ex))
            .DefaultIfEmpty(null);
}
