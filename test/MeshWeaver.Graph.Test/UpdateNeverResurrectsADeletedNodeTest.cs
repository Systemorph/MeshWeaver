using System;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Fixture;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Hosting.Persistence;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// 🚨 <b>A delete wins over an update that commits after it.</b>
///
/// <para>A patch-driven update is made durable by <see cref="IPostCommitFlush"/>, whose storage write
/// is an upsert. The owner commits the patch against the node it holds in memory — and it keeps
/// holding the pre-delete node until its DisposeRequest lands — so a patch that committed AFTER a
/// delete removed the row used to write the row straight back: the delete answered
/// <c>removed=true</c> and the node was in storage again. The persistence sampler's save handler and
/// the dispose-flush already dropped such a write against the delete's tombstone; the post-commit
/// flush did not.</para>
///
/// <para>Seen in CI as an install record that outlived its deleted partition
/// (<c>InstallRecordFollowsItsPartitionTest</c>) and as an uninstalled package whose record was still
/// there after a confirmed drop (<c>PackageUninstallTest</c>): in both, the boot repair pass's
/// install-record migration was updating the very record the delete removed.</para>
/// </summary>
public class UpdateNeverResurrectsADeletedNodeTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private IStorageAdapter Storage => Mesh.ServiceProvider.GetRequiredService<IStorageAdapter>();

    private IPostCommitFlush Flush => Mesh.ServiceProvider.GetRequiredService<IPostCommitFlush>();

    private async Task<MeshNode> Seed(string id) =>
        await NodeFactory.CreateNode(
                new MeshNode(id, TestPartition) { Name = id, NodeType = "Markdown" })
            .Should().Within(30.Seconds()).Emit(cancellationToken: TestContext.Current.CancellationToken);

    private async Task<bool> Exists(string path) =>
        await Storage.Exists(path).Should().Within(10.Seconds()).Emit(
            cancellationToken: TestContext.Current.CancellationToken);

    /// <summary>
    /// THE FALSIFIER, deterministic: the owner's post-commit flush of the node as it stood before the
    /// delete arrives after the delete answered. Before the fix it re-created the row.
    /// </summary>
    [Fact]
    public async Task APostCommitFlushAfterTheDelete_IsRefused_AndTheNodeStaysGone()
    {
        var node = await Seed("flush-after-delete");

        (await NodeFactory.DeleteNode(node.Path).Should().Within(30.Seconds()).Emit())
            .Should().BeTrue("the premise: the delete removed the node");

        var late = node with { Name = "late update", Version = node.Version + 1 };
        // Producer -> test signal: only the flush's ERROR arm completes the subject, so a flush that
        // wrote (the pre-fix behaviour) leaves it empty and times the wait out.
        var failure = new AsyncSubject<Exception>();
        using var flushing = Flush.Flush(late).Subscribe(
            _ => { },
            ex =>
            {
                failure.OnNext(ex);
                failure.OnCompleted();
            });
        var refusal = await failure.Should().Within(10.Seconds()).Emit(
            "a flush of a node deleted after the update committed must be refused, so the writer "
            + "hears its update did not land");
        refusal.Message.Should().Contain("delete wins");

        (await Exists(node.Path)).Should().BeFalse(
            "a delete that answered removed=true must not be undone by an update that committed "
            + "at the owner after it");
    }

    /// <summary>Negative control: the same flush of a live node persists — the guard is scoped to
    /// deleted paths, not to every flush.</summary>
    [Fact]
    public async Task APostCommitFlushOfALiveNode_Persists()
    {
        var node = await Seed("flush-live");

        var updated = node with { Name = "updated", Version = node.Version + 1 };
        await Flush.Flush(updated).Should().Within(10.Seconds()).Emit();

        var stored = await Storage.Read(node.Path, Mesh.JsonSerializerOptions)
            .Should().Within(10.Seconds()).Emit(cancellationToken: TestContext.Current.CancellationToken);
        stored!.Name.Should().Be("updated");
    }

    /// <summary>Second control: a node created AGAIN after its delete is a new node, and its updates
    /// flush normally — the re-create supersedes the tombstone.</summary>
    [Fact]
    public async Task APostCommitFlushOfARecreatedNode_Persists()
    {
        var node = await Seed("flush-recreated");
        (await NodeFactory.DeleteNode(node.Path).Should().Within(30.Seconds()).Emit()).Should().BeTrue();
        var recreated = await Seed("flush-recreated");

        var updated = recreated with { Name = "after re-create", Version = recreated.Version + 1 };
        await Flush.Flush(updated).Should().Within(10.Seconds()).Emit();

        var stored = await Storage.Read(node.Path, Mesh.JsonSerializerOptions)
            .Should().Within(10.Seconds()).Emit(cancellationToken: TestContext.Current.CancellationToken);
        stored!.Name.Should().Be("after re-create");
    }
}
