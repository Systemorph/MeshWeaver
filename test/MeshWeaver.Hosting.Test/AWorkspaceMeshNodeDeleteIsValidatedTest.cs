using System;
using System.Reactive.Linq;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Fixture;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Hosting.Test;

/// <summary>
/// 🚨 A <see cref="MeshNode"/> removed through a <see cref="DataChangeRequest"/> is a DELETE, so it
/// answers to the delete-validator chain — not only to the RLS permission check.
///
/// <para><c>MeshNodeTypeSource</c> answers a MeshNode leaving the workspace with a raw
/// <c>DeleteAndPublish</c> on storage. The change path ran <c>RlsDataValidator</c> (Delete
/// permission) and no <see cref="INodeValidator"/>, so a node every delete guard refused could be
/// removed by posting the change instead of the delete. The refusal is paired with its control in
/// the same mesh: the validator refuses the direct delete too (it is armed), and an unrefused
/// sibling removed the same way does go (so the change path really deletes).</para>
/// </summary>
public class AWorkspaceMeshNodeDeleteIsValidatedTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string RefusalReason = "this node is held in place";

    private readonly RefusePathsDeletionValidator refusing = new(RefusalReason);

    private IStorageAdapter Storage => Mesh.ServiceProvider.GetRequiredService<IStorageAdapter>();

    /// <inheritdoc />
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder).ConfigureServices(services => services
            .AddSingleton<INodeValidator>(refusing));

    [Fact(Timeout = 60000)]
    public async Task RemovingARefusedNodeThroughADataChange_IsRefused_AndAnUnrefusedOneIsRemoved()
    {
        var ct = TestContext.Current.CancellationToken;
        // The removed nodes share ONE hub's MeshNode collection with that hub's own node: removing
        // a hub's ONLY node empties the collection, which MeshNodeTypeSource treats as a no-op
        // (the activation race guard), so the delete path is reached only while another node stays.
        var host = await CreateNode(NewPath("host"));
        var held = await AddThroughDataChange(host, "held");
        var free = await AddThroughDataChange(host, "free");
        refusing.Refuse(held.Path!);

        // Armed: the direct delete is refused with the validator's reason.
        var deleteFault = await NodeFactory.DeleteNode(held.Path!)
            .Select(_ => (Exception?)null)
            .Catch((Exception ex) => Observable.Return<Exception?>(ex))
            .DefaultIfEmpty(null)
            .Should().Within(TestTimeouts.Convergence).Emit(cancellationToken: ct);
        deleteFault.Should().NotBeNull("the validator refuses deleting the held node");

        var refused = await Change(host, new DataChangeRequest { Deletions = [held] });
        refused.Status.Should().Be(DataChangeStatus.Failed,
            "removing the node from the workspace deletes it, and that delete is refused");
        await Gone(held.Path!).Should().NotEmit(TestTimeouts.Quick,
            "the refused change must not delete the held node from storage");

        // The control: the same change for a node no validator objects to removes it from storage.
        var removed = await Change(host, new DataChangeRequest { Deletions = [free] });
        removed.Status.Should().Be(DataChangeStatus.Committed, "nothing objects to deleting this node");
        await Gone(free.Path!).Should().Within(TestTimeouts.Convergence)
            .Emit("the change path deletes the node from storage", cancellationToken: ct);
    }

    private async Task<MeshNode> AddThroughDataChange(string host, string id)
    {
        var node = MeshNode.FromPath($"{host}/{id}") with
        {
            Name = id,
            NodeType = "Markdown",
            State = MeshNodeState.Active,
        };
        var added = await Change(host, new DataChangeRequest { Creations = [node] });
        added.Status.Should().Be(DataChangeStatus.Committed, "the host hub takes the node into its collection");
        // The create flush is debounced — wait for the row, re-querying storage.
        return await Observable.Interval(TimeSpan.FromMilliseconds(50)).StartWith(0L)
            .SelectMany(_ => Storage.Read(node.Path!, Mesh.JsonSerializerOptions).DefaultIfEmpty(null).Take(1))
            .Where(stored => stored is not null)
            .Select(stored => stored!)
            .Take(1)
            .Should().Within(TestTimeouts.Convergence)
            .Emit("the node added through the change is persisted", cancellationToken: TestContext.Current.CancellationToken);
    }

    private Task<DataChangeResponse> Change(string host, DataChangeRequest request) =>
        GetClient()
            .Observe(request with { ChangedBy = TestUsers.Admin.ObjectId },
                o => o.WithTarget(new Address(host)).WithAccessContext(TestUsers.Admin))
            .Select(d => d.Message)
            .OfType<DataChangeResponse>()
            .Do(r => Output.WriteLine($"DataChange on {host}: {r.Status}"))
            .Should().Within(TestTimeouts.Convergence).Emit(cancellationToken: TestContext.Current.CancellationToken);

    private IObservable<bool> Gone(string path) =>
        Observable.Interval(TimeSpan.FromMilliseconds(50)).StartWith(0L)
            .SelectMany(_ => Storage.Read(path, Mesh.JsonSerializerOptions).DefaultIfEmpty(null).Take(1))
            .Where(node => node is null)
            .Select(_ => true)
            .Take(1);

    private Task<MeshNode?> StoredAt(string path) =>
        Storage.Read(path, Mesh.JsonSerializerOptions)
            .DefaultIfEmpty(null)
            .Should().Within(TestTimeouts.Convergence).Emit(cancellationToken: TestContext.Current.CancellationToken);

    private async Task<string> CreateNode(string path)
    {
        await NodeFactory.CreateNode(MeshNode.FromPath(path) with
        {
            Name = path,
            NodeType = "Markdown",
            State = MeshNodeState.Active,
        }).Should().Within(TestTimeouts.Convergence).Emit(cancellationToken: TestContext.Current.CancellationToken);
        return path;
    }

    private static string NewPath(string prefix) =>
        $"{TestPartition}/{prefix}-{Guid.NewGuid().ToString("N")[..8]}";
}
