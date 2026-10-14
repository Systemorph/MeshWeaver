using System;
using System.Collections.Immutable;
using System.Linq;
using System.Reactive;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using MeshWeaver.Reactive.Assertions;
using Xunit;

namespace MeshWeaver.Data.Test;

/// <summary>A request the owner answers on its own turn: once it is answered, everything the
/// client posted before it has been handled by the owner.</summary>
public record ColdStoreBarrierRequest : IRequest<ColdStoreBarrierResponse>;

/// <summary>The answer to <see cref="ColdStoreBarrierRequest"/>.</summary>
public record ColdStoreBarrierResponse;

/// <summary>
/// 🚨 <b>#6594 — a write that waits for a cold store keeps its place.</b>
///
/// <para>A cross-hub MeshNode write whose merge turn finds the owner's store still empty waits for
/// the store to load. It used to wait by posting a fresh merge turn when the load arrived — at the
/// BACK of the store's queue — so every write queued meanwhile committed first and the delayed one
/// then committed on top of them: the last value posted did not stay.</para>
///
/// <para><b>The levers are deterministic.</b> The owner here is a plain data hub whose MeshNode
/// collection starts EMPTY, so write 1 provably takes the cold-store path. The store's turn is then
/// parked by a test-posted update that, once released, loads the node; writes 2..n are posted while
/// it is parked, and a barrier the owner answers on its own turn proves they are queued behind it.
/// So the load lands with writes 2..n already queued and write 1 still waiting — the exact window —
/// with no sleep.</para>
///
/// <para><b>The control</b> runs the same writes against a store that already holds the node: they
/// take the ordinary path and commit in order. It shows the order assertion is met by the normal
/// path, so the cold-store test fails only when the cold-store path reorders.</para>
/// </summary>
public class ColdStoreWritesKeepTheirOrderTest(ITestOutputHelper output) : HubTestBase(output)
{
    private const int Writes = 5;
    private const string NodeId = "cold-order";
    private static readonly MeshNode Node = new(NodeId, "ns") { Name = "w0" };

    /// <inheritdoc />
    protected override MessageHubConfiguration ConfigureHost(MessageHubConfiguration configuration) =>
        base.ConfigureHost(configuration)
            .WithTypes(typeof(ColdStoreBarrierRequest), typeof(ColdStoreBarrierResponse), typeof(MeshNodeReference))
            .WithHandler<ColdStoreBarrierRequest>((hub, request) =>
            {
                hub.Post(new ColdStoreBarrierResponse(), o => o.ResponseFor(request));
                return request.Processed();
            })
            .AddData(data =>
            {
                data.Workspace.Hub.TypeRegistry.WithType(typeof(MeshNode), nameof(MeshNode));
                return data
                    .Configure(reduce => reduce
                        // The owner's own node, reduced off the primary store — the shape the
                        // MeshNode data source registers, cut down to one node.
                        .ForReducedStream<InstanceCollection>(reduced => reduced
                            .AddWorkspaceReference<MeshNodeReference, MeshNode>((current, _, _) =>
                                new ChangeItem<MeshNode>(
                                    current.Value?.Instances.Values.OfType<MeshNode>().FirstOrDefault()!,
                                    current.StreamId, current.Version)))
                        .AddWorkspaceReferenceStream<MeshNode>((workspace, reference, config) =>
                            reference is MeshNodeReference meshReference
                                ? Primary(workspace)
                                    ?.ReduceShared<InstanceCollection>(new CollectionReference(nameof(MeshNode)))
                                    ?.Reduce((WorkspaceReference<MeshNode>)meshReference, config)
                                : null))
                    .AddSource(source => source.WithType<MeshNode>(type => type.WithKey(n => n.Id)));
            });

