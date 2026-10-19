using System;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Hosting.Test;

/// <summary>
/// 🚨 A failed copy never names as WRITTEN a target it CONFIRMED (#6951, review of #6976).
///
/// <para><c>HandleCreateNodeRequest</c> answers success for a create that meets a TRANSIENT node at
/// its path by confirming it (Transient → Active) — the row predates the request. The copy recorded
/// every success as its own write, so a caller undoing the failed copy (the case-only rename of a
/// move) would have deleted a node that existed before it. A create now says whether it confirmed
/// (<see cref="CreateNodeResponse.Confirmed"/>), and the copy reports such a target under
/// <see cref="CopyNodeResponse.Unproven"/> — never removable.</para>
///
/// <para>Isolated fault: one create validator refuses exactly ONE descendant path, so the copy
/// confirms the transient root and then fails. The control is the same copy onto a FRESH target,
/// whose root it inserted and therefore names as written.</para>
/// </summary>
public class AFailedCopyDoesNotClaimAConfirmedTransientTargetTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private readonly RefuseExactPathCreationValidator refusing = new();

    /// <inheritdoc />
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder).ConfigureServices(services => services
            .AddSingleton<INodeValidator>(refusing));

    [Fact(Timeout = 90_000)]
    public async Task ATransientTargetTheCopyConfirmed_IsUnproven_NotWritten()
    {
        var ct = TestContext.Current.CancellationToken;
        var id = Guid.NewGuid().ToString("N")[..8];
        var source = $"{TestPartition}/group-{id}";
        var fresh = $"{TestPartition}/fresh-{id}";
        var transient = $"{TestPartition}/reserved-{id}";
        await Create(source, "Group", MeshNodeState.Active, ct);
        await Create($"{source}/member", "Member", MeshNodeState.Active, ct);
        // A create always stores Active; a TRANSIENT row is one written straight to the store (a
        // reservation the create later confirms).
        await Mesh.ServiceProvider.GetRequiredService<IStorageAdapter>()
            .Write(MeshNode.FromPath(transient) with
                {
                    Name = "Reserved",
                    NodeType = "Markdown",
                    State = MeshNodeState.Transient,
                }, Mesh.JsonSerializerOptions)
            .Take(1)
            .Should().Within(TestTimeouts.Convergence).Emit(cancellationToken: ct);
        refusing.Refuse($"{fresh}/member");
        refusing.Refuse($"{transient}/member");

        // The control: onto a fresh target the root is inserted, so it is the copy's own write.
        var control = await Copy(source, fresh, ct);
        control.Success.Should().BeFalse("the descendant create was refused");
        control.Written.Should().Equal([fresh], "the copy inserted the fresh root");

        var copied = await Copy(source, transient, ct);
        copied.Success.Should().BeFalse("the descendant create was refused");
        copied.Written.Should().NotContain(transient,
            "the root existed as Transient before the copy and was only confirmed — undoing the copy must never delete it");
        copied.Unproven.Should().Equal([transient], "a confirmed target is reported as unproven");
    }

    private Task<CopyNodeResponse> Copy(string source, string target, CancellationToken ct) =>
        ObserveNodeOperation(new CopyNodeRequest(source, target)
            {
                IncludeDescendants = true,
                IncludeSatellites = true,
                PreserveAuthorship = true,
                RequireComplete = true,
            })
            .Select(d => d.Message)
            .Do(r => Output.WriteLine(
                $"COPY {source} -> {target}: success={r.Success} error={r.Error} written=[{string.Join(", ", r.Written)}] "
                + $"unproven=[{string.Join(", ", r.Unproven)}] resumed=[{string.Join(", ", r.Resumed)}]"))
            .Should().Within(TestTimeouts.Convergence).Emit(cancellationToken: ct);

    private Task<MeshNode> Create(string path, string name, MeshNodeState state, CancellationToken ct) =>
        NodeFactory.CreateNode(MeshNode.FromPath(path) with
            {
                Name = name,
                NodeType = "Markdown",
                State = state,
            })
            .Should().Within(TestTimeouts.Convergence).Emit(cancellationToken: ct);
}
