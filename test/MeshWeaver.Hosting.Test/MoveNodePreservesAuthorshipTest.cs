using System;
using System.Reactive.Linq;
using System.Threading.Tasks;
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
/// A MOVE relocates a node. It is not a creation and it is not a content edit, so
/// <see cref="MeshNode.CreatedDate"/>, <see cref="MeshNode.CreatedBy"/>,
/// <see cref="MeshNode.LastModified"/> and <see cref="MeshNode.LastModifiedBy"/> must survive it —
/// on the node named in the request AND on every descendant that rides along. Issue #3263.
///
/// <para>Measured on memex.systemorph.com 2026-09-03: one subtree move re-stamped ~80 nodes,
/// including a commercial proposal, as created that minute by the person who ran the move. Nothing
/// recovers the old values — there is no version history to fall back on — so this is permanent
/// data loss, not a cosmetic wrongness.</para>
///
/// <para>The COPY case is the CONTROL ARM, and it is a different answer on purpose: a copy is a new
/// node and is stamped for the copier. Without it "preserve the stamps" reads as a rule about
/// re-creating nodes in general, which it is not.</para>
/// </summary>
public class MoveNodePreservesAuthorshipTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private AccessService Access => Mesh.ServiceProvider.GetRequiredService<AccessService>();

    private static readonly AccessContext Author =
        new() { ObjectId = "author-a", Name = "Author A" };

    private static readonly AccessContext Mover =
        new() { ObjectId = "mover-b", Name = "Mover B" };

    [Fact(Timeout = 60000)]
    public async Task Move_KeepsTheStamps_OnTheNodeAndOnItsDescendants()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var sourceRoot = $"{TestPartition}/proposal-{suffix}";
        var sourceChild = $"{sourceRoot}/Pricing";
        var targetRoot = $"{TestPartition}/filed-{suffix}";
        var targetChild = $"{targetRoot}/Pricing";

        Access.SetCircuitContext(Author);
        await NodeFactory.CreateNode(MeshNode.FromPath(sourceRoot) with
        {
            Name = "Proposal",
            NodeType = "Markdown",
            State = MeshNodeState.Active,
        }).Should().Within(TestTimeouts.Convergence).Emit();
        await NodeFactory.CreateNode(MeshNode.FromPath(sourceChild) with
        {
            Name = "Pricing",
            NodeType = "Markdown",
            State = MeshNodeState.Active,
        }).Should().Within(TestTimeouts.Convergence).Emit();

        var rootBefore = await ReadExisting(sourceRoot);
        var childBefore = await ReadExisting(sourceChild);

        rootBefore.CreatedBy.Should().Be(Author.ObjectId, "precondition: the author created it");
        childBefore.CreatedBy.Should().Be(Author.ObjectId, "precondition: the author created it");
        rootBefore.CreatedDate.Should().NotBe(default, "precondition: creation is stamped");
        childBefore.LastModified.Should().NotBe(default, "precondition: modification is stamped");

        Access.SetCircuitContext(Mover);
        var moved = await ObserveNodeOperation(new MoveNodeRequest(sourceRoot, targetRoot))
            .Should().Within(TestTimeouts.Convergence).Emit();
        moved.Message.Success.Should().BeTrue(moved.Message.Error ?? "the move must succeed");

        var rootAfter = await ReadExisting(targetRoot);
        var childAfter = await ReadExisting(targetChild);

        Output.WriteLine($"root  before: createdBy={rootBefore.CreatedBy} createdDate={rootBefore.CreatedDate:O} lastModified={rootBefore.LastModified:O} lastModifiedBy={rootBefore.LastModifiedBy}");
        Output.WriteLine($"root  after : createdBy={rootAfter.CreatedBy} createdDate={rootAfter.CreatedDate:O} lastModified={rootAfter.LastModified:O} lastModifiedBy={rootAfter.LastModifiedBy}");
        Output.WriteLine($"child before: createdBy={childBefore.CreatedBy} createdDate={childBefore.CreatedDate:O} lastModified={childBefore.LastModified:O} lastModifiedBy={childBefore.LastModifiedBy}");
        Output.WriteLine($"child after : createdBy={childAfter.CreatedBy} createdDate={childAfter.CreatedDate:O} lastModified={childAfter.LastModified:O} lastModifiedBy={childAfter.LastModifiedBy}");

        rootAfter.CreatedBy.Should().Be(rootBefore.CreatedBy, "a move is not a creation");
        rootAfter.CreatedDate.Should().Be(rootBefore.CreatedDate, "a move is not a creation");
        rootAfter.LastModified.Should().Be(rootBefore.LastModified, "a move is not a content edit");
        rootAfter.LastModifiedBy.Should().Be(rootBefore.LastModifiedBy, "a move is not a content edit");

        childAfter.CreatedBy.Should().Be(childBefore.CreatedBy, "the subtree rides along");
        childAfter.CreatedDate.Should().Be(childBefore.CreatedDate, "the subtree rides along");
        childAfter.LastModified.Should().Be(childBefore.LastModified, "the subtree rides along");
        childAfter.LastModifiedBy.Should().Be(childBefore.LastModifiedBy, "the subtree rides along");
    }

    /// <summary>
    /// The other half of the decision, pinned so it cannot be "simplified" into the move's rule: a
    /// COPY is a NEW node and is stamped for the copier. Preserving the original's
    /// <see cref="MeshNode.CreatedBy"/> here would hand the copy an owner who never touched it —
    /// and <c>AccessContextScope</c> impersonates precisely that identity — so "both operations
    /// preserve" is not a simplification, it is a different (wrong) answer.
    /// </summary>
    [Fact(Timeout = 60000)]
    public async Task Copy_StampsTheCopier_BecauseACopyIsANewNode()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var sourcePath = $"{TestPartition}/original-{suffix}";
        var targetPath = $"{TestPartition}/duplicate-{suffix}";

        Access.SetCircuitContext(Author);
        await NodeFactory.CreateNode(MeshNode.FromPath(sourcePath) with
        {
            Name = "Original",
            NodeType = "Markdown",
            State = MeshNodeState.Active,
        }).Should().Within(TestTimeouts.Convergence).Emit();

        var before = await ReadExisting(sourcePath);
        before.CreatedBy.Should().Be(Author.ObjectId, "precondition: the author created it");

        Access.SetCircuitContext(Mover);
        var copied = await ObserveNodeOperation(new CopyNodeRequest(sourcePath, targetPath))
            .Should().Within(TestTimeouts.Convergence).Emit();
        copied.Message.Success.Should().BeTrue(copied.Message.Error ?? "the copy must succeed");

        var after = await ReadExisting(targetPath);

        after.CreatedBy.Should().Be(Mover.ObjectId,
            "the copy is a new node — it was created by whoever copied it");
        after.LastModifiedBy.Should().Be(Mover.ObjectId,
            "nobody but the copier has ever written this node");
        after.CreatedDate.Should().NotBe(before.CreatedDate,
            "the copy came into existence at the moment of the copy, not when the original was written");

        var stillThere = await ReadExisting(sourcePath);
        stillThere.CreatedBy.Should().Be(Author.ObjectId,
            "a copy leaves the original — and its authorship — alone");
    }

    /// <summary>
    /// The authoritative single-node read — the owner-hub round-trip the base class exposes, not a
    /// query row: the whole point of this test is which stamps the node CARRIES, and a query result
    /// is a projection that need not carry them at all.
    /// </summary>
    private async Task<MeshNode> ReadExisting(string path) =>
        (await ReadNode(path).Should().Within(TestTimeouts.Convergence)
            .Match(n => n is not null, $"the node at {path} must exist"))!;
}
