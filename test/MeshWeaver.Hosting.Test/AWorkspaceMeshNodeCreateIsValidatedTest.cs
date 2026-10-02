using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Reactive.Linq;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Fixture;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Hosting.Test;

/// <summary>
/// 🚨 A <see cref="MeshNode"/> added through a <see cref="DataChangeRequest"/> is a CREATE, so it
/// answers to the create-validator chain — not only to the RLS permission check. The counterpart of
/// <see cref="AWorkspaceMeshNodeDeleteIsValidatedTest"/>.
///
/// <para><c>MeshNodeTypeSource</c> answers a MeshNode entering the workspace with a raw storage
/// write. The change path ran <c>RlsDataValidator</c> (Create permission) and no
/// <see cref="INodeValidator"/>, so a node every create guard refused could be created by posting
/// the change instead of the create. The caller picks the list, and the type source writes an
/// unknown entry of <c>Updates</c> the same way, so both lists are covered. Each refusal is paired
/// with its control in the same mesh: the validator refuses the direct create too (it is armed),
/// and an unrefused sibling added the same way is written (so the change path really creates).</para>
/// </summary>
public class AWorkspaceMeshNodeCreateIsValidatedTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string RefusalReason = "this node may not be created";

    private readonly RefusePathsCreationValidator refusing = new(RefusalReason);

    private IStorageAdapter Storage => Mesh.ServiceProvider.GetRequiredService<IStorageAdapter>();

    /// <inheritdoc />
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder).ConfigureServices(services => services
            .AddSingleton<INodeValidator>(refusing));

    [Theory(Timeout = 60000)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AddingARefusedNodeThroughADataChange_IsRefused_AndAnUnrefusedOneIsWritten(bool asUpdate)
    {
        var ct = TestContext.Current.CancellationToken;
        var host = await CreateNode(NewPath("host"));
        var held = Node($"{host}/held");
        var free = Node($"{host}/free");
        refusing.Refuse(held.Path!);

        // Armed: the direct create is refused with the validator's reason.
        var createFault = await NodeFactory.CreateNode(held)
            .Select(_ => (Exception?)null)
            .Catch((Exception ex) => Observable.Return<Exception?>(ex))
            .DefaultIfEmpty(null)
            .Should().Within(TestTimeouts.Convergence).Emit(cancellationToken: ct);
        createFault.Should().NotBeNull("the validator refuses creating the held node");

        var refused = await Change(host, asUpdate
            ? new DataChangeRequest { Updates = [held] }
            : new DataChangeRequest { Creations = [held] });
        refused.Status.Should().Be(DataChangeStatus.Failed,
            "adding the node to the workspace creates it, and that create is refused");

        // The control: the same change for a node no validator objects to is written to storage.
        var added = await Change(host, asUpdate
            ? new DataChangeRequest { Updates = [free] }
            : new DataChangeRequest { Creations = [free] });
        added.Status.Should().Be(DataChangeStatus.Committed, "nothing objects to creating this node");
        await Stored(free.Path!).Should().Within(TestTimeouts.Convergence)
            .Emit("the change path writes the node to storage", cancellationToken: ct);

        await Stored(held.Path!).Should().NotEmit(TestTimeouts.Quick,
            "the refused change must not write the held node to storage");
    }

    private static MeshNode Node(string path) => MeshNode.FromPath(path) with
    {
        Name = path,
        NodeType = "Markdown",
        State = MeshNodeState.Active,
    };

    private Task<DataChangeResponse> Change(string host, DataChangeRequest request) =>
        GetClient()
            .Observe(request with { ChangedBy = TestUsers.Admin.ObjectId },
                o => o.WithTarget(new Address(host)).WithAccessContext(TestUsers.Admin))
            .Select(d => d.Message)
            .OfType<DataChangeResponse>()
            .Do(r => Output.WriteLine($"DataChange on {host}: {r.Status}"))
            .Should().Within(TestTimeouts.Convergence).Emit(cancellationToken: TestContext.Current.CancellationToken);

    private IObservable<bool> Stored(string path) =>
        Observable.Interval(TimeSpan.FromMilliseconds(50)).StartWith(0L)
            .SelectMany(_ => Storage.Read(path, Mesh.JsonSerializerOptions).DefaultIfEmpty(null).Take(1))
            .Where(node => node is not null)
            .Select(_ => true)
            .Take(1);

    private async Task<string> CreateNode(string path)
    {
        await NodeFactory.CreateNode(Node(path))
            .Should().Within(TestTimeouts.Convergence).Emit(cancellationToken: TestContext.Current.CancellationToken);
        return path;
    }

    private static string NewPath(string prefix) =>
        $"{TestPartition}/{prefix}-{Guid.NewGuid().ToString("N")[..8]}";
}

/// <summary>
/// Refuses the create of the paths it is told to, accepts everything else. Instance state owned by
/// the test's mesh, never static.
/// </summary>
internal sealed class RefusePathsCreationValidator(string reason) : INodeValidator
{
    private ImmutableHashSet<string> refused = ImmutableHashSet.Create<string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Refuse creating <paramref name="path"/> from now on.</summary>
    public void Refuse(string path) => ImmutableInterlocked.Update(ref refused, s => s.Add(path));

    /// <inheritdoc />
    public IReadOnlyCollection<NodeOperation> SupportedOperations { get; } = [NodeOperation.Create];

    /// <inheritdoc />
    public IObservable<NodeValidationResult> Validate(NodeValidationContext context)
        => Observable.Return(refused.Contains(context.Node.Path)
            ? NodeValidationResult.Invalid(reason)
            : NodeValidationResult.Valid());
}
