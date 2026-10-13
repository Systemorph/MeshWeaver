using System;
using System.Reactive.Linq;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// <see cref="DeleteNodeRequest.ExpectedVersion"/> is a precondition the owning hub checks inside
/// the delete (#6621). A caller that read a shared node, decided it may go, and deletes it can say
/// "only if it is still the version I read". If another writer changed the node in between, the
/// delete answers <see cref="NodeDeletionRejectionReason.Conflict"/> and removes nothing.
/// </summary>
public class DeleteExpectedVersionTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private IStorageAdapter Storage => Mesh.ServiceProvider.GetRequiredService<IStorageAdapter>();

    private async Task<MeshNode> Seed(string id, string? ns = null) =>
        await NodeFactory.CreateNode(
                new MeshNode(id, ns ?? TestPartition) { Name = id, NodeType = "Markdown" })
            .Should().Within(TestTimeouts.Convergence).Emit(cancellationToken: TestContext.Current.CancellationToken);

    private async Task<MeshNode> Durable(string path) =>
        (await Storage.Read(path, Mesh.JsonSerializerOptions).Should().Within(TestTimeouts.Convergence)
            .Emit(cancellationToken: TestContext.Current.CancellationToken))!;

    private async Task<bool> Exists(string path) =>
        await Storage.Exists(path).Should().Within(TestTimeouts.Convergence).Emit(
            cancellationToken: TestContext.Current.CancellationToken);

    private async Task<DeleteNodeResponse> Delete(DeleteNodeRequest request) =>
        (await ObserveNodeOperation(request).Should().Within(TestTimeouts.Convergence).Emit(
            "a delete always answers", TestContext.Current.CancellationToken)).Message;

    /// <summary>
    /// THE FALSIFIER: the node is read, someone else changes it, then the delete that names the
    /// version it read arrives. Before #6621 nothing could express the precondition, and the delete
    /// removed the other writer's change.
    /// </summary>
    [Fact]
    public async Task ADeleteNamingAStaleVersion_IsRefusedAsConflict_AndRemovesNothing()
    {
        var node = await Seed("expected-version-stale");
        var read = (await Durable(node.Path)).Version;

        // Another writer moves the node past the version the deleter read.
        await Mesh.GetMeshNodeStream(node.Path).Update(n => n with { Name = "changed by someone else" })
            .Should().Within(TestTimeouts.Convergence).Emit(cancellationToken: TestContext.Current.CancellationToken);
        var now = (await Durable(node.Path)).Version;
        now.Should().BeGreaterThan(read, "the premise: the other writer's change minted a newer version");

        var response = await Delete(new DeleteNodeRequest(node.Path) { ExpectedVersion = read });
        Output.WriteLine($"[delete] success={response.Success} reason={response.RejectionReason} error={response.Error}");

        response.Success.Should().BeFalse("the node changed after it was read");
        response.RejectionReason.Should().Be(NodeDeletionRejectionReason.Conflict,
            "a changed node is its own outcome, distinct from a denial or a validation failure");
        (await Exists(node.Path)).Should().BeTrue(
            "a refused precondition deletes nothing — the other writer's change survives");
    }

    /// <summary>The precondition holds: the delete proceeds exactly as an unconditional one.</summary>
    [Fact]
    public async Task ADeleteNamingTheCurrentVersion_Deletes()
    {
        var node = await Seed("expected-version-current");
        var current = (await Durable(node.Path)).Version;

        var response = await Delete(new DeleteNodeRequest(node.Path) { ExpectedVersion = current });

        response.Success.Should().BeTrue(response.Error ?? "the version still matches");
        response.AlreadyAbsent.Should().BeFalse("this delete removed the node");
        (await Exists(node.Path)).Should().BeFalse("the node is gone");
    }

    /// <summary>
    /// Root only. A recursive delete's precondition is the ROOT's: the cascade's leaf requests must
    /// not inherit it. A leaf carries its own version, so if a leaf inherited the root's precondition
    /// it would refuse, and the matching delete would fail half-way.
    /// </summary>
    [Fact]
    public async Task ARecursiveDelete_ChecksTheRootOnly()
    {
        var root = await Seed("expected-version-tree");
        var child = await Seed("child", root.Path);
        // Move the child's version away from the root's, so an inherited check would refuse it.
        await Mesh.GetMeshNodeStream(child.Path).Update(n => n with { Name = "child v2" })
            .Should().Within(TestTimeouts.Convergence).Emit(cancellationToken: TestContext.Current.CancellationToken);
        await Mesh.GetMeshNodeStream(child.Path).Update(n => n with { Name = "child v3" })
            .Should().Within(TestTimeouts.Convergence).Emit(cancellationToken: TestContext.Current.CancellationToken);
        var rootVersion = (await Durable(root.Path)).Version;
        var childVersion = (await Durable(child.Path)).Version;
        Output.WriteLine($"[versions] root={rootVersion} child={childVersion}");
        childVersion.Should().NotBe(rootVersion, "the premise of this test");

        var response = await Delete(new DeleteNodeRequest(root.Path)
        {
            Recursive = true,
            ConfirmWarnings = true,
            ExpectedVersion = rootVersion,
        });

        response.Success.Should().BeTrue(response.Error ?? "the root's version matches");
        (await Exists(child.Path)).Should().BeFalse("the cascade removed the child");
        (await Exists(root.Path)).Should().BeFalse("and the root");
    }

    /// <summary>An absent node still answers the idempotent success, whatever version was named.</summary>
    [Fact]
    public async Task ADeleteOfAnAbsentNode_WithAnExpectedVersion_IsAlreadyAbsent()
    {
        var response = await Delete(new DeleteNodeRequest($"{TestPartition}/never-existed-6621") { ExpectedVersion = 3 });

        response.Success.Should().BeTrue(response.Error ?? "the postcondition already holds");
        response.AlreadyAbsent.Should().BeTrue();
    }
}
