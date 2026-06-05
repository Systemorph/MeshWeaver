using System;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Markdown;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using Xunit;

namespace MeshWeaver.Hosting.Monolith.Test;

/// <summary>
/// Focused tests for the <see cref="CreateOrUpdateNodeRequest"/> mesh-hub
/// handler. Two strict paths cover the contract: missing target → forwards
/// as <see cref="CreateNodeRequest"/>, existing target → applies via
/// <c>workspace.GetMeshNodeStream(path).Update(state =&gt;
/// UpdateAccordingToSourceNode(state, sourceNode))</c>. Direct
/// <c>persistence.Write</c> is explicitly disallowed by the
/// "per-node hub is the sole owner of its state" rule that this handler
/// enforces.
/// </summary>
public class CreateOrUpdateNodeRequestTest(ITestOutputHelper output)
    : MonolithMeshTestBase(output)
{
    /// <summary>
    /// Missing target case: the upsert falls through to <see cref="CreateNodeRequest"/>
    /// internally; the response carries <see cref="CreateOrUpdateNodeResponse.WasCreated"/> = true.
    /// </summary>
    [Fact(Timeout = 30_000)]
    public void Upsert_OnMissingTarget_CreatesAndReports_WasCreated_True()
    {
        var path = $"{TestPartition}/upsert-create-{Guid.NewGuid():N}";
        var sourceNode = new MeshNode(path.Split('/').Last(), TestPartition)
        {
            Name = "Brand-new node",
            NodeType = "Markdown",
            Content = new MarkdownContent { Content = "# fresh content" },
            State = MeshNodeState.Active,
        };

        var resp = Mesh
            .Observe<CreateOrUpdateNodeResponse>(new CreateOrUpdateNodeRequest(sourceNode))
            .Select(d => d.Message)
            .Should().Emit();

        resp.Success.Should().BeTrue(resp.Error ?? "");
        resp.WasCreated.Should().BeTrue(
            "the target was missing — the handler must forward as CreateNodeRequest internally");
        resp.Node.Should().NotBeNull();
        resp.Node!.Path.Should().Be(path);
        resp.Node.Name.Should().Be("Brand-new node");
        resp.Log.Should().NotBeNull("every upsert rides on a single ActivityLog");

        // Verify the node lives in the mesh — single-node read via per-node hub.
        var live = Mesh.GetMeshNode(path, 10.Seconds()).Should().Emit();
        live.Should().NotBeNull();
        live!.Name.Should().Be("Brand-new node");
        live.Content.Should().BeOfType<MarkdownContent>()
            .Which.Content.Should().Be("# fresh content");
    }

    /// <summary>
    /// Existing target case: the upsert applies through
    /// <c>workspace.GetMeshNodeStream(path).Update(...)</c>; the response
    /// carries <see cref="CreateOrUpdateNodeResponse.WasCreated"/> = false and
    /// the post-update node has the source's writable fields merged in.
    /// </summary>
    [Fact(Timeout = 30_000)]
    public void Upsert_OnExistingTarget_UpdatesViaStream_WasCreated_False()
    {
        var path = $"{TestPartition}/upsert-update-{Guid.NewGuid():N}";

        // Seed an existing node — first via NodeFactory so the per-node hub
        // is alive and owns the state we'll update through GetMeshNodeStream.
        NodeFactory.CreateNode(new MeshNode(path.Split('/').Last(), TestPartition)
        {
            Name = "Original",
            NodeType = "Markdown",
            Content = new MarkdownContent { Content = "# v1" },
            State = MeshNodeState.Active,
        }).Should().Emit();

        // Send the upsert with the same path but new writable fields. The
        // handler must take the existence path and apply via the stream.
        var sourceNode = new MeshNode(path.Split('/').Last(), TestPartition)
        {
            Name = "Overwritten",
            NodeType = "Markdown",
            Content = new MarkdownContent { Content = "# v2" },
            State = MeshNodeState.Active,
        };

        var resp = Mesh
            .Observe<CreateOrUpdateNodeResponse>(new CreateOrUpdateNodeRequest(sourceNode))
            .Select(d => d.Message)
            .Should().Emit();

        resp.Success.Should().BeTrue(resp.Error ?? "");
        resp.WasCreated.Should().BeFalse(
            "the target existed — the handler must apply via stream.Update, not CreateNodeRequest");
        resp.Node.Should().NotBeNull();
        resp.Node!.Name.Should().Be("Overwritten");
        resp.Node.Content.Should().BeOfType<MarkdownContent>()
            .Which.Content.Should().Be("# v2");

        // Verify the live read agrees — wait for the stream to converge on
        // the new state. MeshNodeTypeSource debounces persistence saves over
        // 200ms; an immediate point-in-time read can race that. Subscribe to
        // the per-node hub's MeshNode stream and wait for the new Name.
        var workspace = GetClient(c => c.AddData()).GetWorkspace();
        var converged = workspace.GetMeshNodeStream(path)
            .Should().Within(10.Seconds())
            .Match(n => n?.Name == "Overwritten");
        converged.Name.Should().Be("Overwritten");
        converged.Content.Should().BeOfType<MarkdownContent>()
            .Which.Content.Should().Be("# v2");
    }

    /// <summary>
    /// Existence preservation: identity fields (Id, Path, CreatedDate,
    /// CreatedBy) on the existing node MUST NOT be overwritten by the
    /// source's defaults — only the writable surface (Name, NodeType, Icon,
    /// Category, Content, State, PreRenderedHtml) flows through.
    /// </summary>
    [Fact(Timeout = 30_000)]
    public void Upsert_OnExistingTarget_PreservesIdentityFields()
    {
        var path = $"{TestPartition}/upsert-identity-{Guid.NewGuid():N}";

        NodeFactory.CreateNode(new MeshNode(path.Split('/').Last(), TestPartition)
        {
            Name = "Original",
            NodeType = "Markdown",
            Content = new MarkdownContent { Content = "# original" },
            State = MeshNodeState.Active,
        }).Should().Emit();
        var before = Mesh.GetMeshNode(path, 10.Seconds()).Should().Emit();
        before.Should().NotBeNull();
        var originalCreatedDate = before!.CreatedDate;
        var originalCreatedBy = before.CreatedBy;

        var resp = Mesh
            .Observe<CreateOrUpdateNodeResponse>(new CreateOrUpdateNodeRequest(
                new MeshNode(path.Split('/').Last(), TestPartition)
                {
                    Name = "Renamed",
                    NodeType = "Markdown",
                    Content = new MarkdownContent { Content = "# updated" },
                    State = MeshNodeState.Active,
                }))
            .Select(d => d.Message)
            .Should().Emit();
        resp.Success.Should().BeTrue(resp.Error ?? "");

        // Wait for the stream to converge on the renamed state.
        var workspace = GetClient(c => c.AddData()).GetWorkspace();
        var after = workspace.GetMeshNodeStream(path)
            .Should().Within(10.Seconds())
            .Match(n => n?.Name == "Renamed");
        after.CreatedDate.Should().Be(originalCreatedDate,
            "CreatedDate is identity — UpdateAccordingToSourceNode preserves it");
        after.CreatedBy.Should().Be(originalCreatedBy,
            "CreatedBy is identity — UpdateAccordingToSourceNode preserves it");
        after.Path.Should().Be(path, "Path is identity");
        after.Name.Should().Be("Renamed", "Name is writable — should overwrite");
    }
}
