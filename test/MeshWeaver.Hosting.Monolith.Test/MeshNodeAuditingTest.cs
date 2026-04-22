using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using MeshWeaver.Data;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Xunit;

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
    [Fact(Timeout = 20000)]
    public async Task CreateNodeRequest_StampsCreatedAndLastModifiedFromIdentity()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = GetClient();
        await client.AwaitResponse(new PingRequest(), o => o.WithTarget(Mesh.Address), ct);

        var request = new CreateNodeRequest(
            new MeshNode("audit-create", TestPartition) { Name = "Audit Create", NodeType = "Markdown" })
        {
            CreatedBy = "alice@example.com"
        };
        var response = await client.AwaitResponse(request, o => o.WithTarget(Mesh.Address), ct);

        response.Message.Success.Should().BeTrue();
        var saved = response.Message.Node!;
        saved.CreatedDate.Should().NotBe(default);
        saved.LastModified.Should().NotBe(default);
        saved.CreatedBy.Should().Be("alice@example.com");
        saved.LastModifiedBy.Should().Be("alice@example.com");
    }

    [Fact(Timeout = 20000)]
    public async Task UpdateNodeRequest_PreservesCreatedAndRefreshesLastModified()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = GetClient();
        await client.AwaitResponse(new PingRequest(), o => o.WithTarget(Mesh.Address), ct);

        // Create
        var create = new CreateNodeRequest(
            new MeshNode("audit-update", TestPartition) { Name = "Audit Update", NodeType = "Markdown" })
        {
            CreatedBy = "alice@example.com"
        };
        var createResp = await client.AwaitResponse(create, o => o.WithTarget(Mesh.Address), ct);
        createResp.Message.Success.Should().BeTrue();
        var created = createResp.Message.Node!;
        var originalCreatedDate = created.CreatedDate;

        // Small wait so LastModified is visibly different
        await Task.Delay(10, ct);

        // Update via UpdateNodeRequest with a new user
        var update = new UpdateNodeRequest(created with { Name = "Renamed" })
        {
            UpdatedBy = "bob@example.com"
        };
        var updateResp = await client.AwaitResponse(update, o => o.WithTarget(Mesh.Address), ct);
        updateResp.Message.Success.Should().BeTrue();
        var updated = updateResp.Message.Node!;

        updated.CreatedDate.Should().Be(originalCreatedDate,
            "CreatedDate is immutable — only LastModified should move");
        updated.CreatedBy.Should().Be("alice@example.com",
            "CreatedBy is immutable — reflects the original author");
        updated.LastModifiedBy.Should().Be("bob@example.com",
            "UpdatedBy in the request stamps LastModifiedBy on the node");
        updated.LastModified.Should().BeAfter(originalCreatedDate,
            "LastModified is refreshed on every update");
    }

    [Fact(Timeout = 20000)]
    public async Task CreateNode_PreservesExplicitCreatedDate()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = GetClient();
        await client.AwaitResponse(new PingRequest(), o => o.WithTarget(Mesh.Address), ct);

        // Import flows (e.g. file-system seed) set CreatedDate explicitly; the handler
        // must not overwrite it with "now".
        var historicCreated = new System.DateTimeOffset(2020, 1, 15, 10, 0, 0, System.TimeSpan.Zero);
        var request = new CreateNodeRequest(new MeshNode("audit-import", TestPartition)
        {
            Name = "Imported",
            NodeType = "Markdown",
            CreatedDate = historicCreated
        });
        var response = await client.AwaitResponse(request, o => o.WithTarget(Mesh.Address), ct);

        response.Message.Success.Should().BeTrue();
        response.Message.Node!.CreatedDate.Should().Be(historicCreated);
    }
}
