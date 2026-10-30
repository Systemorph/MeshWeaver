using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Hosting.Persistence;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Testing.FaultInjection;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// #7347: a <see cref="CreateNodeRequest"/> is an INSERT, and the STORE decides it — so of two creates
/// of one path, exactly one is told it created the node.
///
/// <para><b>The measured failure.</b> One log incident opened two GitHub issues in the same second
/// (core #7576/#7577, 2026-10-29), after the per-attempt filing claim of MeshWeaver.Plugins#4295 was
/// deployed: that claim is a create-only node, and the claim node's history shows TWO creators
/// (<c>claimedAt</c> 17:07:42.398 and 17:07:42.416) that were both granted. The create handler decides
/// existence with a READ and then commits with the ordinary upsert, which applies at EQUAL versions —
/// so two creates that both read "absent" both commit version 1, last-write-wins, and both answer
/// Success.</para>
///
/// <para><b>The isolated fault.</b> The create is paused at the store after its existence read saw
/// nothing (<see cref="FaultInjectingStorageAdapter.HoldWrites"/>, a controlled switch). Another
/// process's create of the same path then commits DIRECTLY on the store of record — beneath every
/// decorator of this process, exactly as a second replica sharing the database does. Released, the
/// paused create must be refused with <see cref="NodeCreationRejectionReason.NodeAlreadyExists"/> and
/// must not overwrite the winner. Negative control: the identical paused create with no foreign
/// create succeeds.</para>
/// </summary>
public class ACreateIsAnInsertDecidedByTheStoreTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    // ONE database, two processes: this mesh's adapter, and a second adapter over the same rows with
    // its OWN change feed — a replica whose commit this mesh is never told about.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, MeshNode> _rows =
        new(System.StringComparer.OrdinalIgnoreCase);
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, System.Collections.Generic.List<object>> _objects =
        new(System.StringComparer.OrdinalIgnoreCase);
    private InMemoryStorageAdapter? _thisProcess;
    private InMemoryStorageAdapter? _otherProcess;
    private InMemoryStorageAdapter StoreOfRecord => _thisProcess ??= new InMemoryStorageAdapter(_rows, _objects);
    private InMemoryStorageAdapter OtherProcess => _otherProcess ??= new InMemoryStorageAdapter(_rows, _objects);
    private FaultInjectingStorageAdapter? _storage;
    private FaultInjectingStorageAdapter Storage => _storage ??= new FaultInjectingStorageAdapter(StoreOfRecord);

    /// <inheritdoc />
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder.ConfigureServices(services =>
        {
            // Registered BEFORE the bootstrap's TryAddSingleton, so the production decorators wrap it.
            services.AddSingleton<IStorageAdapter>(Storage);
            return services;
        }));

    private CancellationToken Ct => TestContext.Current.CancellationToken;

    private MeshNode Claim(string id, string name) =>
        new(id, TestPartition) { Name = name, NodeType = "Markdown" };

    private async Task<MeshNode?> Stored(string path) =>
        await StoreOfRecord.Read(path, Mesh.JsonSerializerOptions).Should().Within(TestTimeouts.Convergence)
            .Emit(cancellationToken: Ct);

    /// <summary>
    /// Issues a create of <paramref name="node"/>, waits until it has passed its existence read and is
    /// paused at the store, runs <paramref name="whilePaused"/>, releases it and returns its answer.
    /// </summary>
    private async Task<CreateNodeResponse> PausedCreate(MeshNode node, System.Func<Task> whilePaused)
    {
        var hold = Storage.HoldWrites(node.Path);
        var create = ObserveNodeOperation(new CreateNodeRequest(node)).Replay(1);
        using var connection = create.Connect();
        try
        {
            await hold.Arrivals.Should().Within(TestTimeouts.Convergence)
                .Emit("the create read the path as absent and reached the store", cancellationToken: Ct);
            await whilePaused();
        }
        finally
        {
            hold.Release();
        }
        var response = (await create.Should().Within(TestTimeouts.Convergence)
            .Emit("a create always answers", Ct)).Message;
        Output.WriteLine($"[create] success={response.Success} reason={response.RejectionReason} error={response.Error}");
        return response;
    }

    /// <summary>
    /// THE FALSIFIER: another process's create of the same path commits while this create is past its
    /// existence read. This create is refused as <see cref="NodeCreationRejectionReason.NodeAlreadyExists"/>
    /// and the winner's row survives untouched.
    /// </summary>
    [Fact]
    public async Task ACreateThatLostTheRace_IsRefusedAsAlreadyExisting_AndLeavesTheWinner()
    {
        var mine = Claim("create-race-lost", "this process");

        var response = await PausedCreate(mine, async () =>
        {
            var inserted = await OtherProcess
                .WriteIfVersion(mine with { Name = "the other replica", Version = 1 }, 0, Mesh.JsonSerializerOptions)
                .Should().Within(TestTimeouts.Convergence).Emit("the other replica's create commits first", cancellationToken: Ct);
            inserted.Should().BeTrue("the path was free when the other replica inserted it");
        });

        response.Success.Should().BeFalse(
            "another create committed the path after this one's existence read; telling both that they "
            + "created the node is how one incident opened two issues (#7347)");
        response.RejectionReason.Should().Be(NodeCreationRejectionReason.NodeAlreadyExists,
            "a lost race is the same fact as a row the existence read could see");
        var stored = await Stored(mine.Path);
        stored.Should().NotBeNull();
        stored!.Name.Should().Be("the other replica", "the refused create must not overwrite the winner");
    }

    /// <summary>
    /// NEGATIVE CONTROL: the identical paused create with no foreign create succeeds and stores its
    /// node — so the refusal above is the store's insert-if-absent, not a refusal of every paused create.
    /// </summary>
    [Fact]
    public async Task ThePausedCreate_WithNoOtherCreate_Creates()
    {
        var mine = Claim("create-race-free", "this process");

        var response = await PausedCreate(mine, () => Task.CompletedTask);

        response.Success.Should().BeTrue($"nothing else created the path; error={response.Error}");
        var stored = await Stored(mine.Path);
        stored.Should().NotBeNull();
        stored!.Name.Should().Be("this process");
    }
}
