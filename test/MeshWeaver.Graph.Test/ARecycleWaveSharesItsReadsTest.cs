using System;
using System.Collections.Immutable;
using System.Linq;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using MeshWeaver.Reactive.Assertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// 🚨 <b>A recycle WAVE asks the index each question once while it is in flight, not once per
/// leg</b> (#6520).
///
/// <para>Measured on a portal pod: one wave ran a cascade per recycled type, each issued the same
/// <c>nodeType:NodeType partitions:all</c> enumeration inside one second, the Postgres provider
/// emitted no Initial within the fan-in's bound for ANY of them, and every leg was reported
/// INCOMPLETE. A read of the same window for stalls on any other query found none (against a
/// positive control that found the enumeration's): the cascade made its own load.</para>
///
/// <para><b>SHOULD-FAIL-IF</b> the cascade issues its own enumeration again: a cascade started while
/// a shared read is in flight would then answer from its own query instead of waiting for that read,
/// and the read counter would grow with every concurrent caller.</para>
/// </summary>
public class ARecycleWaveSharesItsReadsTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string CascadeNodeType = "RecycleWaveGadget";
    private const string InstanceId = "RecycleWaveInstance";
    private const string InstancePath = $"{TestPartition}/{InstanceId}";
    private const int Wave = 20;

    /// <inheritdoc />
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder)
            .AddMeshNodes(
                new MeshNode(CascadeNodeType) { Name = "Recycle Wave Gadget" },
                new MeshNode(InstanceId, TestPartition) { Name = "Recycle Wave Instance", NodeType = CascadeNodeType });

    private RecycleCascadeReads Reads => Mesh.ServiceProvider.GetRequiredService<RecycleCascadeReads>();
    private IMessageHub Owner => Mesh.ReadIssuingHub();

    /// <summary>
    /// The mechanism: concurrent callers share ONE read while it is in flight and all receive its
    /// answer; once it settles it is released, so the next caller reads afresh.
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task ConcurrentCallers_ShareOneRead_AndTheNextWaveReadsAfresh()
    {
        var ct = TestContext.Current.CancellationToken;
        var reads = 0;
        var gate = new Subject<ImmutableDictionary<string, NodeTypeDefinition?>>();
        IObservable<ImmutableDictionary<string, NodeTypeDefinition?>> Read()
        {
            Interlocked.Increment(ref reads);
            return gate;
        }

        using var connections = new CompositeDisposable();
        var callers = Enumerable.Range(0, Wave).Select(_ =>
        {
            var caller = Reads.Definitions(Owner, Read).Replay(1);
            connections.Add(caller.Connect());
            return caller;
        }).ToList();

        var answer = ImmutableDictionary<string, NodeTypeDefinition?>.Empty.Add("A/Type", null);
        gate.OnNext(answer);
        foreach (var caller in callers)
            (await caller.Should().Within(TestTimeouts.Convergence).Emit("every caller of the wave gets the shared answer", ct))
                .Should().BeSameAs(answer);
        Volatile.Read(ref reads).Should().Be(1, $"{Wave} concurrent callers must issue ONE read, not one each");

        var next = new Subject<ImmutableDictionary<string, NodeTypeDefinition?>>();
        var later = Reads.Definitions(Owner, () =>
        {
            Interlocked.Increment(ref reads);
            return next;
        }).Replay(1);
        connections.Add(later.Connect());
        next.OnNext(ImmutableDictionary<string, NodeTypeDefinition?>.Empty);
        (await later.Should().Within(TestTimeouts.Convergence).Emit("a later caller reads afresh", ct))
            .Should().BeEmpty();
        Volatile.Read(ref reads).Should().Be(2, "a settled read is released — nothing is cached past the wave");
    }

    /// <summary>A fault reaches every caller of the read that faulted, and is never replayed to a later one.</summary>
    [Fact(Timeout = 60_000)]
    public async Task AFault_ReachesItsCallers_AndIsNeverReplayed()
    {
        var ct = TestContext.Current.CancellationToken;
        var gate = new Subject<ImmutableList<string>>();
        using var connections = new CompositeDisposable();
        var callers = Enumerable.Range(0, 3).Select(_ =>
        {
            var caller = Reads.Instances("A/Type", Owner, () => gate).Materialize().Replay(1);
            connections.Add(caller.Connect());
            return caller;
        }).ToList();
        gate.OnError(new TimeoutException("stalled"));
        foreach (var caller in callers)
            (await caller.Should().Within(TestTimeouts.Convergence).Emit("the fault arrives", ct))
                .Exception.Should().BeOfType<TimeoutException>();

        var fresh = await Reads.Instances("A/Type", Owner,
                () => Observable.Return(ImmutableList.Create("A/instance")))
            .Should().Within(TestTimeouts.Convergence).Emit("the next caller reads afresh", ct);
        fresh.Should().ContainSingle().Which.Should().Be("A/instance");
    }

    /// <summary>
    /// 🚨 Settled reads leave nothing behind on the owning hub. The read-issuing hub lives as long as
    /// the mesh and its disposal registry is append-only, so a read that registered an owner of its
    /// own with that hub grew the registry by one per read, for the life of the process.
    /// SHOULD-FAIL-IF each read registers its own owner with the hub again.
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task SettledReads_DoNotGrowTheOwningHubsDisposalRegistry()
    {
        var ct = TestContext.Current.CancellationToken;
        var owner = (MessageHub)Owner;

        // One read first, so the owner registry this singleton keeps for the hub exists.
        (await Reads.Instances("Warm/Type", Owner, () => Observable.Return(ImmutableList<string>.Empty))
            .Should().Within(TestTimeouts.Convergence).Emit("the warm-up read answers", ct)).Should().BeEmpty();
        var before = owner.DisposalRegistrantCount;

        const int waves = 25;
        for (var i = 0; i < waves; i++)
        {
            (await Reads.Instances($"Wave{i}/Type", Owner, () => Observable.Return(ImmutableList.Create("x")))
                .Should().Within(TestTimeouts.Convergence).Emit("each read answers", ct)).Should().ContainSingle();
            (await Reads.Definitions(Owner, () => Observable.Return(ImmutableDictionary<string, NodeTypeDefinition?>.Empty))
                .Should().Within(TestTimeouts.Convergence).Emit("each enumeration answers", ct)).Should().BeEmpty();
        }

        // Other services may register with the same hub meanwhile, so the bound is loose — but it is
        // far below one registrant per read (2 × waves = 50).
        (owner.DisposalRegistrantCount - before).Should().BeLessThan(waves,
            $"{2 * waves} settled reads must not each leave a registrant on the long-lived read hub");
    }

    /// <summary>
    /// The wiring: a cascade started while a NodeType enumeration is in flight JOINS it — it emits
    /// nothing until that read answers and then answers from it — and a whole wave of cascades still
    /// finds the instance.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task ACascade_JoinsTheEnumerationInFlight_AndAWaveStillFindsTheInstance()
    {
        var ct = TestContext.Current.CancellationToken;
        var inFlight = new Subject<ImmutableDictionary<string, NodeTypeDefinition?>>();
        using var connections = new CompositeDisposable();
        var holder = Reads.Definitions(Owner, () => inFlight).Replay(1);
        connections.Add(holder.Connect());

        var joined = NodeTypeRecycleCascade.DependencyNetwork(Mesh, CascadeNodeType).Replay(1);
        connections.Add(joined.Connect());
        await joined.Should().NotEmit(TimeSpan.FromSeconds(2),
            "the cascade must wait for the enumeration already in flight, not issue its own", ct);

        inFlight.OnNext(ImmutableDictionary<string, NodeTypeDefinition?>.Empty);
        var result = await joined.Should().Within(TestTimeouts.Convergence)
            .Emit("the cascade answers from the shared enumeration", ct);
        result.Incomplete.Should().BeEmpty();
        result.Addresses.Should().Contain(InstancePath);

        var wave = Enumerable.Range(0, Wave)
            .Select(_ => NodeTypeRecycleCascade.DependencyNetwork(Mesh, CascadeNodeType))
            .Merge()
            .ToList();
        var results = await wave.Should().Within(TestTimeouts.Convergence)
            .Emit("a wave of cascades completes", ct);
        results.Should().HaveCount(Wave);
        results.Should().OnlyContain(r => r.IsComplete && r.Addresses.Contains(InstancePath));
    }
}
