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

namespace MeshWeaver.Hosting.Test;

/// <summary>
/// A move is copy-then-delete. When its reply is lost (#6107) the copy leg can LAND while the delete
/// leg never runs, leaving the same node at both addresses. Issue #6310: re-issuing the move then
/// failed in its copy leg with <see cref="NodeMoveRejectionReason.TargetAlreadyExists"/>, so a
/// half-landed move could never be completed — measured on memex.systemorph.com 2026-10-08, three
/// Feedback submissions stranded at their old address with every hand-over retry refused.
///
/// <para>The half-landing is reproduced deterministically by running exactly the copy leg a move
/// issues — a <see cref="CopyNodeRequest"/> with <c>PreserveAuthorship</c> — and nothing else. The
/// retry must then finish the move: target kept, source removed.</para>
///
/// <para>The NEGATIVE CONTROL is an unrelated node at the target. "Resume" must never be a
/// euphemism for "overwrite whatever is there": that move must still be refused, with the source
/// and the foreign target both untouched.</para>
/// </summary>
public class MoveResumesAHalfLandedMoveTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private AccessService Access => Mesh.ServiceProvider.GetRequiredService<AccessService>();

    [Fact(Timeout = 60000)]
    public async Task ARetriedMove_CompletesAMoveWhoseCopyLegLandedButWhoseDeleteNeverRan()
    {
        Access.SetCircuitContext(TestUsers.Admin);
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var source = $"{TestPartition}/submission-{suffix}";
        var sourceChild = $"{source}/Attachment";
        var target = $"{TestPartition}/inbox-{suffix}";
        var targetChild = $"{target}/Attachment";
        await Seed(source, "Submission");
        await Seed(sourceChild, "Attachment");
        var sourceBefore = await ReadExisting(source);

        // The half-landing: the copy leg of a move, ROOT ONLY — so the retry also has to carry the
        // descendant the first attempt never reached, not merely skip the root.
        var copied = await ObserveNodeOperation(new CopyNodeRequest(source, target)
            {
                IncludeDescendants = false,
                PreserveAuthorship = true,
            })
            .Should().Within(TestTimeouts.Convergence).Emit(cancellationToken: TestContext.Current.CancellationToken);
        copied.Message.Success.Should().BeTrue(copied.Message.Error ?? "precondition: the copy leg lands");

        var moved = await ObserveNodeOperation(new MoveNodeRequest(source, target))
            .Should().Within(TestTimeouts.Convergence).Emit(cancellationToken: TestContext.Current.CancellationToken);
        Output.WriteLine($"retry: success={moved.Message.Success} reason={moved.Message.RejectionReason} error={moved.Message.Error}");

        moved.Message.Success.Should().BeTrue(
            moved.Message.Error ?? "the target already IS the source relocated — the retry resumes at the delete leg");
        var targetAfter = await ReadExisting(target);
        targetAfter.CreatedDate.Should().Be(sourceBefore.CreatedDate, "the node at the target is the moved node");
        (await ReadExisting(targetChild)).Name.Should().Be("Attachment",
            "the descendant the first attempt never carried is carried by the retry");
        // Storage, not a hub read: the question is whether the delete leg removed the ROWS.
        (await Stored(source)).Should().BeNull("the move completed: the source is gone");
        (await Stored(sourceChild)).Should().BeNull("the move completed: the source subtree is gone");
    }

    /// <summary>
    /// Negative control: an UNRELATED node at the target — same path, a different node — is a real
    /// collision. Without this arm the test above is satisfied by a move that simply ignores
    /// <c>TargetAlreadyExists</c>, which would silently destroy the source after "copying" onto a
    /// node it never wrote.
    /// </summary>
    [Fact(Timeout = 60000)]
    public async Task AMoveOntoAnUnrelatedNode_IsStillRefused_AndDeletesNothing()
    {
        Access.SetCircuitContext(TestUsers.Admin);
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var source = $"{TestPartition}/submission-{suffix}";
        var target = $"{TestPartition}/occupied-{suffix}";
        await Seed(source, "Submission");
        await Seed(target, "Someone else's node");

        var moved = await ObserveNodeOperation(new MoveNodeRequest(source, target))
            .Should().Within(TestTimeouts.Convergence).Emit(cancellationToken: TestContext.Current.CancellationToken);
        Output.WriteLine($"collision: success={moved.Message.Success} reason={moved.Message.RejectionReason} error={moved.Message.Error}");

        moved.Message.Success.Should().BeFalse("the target holds a different node");
        moved.Message.RejectionReason.Should().Be(NodeMoveRejectionReason.TargetAlreadyExists);
        (await ReadExisting(source)).Name.Should().Be("Submission", "a refused move removes nothing");
        (await ReadExisting(target)).Name.Should().Be("Someone else's node", "a refused move overwrites nothing");
    }

    private IStorageAdapter Storage => Mesh.ServiceProvider.GetRequiredService<IStorageAdapter>();

    private async Task<MeshNode?> Stored(string path) =>
        await Storage.Read(path, Mesh.JsonSerializerOptions).Take(1).DefaultIfEmpty(null)
            .Should().Within(TestTimeouts.Convergence).Emit(cancellationToken: TestContext.Current.CancellationToken);

    private async Task Seed(string path, string name) =>
        await NodeFactory.CreateNode(MeshNode.FromPath(path) with
        {
            Name = name,
            NodeType = "Markdown",
            State = MeshNodeState.Active,
        }).Should().Within(TestTimeouts.Convergence).Emit(cancellationToken: TestContext.Current.CancellationToken);

    private async Task<MeshNode> ReadExisting(string path) =>
        (await ReadNode(path).Should().Within(TestTimeouts.Convergence)
            .Match(n => n is not null, $"the node at {path} must exist",
                cancellationToken: TestContext.Current.CancellationToken))!;
}
