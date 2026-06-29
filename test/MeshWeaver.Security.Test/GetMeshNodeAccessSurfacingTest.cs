using System;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Hosting.Security;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Security.Test;

/// <summary>
/// Negative tests: a hub / caller WITHOUT access reads a node and we assert the
/// access denial SURFACES as a clear error — never a silent <c>null</c> the caller
/// can't tell apart from "node not found".
///
/// <para>Why this matters: a per-node hub's read-validator pipeline correctly DENIES
/// the read and posts <c>GetDataResponse{ Error = "..." }</c> (see
/// <c>MeshDataSource.AddReadValidatorPipeline</c>). But a caller using the
/// <c>hub.GetMeshNode(path)</c> convenience read only inspects <c>resp.Data</c> and
/// returns <c>null</c> on denial, dropping <c>resp.Error</c> on the floor. Downstream
/// (e.g. <c>FutuReDataLoader</c> reading the parent BusinessUnit node) then treats the
/// denial as "missing" and silently falls back — the access failure never reaches a
/// log, the GUI error area, or the activity surface. These tests pin that behaviour.</para>
/// </summary>
public class GetMeshNodeAccessSurfacingTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => ConfigureMeshBase(builder)
            .AddRowLevelSecurity()
            .AddMeshNodes(
                new MeshNode("Secret") { Name = "Secret Hub" },
                new MeshNode("Secret/Item") { Name = "Secret Item" })
            .ConfigureDefaultNodeHub(c => c.AddData(d => d));

    private async Task EnsureHubStarted(Address address)
    {
        var client = GetClient();
        await client.Observe(new PingRequest(), o => o.WithTarget(address)).Should().Emit();
    }

    /// <summary>
    /// Baseline — the framework DOES surface access denial at the message level: a raw
    /// <see cref="GetDataRequest"/>(<see cref="MeshNodeReference"/>) from a caller without
    /// access fails the delivery with a <c>DeliveryFailureException</c> whose message names
    /// the user, the missing permission, and the node. This is the error a swallowing
    /// convenience-read throws away.
    /// </summary>
    [Fact(Timeout = 20000)]
    public async Task RawGetDataRequest_WithoutAccess_SurfacesDeliveryFailure()
    {
        await EnsureHubStarted(new Address("Secret/Item"));
        TestUsers.DevLogin(Mesh, new AccessContext { ObjectId = "NobodyUser", Name = "Nobody" });

        var client = GetClient();
        Func<Task> act = () => client
            .Observe<GetDataResponse>(
                new GetDataRequest(new MeshNodeReference()),
                o => o.WithTarget(new Address("Secret/Item")))
            .FirstAsync()
            .Timeout(10.Seconds())
            .ToTask();

        var ex = (await act.Should().ThrowAsync<Exception>()).Which;
        Output.WriteLine($"Surfaced: {ex.GetType().Name}: {ex.Message}");

        ex.Should().NotBeOfType<TimeoutException>(
            "an access-denied read must surface as an error, not hang to a timeout");
        ex.Message.Should().Contain("Access denied",
            "the surfaced delivery failure must say the read was access-denied");
        ex.Message.Should().Contain("Read",
            "the surfaced error must name the missing permission so the user can act");
    }

    /// <summary>
    /// The <c>GetMeshNode</c> convenience read must SURFACE the access denial — either by
    /// erroring or by otherwise making the denial observable — and must NOT silently
    /// return <c>null</c> (indistinguishable from "not found") while discarding
    /// <see cref="GetDataResponse.Error"/>. This is the swallow that lets a data loader
    /// fall back silently instead of reporting "you don't have access".
    /// </summary>
    [Fact(Timeout = 20000)]
    public async Task GetMeshNode_WithoutAccess_ShouldSurfaceError_NotSilentNull()
    {
        await EnsureHubStarted(new Address("Secret/Item"));
        TestUsers.DevLogin(Mesh, new AccessContext { ObjectId = "NobodyUser", Name = "Nobody" });

        Exception? surfaced = null;
        MeshNode? emitted = null;
        try
        {
            emitted = await Mesh.GetMeshNode("Secret/Item", 10.Seconds()).FirstAsync().ToTask();
        }
        catch (Exception ex)
        {
            surfaced = ex;
            Output.WriteLine($"Surfaced: {ex.GetType().Name}: {ex.Message}");
        }

        Output.WriteLine($"emitted={(emitted is null ? "null" : emitted.Path)} surfacedError={surfaced?.GetType().Name ?? "<none>"}");

        // The denial must be observable, not a silent null the caller mistakes for
        // "node not found".
        emitted.Should().BeNull("a denied read must not emit a (null) node value");
        surfaced.Should().NotBeNull(
            "an access-denied read must surface the denial, "
            + "not collapse to a silent null that callers mistake for 'node not found'");
        surfaced.Should().NotBeOfType<TimeoutException>(
            "denial must surface as an access error, not as a timeout/hang");
        surfaced!.Message.Should().Contain("Access denied",
            "the surfaced error must say the read was access-denied so it can reach a log/GUI");
    }
}
