using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Reactive.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using MeshWeaver.Testing.Xunit;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// Plugins#2362: during mesh teardown, callbacks reached a DI resolve on a hub whose Autofac
/// container was already disposed while the hub still read <c>IsShuttingDown == false</c>. This pins
/// the invariant the gate relies on, for every hub kind a mesh holds — the mesh itself, a hosted
/// child, a hosted grandchild, and a per-node hub the router minted: <b>by the first instant the
/// container that roots them is disposed, every one of them is part of a shutdown</b>.
///
/// <para>The instant is measured, not inferred. A <see cref="ContainerEndProbe"/> is a singleton of
/// the fixture's ROOT provider resolved AFTER every hub was created, so Autofac — which disposes in
/// reverse creation order, and marks the scope disposed BEFORE it disposes anything — disposes it
/// first: its <c>Dispose</c> runs at the earliest moment any resolve against the container throws.
/// It records what each hub reads there.</para>
///
/// <para>Both teardown shapes a <see cref="MonolithMeshTestBase"/> class can take are driven through
/// their REAL code: the per-test one (<c>DisposeAsync</c>) and the collection-scoped shared-mesh one
/// (<see cref="TestCollectionScope"/> disposing the provider it cached).</para>
/// </summary>
public class EveryHubShutsDownBeforeItsContainerTest(ITestOutputHelper output)
{
    [Fact]
    public async Task PerTestTeardown_EveryHubIsShuttingDown_WhenItsContainerEnds()
    {
        var fixture = new PerTestFixture(output);
        ContainerEndProbe probe;
        try
        {
            await fixture.InitializeAsync();
            probe = await fixture.ArmAsync("per-test");
        }
        finally
        {
            await fixture.DisposeAsync();
        }

        AssertEveryHubWasShuttingDown(probe);
    }

    [Fact]
    public async Task SharedMeshTeardown_EveryHubIsShuttingDown_WhenItsContainerEnds()
    {
        // The scope the runner opens around a collection, opened here so this test owns the moment
        // the collection's shared mesh is torn down (the same shape TeardownWaitsForCollectibleUnloadsTest
        // uses). `await using` covers every failure path; the explicit dispose below is the subject.
        await using var scope = TestCollectionScope.Begin("Plugins#2362 shared-mesh teardown");
        var probe = await RunOneSharedCaseAsync(output);

        await scope.DisposeAsync();

        AssertEveryHubWasShuttingDown(probe);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<ContainerEndProbe> RunOneSharedCaseAsync(ITestOutputHelper output)
    {
        var fixture = new SharedFixture(output);
        try
        {
            await fixture.InitializeAsync();
            return await fixture.ArmAsync("shared");
        }
        finally
        {
            // The per-test half of a shared class's teardown: it leaves the shared mesh UP.
            await fixture.DisposeAsync();
        }
    }

    private static void AssertEveryHubWasShuttingDown(ContainerEndProbe probe)
    {
        probe.Readings.Should().NotBeNull("the probe must have been disposed by the container it lives in");
        var readings = probe.Readings!;
        string.Join(",", readings.Select(r => r.Kind)).Should().Be(
            "mesh,hosted child,hosted grandchild,per-node hub",
            "every hub kind the mesh holds is measured");
        readings.Where(r => !r.IsShuttingDown).Should().BeEmpty(
            "by the first instant its container is disposed every hub must already be part of a "
            + "shutdown — a hub that reads IsShuttingDown=false there runs its callbacks into a dead "
            + "scope believing it is live (Plugins#2362). Readings: "
            + string.Join("; ", readings.Select(r => r.ToString())));
    }

    /// <summary>What one hub read at the first instant its container was disposed.</summary>
    internal sealed record HubReading(string Kind, string Address, bool IsShuttingDown, MessageHubRunLevel RunLevel)
    {
        public override string ToString() => $"{Kind} {Address}: IsShuttingDown={IsShuttingDown}, RunLevel={RunLevel}";
    }

    /// <summary>
    /// A root-provider singleton, resolved last, whose <c>Dispose</c> reads every watched hub at the
    /// first instant the container is torn down. Instance state only.
    /// </summary>
    internal sealed class ContainerEndProbe : IDisposable
    {
        private ImmutableList<(string Kind, IMessageHub Hub)> watched = [];

        public ImmutableList<HubReading>? Readings { get; private set; }

        public void Watch(string kind, IMessageHub hub) =>
            watched = watched.Add((kind, hub));

        public void Dispose() =>
            Readings ??= watched
                .Select(w => new HubReading(w.Kind, w.Hub.Address.ToString(), w.Hub.IsShuttingDown, w.Hub.RunLevel))
                .ToImmutableList();
    }

    private abstract class Fixture(ITestOutputHelper output) : MonolithMeshTestBase(output)
    {
        protected override MeshBuilder ConfigureMesh(MeshBuilder builder) =>
            base.ConfigureMesh(builder)
                .ConfigureServices(services => services.AddSingleton(_ => new ContainerEndProbe()));

        /// <summary>Creates one hub of each kind, then resolves the probe LAST and arms it on them.</summary>
        public async Task<ContainerEndProbe> ArmAsync(string tag)
        {
            var ct = TestContext.Current.CancellationToken;
            var id = $"{tag}-{Guid.NewGuid():N}";
            var child = Mesh.GetHostedHub(new Address("teardown-probe-child", id));
            var grandchild = child.GetHostedHub(new Address("teardown-probe-grandchild", id));

            var nodePath = $"TeardownProbe{Guid.NewGuid():N}";
            await SeedTopLevel(new MeshNode(nodePath) { Name = nodePath, NodeType = "Markdown" });
            await Mesh.GetWorkspace().GetMeshNodeStream(nodePath)
                .Where(n => n is not null)
                .Take(1)
                .Should().Within(TimeSpan.FromSeconds(30)).Emit("the node's own hub must come up", ct);
            var nodeHub = Mesh.GetHostedHub(new Address(nodePath), HostedHubCreation.Never);
            nodeHub.Should().NotBeNull("reading the node through the mesh activates its per-node hub");

            // Resolved from the ROOT provider, AFTER every hub above: disposed first.
            var probe = ServiceProvider.GetRequiredService<ContainerEndProbe>();
            probe.Watch("mesh", Mesh);
            probe.Watch("hosted child", child);
            probe.Watch("hosted grandchild", grandchild);
            probe.Watch("per-node hub", nodeHub!);
            return probe;
        }
    }

    private sealed class PerTestFixture(ITestOutputHelper output) : Fixture(output);

    private sealed class SharedFixture(ITestOutputHelper output) : Fixture(output)
    {
        protected override bool ShareMeshAcrossTests => true;
    }
}
