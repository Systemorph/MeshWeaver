using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

using System.Reactive.Linq;
namespace MeshWeaver.Hosting.Monolith.Test;

/// <summary>
/// Verifies the MeshNode auditing fields (CreatedDate, CreatedBy, LastModified,
/// LastModifiedBy) are stamped by the Create/Update request handlers. These back
/// the Overview meta row the user asked for: "don't see created timestamp ==&gt;
/// should always be there, the create process should hand out. also don't see last
/// modified and last modified by".
/// </summary>
public class MeshNodeAuditingTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    /// <summary>Share Mesh/SP across [Fact]s — see MonolithMeshTestBase.ShareMeshAcrossTests.</summary>
    protected override bool ShareMeshAcrossTests => true;

    [Fact(Timeout = 20000)]
    public async Task CreateNodeRequest_StampsCreatedAndLastModifiedFromIdentity()
    {
        var client = GetClient();
        await client.Observe(new PingRequest(), o => o.WithTarget(Mesh.Address)).Should().Within(15.Seconds()).Emit();

        var request = new CreateNodeRequest(
            new MeshNode("audit-create", TestPartition) { Name = "Audit Create", NodeType = "Markdown" })
        {
            CreatedBy = "alice@example.com"
        };
        var response = await client.Observe(request, o => o.WithTarget(Mesh.Address)).Should().Within(15.Seconds()).Emit();

        response.Message.Success.Should().BeTrue();
        var saved = response.Message.Node!;
        saved.CreatedDate.Should().NotBe(default);
        saved.LastModified.Should().NotBe(default);
        saved.CreatedBy.Should().Be("alice@example.com");
        saved.LastModifiedBy.Should().Be("alice@example.com");
    }

    [Fact(Timeout = 20000)]
    public async Task StreamUpdate_PreservesCreatedAndStampsLastModifiedFromIdentity()
    {
        var client = GetClient();
        await client.Observe(new PingRequest(), o => o.WithTarget(Mesh.Address)).Should().Within(15.Seconds()).Emit();

        // Create
        var create = new CreateNodeRequest(
            new MeshNode("audit-update", TestPartition) { Name = "Audit Update", NodeType = "Markdown" })
        {
            CreatedBy = "alice@example.com"
        };
        var createResp = await client.Observe(create, o => o.WithTarget(Mesh.Address)).Should().Within(15.Seconds()).Emit();
        createResp.Message.Success.Should().BeTrue();
        var created = createResp.Message.Node!;
        var originalCreatedDate = created.CreatedDate;

        // Small wait so LastModified is visibly different (sanctioned distinct-timestamp delay)
        Thread.Sleep(10);

        // Update via the canonical stream.Update as bob. LastModifiedBy is stamped from the
        // caller's AUTHENTICATED identity (the AccessContext) — the same audit trail
        // UpdateNodeRequest stamped from UpdatedBy, now that UpdateNodeRequest is retired.
        var accessService = Mesh.ServiceProvider.GetRequiredService<AccessService>();
        var originalCtx = accessService.CircuitContext;
        accessService.SetCircuitContext(new AccessContext { ObjectId = "bob@example.com", Name = "bob@example.com" });
        MeshNode updated;
        try
        {
            updated = await Mesh.GetMeshNodeStream(created.Path)
                .Update(n => n with { Name = "Renamed" })
                .Should().Within(15.Seconds()).Emit();
        }
        finally
        {
            accessService.SetCircuitContext(originalCtx);
        }

        updated.CreatedDate.Should().Be(originalCreatedDate,
            "CreatedDate is immutable â€” only LastModified should move");
        updated.CreatedBy.Should().Be("alice@example.com",
            "CreatedBy is immutable â€” reflects the original author");
        updated.LastModifiedBy.Should().Be("bob@example.com",
            "UpdatedBy in the request stamps LastModifiedBy on the node");
        updated.LastModified.Should().BeAfter(originalCreatedDate,
            "LastModified is refreshed on every update");
    }

    [Fact(Timeout = 20000)]
    public async Task CreateNode_PreservesExplicitCreatedDate()
    {
        var client = GetClient();
        await client.Observe(new PingRequest(), o => o.WithTarget(Mesh.Address)).Should().Within(15.Seconds()).Emit();

        // Import flows (e.g. file-system seed) set CreatedDate explicitly; the handler
        // must not overwrite it with "now".
        var historicCreated = new System.DateTimeOffset(2020, 1, 15, 10, 0, 0, System.TimeSpan.Zero);
        var request = new CreateNodeRequest(new MeshNode("audit-import", TestPartition)
        {
            Name = "Imported",
            NodeType = "Markdown",
            CreatedDate = historicCreated
        });
        var response = await client.Observe(request, o => o.WithTarget(Mesh.Address)).Should().Within(15.Seconds()).Emit();

        response.Message.Success.Should().BeTrue();
        response.Message.Node!.CreatedDate.Should().Be(historicCreated);
    }
}
