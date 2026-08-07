using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
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
/// Tests for the BULK create verb: <see cref="CreateNodesRequest"/> /
/// <see cref="IMeshService.CreateNodes"/>. The contract under test: one round-trip creates N
/// plain nodes with the FULL validation surface run for every node BEFORE anything is written
/// (validate-all-then-write), caller order preserved onto the change feed post-commit, existing
/// paths skipped and reported (never overwritten), and satellites refused. This is the
/// sanctioned batched path for install/copy plans that used to fan out one
/// <see cref="CreateNodeRequest"/> round-trip per node.
/// </summary>
public class CreateNodesRequestTest(ITestOutputHelper output)
    : MonolithMeshTestBase(output)
{
    private static MeshNode Node(string path, string body) =>
        new(path.Split('/').Last(), string.Join('/', path.Split('/')[..^1]))
        {
            Name = path.Split('/').Last(),
            NodeType = "Markdown",
            Content = new MarkdownContent { Content = body },
            State = MeshNodeState.Active,
        };

    /// <summary>
    /// The happy path: N nodes land in one round-trip, in caller order — asserted on the change
    /// feed, whose per-node <c>Created</c> publishes are the contract that keeps parents ahead of
    /// children for every subscriber (stream caches, live queries).
    /// </summary>
    [Fact(Timeout = 30_000)]
    public async Task BulkCreate_CreatesAll_InCallerOrder_AndReadsBack()
    {
        var stem = $"{TestPartition}/bulk-{Guid.NewGuid():N}";
        var paths = Enumerable.Range(0, 5).Select(i => $"{stem}-{i}").ToArray();

        // Collect the change feed's Created events for OUR paths, subscribed before the post.
        // 🚨 The feed fans out on its OWN serial dispatch loop, never the publisher's thread
        // (issue #899), so the events are ORDERED but not yet delivered when the bulk-create
        // response returns. Signal on the last one and await it — the contract under test is
        // the ORDER of the publishes, never that they land on the caller's stack.
        var feed = Mesh.ServiceProvider.GetRequiredService<IMeshChangeFeed>();
        var seen = new List<string>();
        var allPublished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var sub = feed.Subscribe(change =>
        {
            if (!paths.Contains(change.Path))
                return;
            lock (seen)
            {
                seen.Add(change.Path);
                if (seen.Count >= paths.Length)
                    allPublished.TrySetResult();
            }
        }, MeshChangeKind.Created);

        var response = await NodeFactory
            .CreateNodes(paths.Select(p => Node(p, $"# body of {p}")).ToImmutableList())
            .Should().Emit();

        response.Success.Should().BeTrue(response.Error ?? "");
        response.Existing.Should().BeEmpty();
        response.Created.Should().HaveCount(paths.Length);
        response.Created.Select(n => n.Path).Should().ContainInOrder(paths,
            "caller order is contract — parents land before children");
        response.Created.Should().OnlyContain(n => n.State == MeshNodeState.Active && n.Version >= 1,
            "bulk-created nodes get the same stamps as singular creates");

        await allPublished.Task.WaitAsync(TimeSpan.FromSeconds(15));
        lock (seen)
            seen.Should().ContainInOrder(paths,
                "the change feed publishes Created per node post-commit, in caller order");

        // The nodes are REAL: a per-node read resolves content.
        var live = await Mesh.GetMeshNode(paths[^1], 10.Seconds()).Should().Emit();
        live.Should().NotBeNull();
        live!.Content.Should().BeOfType<MarkdownContent>()
            .Which.Content.Should().Be($"# body of {paths[^1]}");
    }

    /// <summary>
    /// Creates only: a path that already exists is skipped and reported — its content is never
    /// overwritten (updates flow through the owning per-node hub, not through a bulk create).
    /// </summary>
    [Fact(Timeout = 30_000)]
    public async Task BulkCreate_SkipsExisting_NeverOverwrites()
    {
        var stem = $"{TestPartition}/bulk-skip-{Guid.NewGuid():N}";
        var existingPath = $"{stem}-existing";
        var freshPath = $"{stem}-fresh";

        await NodeFactory.CreateNode(Node(existingPath, "# original")).Should().Emit();

        var response = await NodeFactory
            .CreateNodes(new[] { Node(existingPath, "# CLOBBER"), Node(freshPath, "# fresh") })
            .Should().Emit();

        response.Success.Should().BeTrue(response.Error ?? "");
        response.Existing.Should().ContainSingle().Which.Should().Be(existingPath);
        response.Created.Should().ContainSingle().Which.Path.Should().Be(freshPath);

        var untouched = await Mesh.GetMeshNode(existingPath, 10.Seconds()).Should().Emit();
        untouched!.Content.Should().BeOfType<MarkdownContent>()
            .Which.Content.Should().Be("# original", "an existing node must never be overwritten by a bulk create");
    }

    /// <summary>
    /// Satellites are per-node lifecycle: a <c>_</c>-segment path refuses the WHOLE batch before
    /// anything is written — the valid sibling in the same batch must not land.
    /// </summary>
    [Fact(Timeout = 30_000)]
    public async Task BulkCreate_RefusesSatellitePaths_NothingWritten()
    {
        var stem = $"{TestPartition}/bulk-sat-{Guid.NewGuid():N}";
        var satellitePath = $"{stem}/_Activity/act-1";
        var plainPath = $"{stem}-plain";

        var response = await Mesh
            .Observe<CreateNodesResponse>(new CreateNodesRequest(ImmutableList.Create(
                Node(plainPath, "# plain"),
                Node(satellitePath, "# satellite"))))
            .Select(d => d.Message)
            .Should().Emit();

        response.Success.Should().BeFalse("a satellite path must refuse the batch");
        response.RejectionReason.Should().Be(NodeCreationRejectionReason.InvalidPath);
        response.FailedPath.Should().Be(satellitePath);
        response.Created.Should().BeEmpty("validate-all-then-write: nothing may land on a refusal");

        var storage = Mesh.ServiceProvider.GetRequiredService<IStorageAdapter>();
        var plain = await storage.Read(plainPath, Mesh.JsonSerializerOptions).FirstAsync().ToTask();
        plain.Should().BeNull("the valid sibling of a refused batch must not have been written");
    }

    /// <summary>
    /// An unregistered NodeType anywhere in the batch fails the whole request pre-write — the
    /// valid sibling must not land (same recognition order as the singular create: static
    /// provider, then persistence).
    /// </summary>
    [Fact(Timeout = 30_000)]
    public async Task BulkCreate_UnregisteredType_FailsWholeBatch_NothingWritten()
    {
        var stem = $"{TestPartition}/bulk-type-{Guid.NewGuid():N}";
        var validPath = $"{stem}-valid";
        var bogus = Node($"{stem}-bogus", "# bogus") with { NodeType = $"NoSuchType{Guid.NewGuid():N}" };

        var response = await Mesh
            .Observe<CreateNodesResponse>(new CreateNodesRequest(ImmutableList.Create(
                Node(validPath, "# valid"), bogus)))
            .Select(d => d.Message)
            .Should().Emit();

        response.Success.Should().BeFalse();
        response.RejectionReason.Should().Be(NodeCreationRejectionReason.InvalidNodeType);
        response.FailedPath.Should().Be(bogus.Path);
        response.Created.Should().BeEmpty();

        var storage = Mesh.ServiceProvider.GetRequiredService<IStorageAdapter>();
        var valid = await storage.Read(validPath, Mesh.JsonSerializerOptions).FirstAsync().ToTask();
        valid.Should().BeNull("validate-all-then-write: the valid sibling must not have been written");
    }

    /// <summary>
    /// Duplicate paths inside one batch are a caller bug and refuse the batch — last-write-wins
    /// inside a single WriteMany would be silent data loss.
    /// </summary>
    [Fact(Timeout = 30_000)]
    public async Task BulkCreate_DuplicatePathInBatch_Refused()
    {
        var path = $"{TestPartition}/bulk-dup-{Guid.NewGuid():N}";

        var response = await Mesh
            .Observe<CreateNodesResponse>(new CreateNodesRequest(ImmutableList.Create(
                Node(path, "# first"), Node(path, "# second"))))
            .Select(d => d.Message)
            .Should().Emit();

        response.Success.Should().BeFalse();
        response.RejectionReason.Should().Be(NodeCreationRejectionReason.InvalidPath);
        response.FailedPath.Should().Be(path);
    }

    /// <summary>
    /// A null entry in the batch (deserialization artifact / caller bug) refuses the batch as a
    /// STRUCTURED response — never a NullReferenceException swallowed by the handler, and never a
    /// throw inside the pipeline's permission evaluation.
    /// </summary>
    [Fact(Timeout = 30_000)]
    public async Task BulkCreate_NullEntry_RefusedStructurally()
    {
        var path = $"{TestPartition}/bulk-null-{Guid.NewGuid():N}";

        var response = await Mesh
            .Observe<CreateNodesResponse>(new CreateNodesRequest(ImmutableList.Create(
                Node(path, "# fine"), null!)))
            .Select(d => d.Message)
            .Should().Emit();

        response.Success.Should().BeFalse();
        response.RejectionReason.Should().Be(NodeCreationRejectionReason.ValidationFailed);
        response.Created.Should().BeEmpty();

        var storage = Mesh.ServiceProvider.GetRequiredService<IStorageAdapter>();
        var sibling = await storage.Read(path, Mesh.JsonSerializerOptions).FirstAsync().ToTask();
        sibling.Should().BeNull("nothing may land from a refused batch");
    }

    /// <summary>
    /// The empty batch is a trivial success — no round-trip side effects, empty result lists.
    /// </summary>
    [Fact(Timeout = 30_000)]
    public async Task BulkCreate_EmptyBatch_TrivialOk()
    {
        var response = await NodeFactory.CreateNodes(Array.Empty<MeshNode>()).Should().Emit();
        response.Success.Should().BeTrue(response.Error ?? "");
        response.Created.Should().BeEmpty();
        response.Existing.Should().BeEmpty();
    }
}