    /// <inheritdoc />
    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration) =>
        base.ConfigureClient(configuration)
            .WithTypes(typeof(ColdStoreBarrierRequest), typeof(ColdStoreBarrierResponse), typeof(MeshNodeReference));

    private static ISynchronizationStream<EntityStore>? Primary(IWorkspace workspace)
        => workspace.DataContext.GetDataSourceForType(typeof(MeshNode))?.GetStreamForPartition(null);

    private static string Name(int i) => $"w{i}";

    /// <summary>Posts a rename of the owner's node now; the returned observable carries its ack.</summary>
    private static IConnectableObservable<bool> PostRename(IMessageHub client, string name)
    {
        var ack = client
            .Observe<PatchDataResponse>(
                new PatchDataRequest(new MeshNodeReference(), new RawJson("{\"name\":\"" + name + "\"}")),
                o => o.WithTarget(CreateHostAddress()))
            .Take(1)
            .Select(r => r.Message.Success)
            .Replay(1);
        ack.Connect();
        return ack;
    }

    /// <summary>Returns once the owner has handled everything <paramref name="client"/> posted before.</summary>
    private static Task Barrier(IMessageHub client, string because)
        => client.Observe<ColdStoreBarrierResponse>(new ColdStoreBarrierRequest(), o => o.WithTarget(CreateHostAddress()))
            .Should().Within(TestTimeouts.Convergence).Emit(because);

    /// <summary>The node names the owner's store committed, in commit order, from its current state on.</summary>
    private static IConnectableObservable<ImmutableList<string>> CommittedNames(ISynchronizationStream<EntityStore> primary)
    {
        var names = primary
            .Select(change => change.Value?.GetCollection(nameof(MeshNode))?.Instances.Values
                .OfType<MeshNode>().FirstOrDefault()?.Name)
            .Where(name => name is not null)
            .DistinctUntilChanged()
            .Scan(ImmutableList<string>.Empty, (seen, name) => seen.Add(name!))
            .Replay(1);
        names.Connect();
        return names;
    }

    private static ChangeItem<EntityStore> LoadNode(ISynchronizationStream<EntityStore> primary, EntityStore? store)
        => primary.ApplyChanges(new EntityStoreAndUpdates(
            (store ?? new EntityStore()).Update(nameof(MeshNode), c => c.Update(NodeId, Node)),
            [new EntityUpdate(nameof(MeshNode), NodeId, Node)],
            primary.StreamId));

    /// <summary>The load, then every write in posting order.</summary>
    private static string[] Expected
        => new[] { Node.Name! }.Concat(Enumerable.Range(1, Writes).Select(Name)).ToArray();

    /// <summary>
    /// 🚨 THE REGRESSION. Write 1 meets an empty store; the load lands while writes 2..n are queued.
    /// Before the fix the store committed <c>w0 w2 w3 w4 w5 w1</c> and kept <c>w1</c>; after it,
    /// <c>w0 w1 … w5</c>, keeping the last write posted.
    /// </summary>
    [HubFact]
    public async Task WritesQueuedBehindAColdStoreWrite_CommitInTheOrderTheyWerePosted()
    {
        var client = GetClient();
        var host = GetHost();
        await Barrier(client, "the owner is up");
        var primary = Primary(host.GetWorkspace())
            ?? throw new InvalidOperationException("the owner has no MeshNode store");
        var committed = CommittedNames(primary);

        var acks = ImmutableList.Create(PostRename(client, Name(1)));
        await Barrier(client, "write 1's merge turn is on the store's queue, ahead of anything posted later");

        var release = 0;
        var parked = new AsyncSubject<Unit>();
        try
        {
            primary.Update(store =>
            {
                parked.OnNext(Unit.Default);
                parked.OnCompleted();
                // The store's turn is the subject: held until the later writes are queued behind
                // it, then it commits the load that write 1 is waiting for.
                SpinWait.SpinUntil(() => Volatile.Read(ref release) == 1, TestTimeouts.Convergence);
                return LoadNode(primary, store);
            }, ex => Output.WriteLine($"load failed: {ex}"));
            await parked.Should().Within(TestTimeouts.Convergence)
                .Emit("the store's turn is parked behind write 1, which found the store empty");

            for (var i = 2; i <= Writes; i++)
                acks = acks.Add(PostRename(client, Name(i)));
            await Barrier(client, "writes 2..n are queued on the store, behind the parked load");
        }
        finally
        {
            Volatile.Write(ref release, 1);
        }

        foreach (var (ack, i) in acks.Select((a, i) => (a, i + 1)))
            (await ack.Should().Within(TestTimeouts.Convergence).Emit($"write {i} is answered"))
                .Should().BeTrue($"write {i} must commit");

        var names = await committed.Where(seen => seen.Count >= Expected.Length)
            .Should().Within(TestTimeouts.Convergence).Emit("every write has committed");
        names.Should().Equal(Expected,
            "writes from one sender commit in the order they were posted — the write that waited for "
            + "the cold store must not be re-run behind the writes queued while it waited (#6594)");
    }

    /// <summary>
    /// The control: the same writes against a store that already holds the node take the ordinary
    /// path, and commit in order. The lane is not involved.
    /// </summary>
    [HubFact]
    public async Task WritesOnALoadedStore_CommitInTheOrderTheyWerePosted()
    {
        var client = GetClient();
        var host = GetHost();
        await Barrier(client, "the owner is up");
        var primary = Primary(host.GetWorkspace())
            ?? throw new InvalidOperationException("the owner has no MeshNode store");
        primary.Update(store => LoadNode(primary, store), ex => Output.WriteLine($"load failed: {ex}"));
        await primary.Where(c => c.Value?.GetCollection(nameof(MeshNode))?.Instances.Count > 0)
            .Should().Within(TestTimeouts.Convergence).Emit("the node is loaded before any write");
        var committed = CommittedNames(primary);

        var acks = Enumerable.Range(1, Writes).Select(i => PostRename(client, Name(i))).ToArray();
        foreach (var (ack, i) in acks.Select((a, i) => (a, i + 1)))
            (await ack.Should().Within(TestTimeouts.Convergence).Emit($"write {i} is answered"))
                .Should().BeTrue($"write {i} must commit");

        var names = await committed.Where(seen => seen.Count >= Expected.Length)
            .Should().Within(TestTimeouts.Convergence).Emit("every write has committed");
        names.Should().Equal(Expected, "writes on a loaded store commit in posting order");
    }
}
