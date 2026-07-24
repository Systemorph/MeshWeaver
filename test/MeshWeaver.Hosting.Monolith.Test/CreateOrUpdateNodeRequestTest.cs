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
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
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
    public async Task Upsert_OnMissingTarget_CreatesAndReports_WasCreated_True()
    {
        var path = $"{TestPartition}/upsert-create-{Guid.NewGuid():N}";
        var sourceNode = new MeshNode(path.Split('/').Last(), TestPartition)
        {
            Name = "Brand-new node",
            NodeType = "Markdown",
            Content = new MarkdownContent { Content = "# fresh content" },
            State = MeshNodeState.Active,
        };

        var resp = await Mesh
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
        var live = await Mesh.GetMeshNode(path, 10.Seconds()).Should().Emit();
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
    public async Task Upsert_OnExistingTarget_UpdatesViaStream_WasCreated_False()
    {
        var path = $"{TestPartition}/upsert-update-{Guid.NewGuid():N}";

        // Seed an existing node — first via NodeFactory so the per-node hub
        // is alive and owns the state we'll update through GetMeshNodeStream.
        await NodeFactory.CreateNode(new MeshNode(path.Split('/').Last(), TestPartition)
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

        var resp = await Mesh
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
        var converged = await workspace.GetMeshNodeStream(path)
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
    public async Task Upsert_OnExistingTarget_PreservesIdentityFields()
    {
        var path = $"{TestPartition}/upsert-identity-{Guid.NewGuid():N}";

        await NodeFactory.CreateNode(new MeshNode(path.Split('/').Last(), TestPartition)
        {
            Name = "Original",
            NodeType = "Markdown",
            Content = new MarkdownContent { Content = "# original" },
            State = MeshNodeState.Active,
        }).Should().Emit();
        var before = await Mesh.GetMeshNode(path, 10.Seconds()).Should().Emit();
        before.Should().NotBeNull();
        var originalCreatedDate = before!.CreatedDate;
        var originalCreatedBy = before.CreatedBy;

        var resp = await Mesh
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
        var after = await workspace.GetMeshNodeStream(path)
            .Should().Within(10.Seconds())
            .Match(n => n?.Name == "Renamed");
        after.CreatedDate.Should().Be(originalCreatedDate,
            "CreatedDate is identity — UpdateAccordingToSourceNode preserves it");
        after.CreatedBy.Should().Be(originalCreatedBy,
            "CreatedBy is identity — UpdateAccordingToSourceNode preserves it");
        after.Path.Should().Be(path, "Path is identity");
        after.Name.Should().Be("Renamed", "Name is writable — should overwrite");
    }

    /// <summary>
    /// The no-op guard: an upsert IDENTICAL to the persisted state must be acknowledged without
    /// reaching the owner — no Version mint, no LastModified re-stamp, no history row, no stream
    /// re-broadcast (the deploy-flicker source when a full re-sync rewrites unchanged nodes).
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task Upsert_WithIdenticalState_IsSkipped_NoVersionOrTimestampChurn()
    {
        var path = $"{TestPartition}/upsert-noop-{Guid.NewGuid():N}";
        MeshNode Node() => new(path.Split('/').Last(), TestPartition)
        {
            Name = "Same",
            NodeType = "Markdown",
            Content = new MarkdownContent { Content = "# identical" },
            State = MeshNodeState.Active,
        };

        await NodeFactory.CreateNode(Node()).Should().Emit();
        var storage = Mesh.ServiceProvider.GetRequiredService<IStorageAdapter>();
        var before = await ReadStable(storage, path);

        var resp = await Mesh
            .Observe<CreateOrUpdateNodeResponse>(new CreateOrUpdateNodeRequest(Node()))
            .Select(d => d.Message)
            .Should().Emit();

        resp.Success.Should().BeTrue(resp.Error ?? "");
        resp.WasCreated.Should().BeFalse();
        resp.Node.Should().NotBeNull();
        resp.Log!.Messages.Any(m => m.Message.Contains("no-op", StringComparison.OrdinalIgnoreCase))
            .Should().BeTrue("the skip must be visible in the activity log, not a silent success");

        // The persisted stamps prove no write reached the owner: any write re-stamps LastModified
        // and mints a fresh Version unconditionally. ReadStable's quiet window (>1.2s, well past
        // the 200ms persist debounce) would catch a stray write.
        var after = await ReadStable(storage, path);
        after.Version.Should().Be(before.Version, "an identical upsert must not mint a Version");
        after.LastModified.Should().Be(before.LastModified, "an identical upsert must not re-stamp LastModified");
    }

    /// <summary>
    /// The guard must not over-skip: a single changed writable field (here Name; content identical)
    /// still takes the write path.
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task Upsert_WithOneChangedField_StillWrites()
    {
        var path = $"{TestPartition}/upsert-nearnoop-{Guid.NewGuid():N}";
        await NodeFactory.CreateNode(new MeshNode(path.Split('/').Last(), TestPartition)
        {
            Name = "Before",
            NodeType = "Markdown",
            Content = new MarkdownContent { Content = "# same content" },
            State = MeshNodeState.Active,
        }).Should().Emit();
        var storage = Mesh.ServiceProvider.GetRequiredService<IStorageAdapter>();
        var before = await ReadStable(storage, path);

        var resp = await Mesh
            .Observe<CreateOrUpdateNodeResponse>(new CreateOrUpdateNodeRequest(
                new MeshNode(path.Split('/').Last(), TestPartition)
                {
                    Name = "After",
                    NodeType = "Markdown",
                    Content = new MarkdownContent { Content = "# same content" },
                    State = MeshNodeState.Active,
                }))
            .Select(d => d.Message)
            .Should().Emit();
        resp.Success.Should().BeTrue(resp.Error ?? "");

        var after = await ReadStable(storage, path, n => n.Name == "After");
        after.Name.Should().Be("After");
        after.LastModified.Should().BeAfter(before.LastModified,
            "a real change takes the write path and re-stamps");
    }

    // Reads until the persisted node satisfies the predicate AND its Version is unchanged across
    // 4 consecutive samples (~1.2s quiet — past the 200ms persist debounce), so enrichment/debounce
    // trails can't masquerade as churn.
    private async Task<MeshNode> ReadStable(
        IStorageAdapter storage, string path, Func<MeshNode, bool>? predicate = null)
    {
        MeshNode? last = null;
        var stable = 0;
        for (var i = 0; i < 100 && stable < 4; i++)
        {
            var current = await storage.Read(path, Mesh.JsonSerializerOptions).FirstAsync().ToTask();
            stable = current is not null && last is not null
                     && current.Version == last.Version
                     && (predicate is null || predicate(current))
                ? stable + 1
                : 0;
            last = current ?? last;
            await Task.Delay(300);
        }
        last.Should().NotBeNull($"node {path} must be persisted");
        return last!;
    }
}
