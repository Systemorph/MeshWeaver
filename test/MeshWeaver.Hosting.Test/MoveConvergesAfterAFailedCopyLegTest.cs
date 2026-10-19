using System;
using System.Reactive.Linq;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Fixture;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Hosting.Test;

/// <summary>
/// Issue #6310, the half-landed move, driven through a REAL move whose copy leg FAILED part-way —
/// not the copy leg run on its own, which <see cref="MoveResumesAHalfLandedMoveTest"/> does. The
/// copy creates the root before its descendants, so a descendant refused at the target leaves the
/// root (and any sibling that landed) behind while the source is untouched. Once the cause is gone,
/// re-issuing the same move must CONVERGE: every landed node resumed, the missing one carried, the
/// source removed, nothing duplicated — and a further retry finds nothing left to move.
///
/// <para>The second arm is the fail-closed half: a landed node that was EDITED at the target after
/// the failed attempt, on a store that keeps no version history (the in-memory store's
/// <see cref="IVersionQuery"/> is the no-op). Whether the source changed since the copy cannot be
/// established there, so the retry is refused — and it neither overwrites the target's newer edit
/// nor deletes anything. The history-backed resume of that same shape is
/// <see cref="MoveResumesOverAnEditedTargetWithHistoryTest"/>.</para>
/// </summary>
public class MoveConvergesAfterAFailedCopyLegTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private AccessService Access => Mesh.ServiceProvider.GetRequiredService<AccessService>();
    private IStorageAdapter Storage => Mesh.ServiceProvider.GetRequiredService<IStorageAdapter>();

    [Fact(Timeout = 90_000)]
    public async Task AMoveWhoseCopyLegFailedPartWay_ConvergesOnRetry_WithoutDuplicatingOrLosingData()
    {
        Access.SetCircuitContext(TestUsers.Admin);
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var source = $"{TestPartition}/submission-{suffix}";
        var target = $"{TestPartition}/inbox-{suffix}";
        await Seed(source, "Submission");
        await Seed($"{source}/Attachment", "Attachment");
        await Seed($"{source}/Notes", "Notes");
        // The cause of the first failure: a foreign node where one descendant must land.
        await Seed($"{target}/Notes", "Someone else's notes");
        var sourceRoot = await ReadExisting(source);

        var first = await Move(source, target);
        Output.WriteLine($"first attempt: success={first.Success} reason={first.RejectionReason} error={first.Error}");
        first.Success.Should().BeFalse("precondition: a descendant's target is taken by a different node");
        first.RejectionReason.Should().Be(NodeMoveRejectionReason.TargetAlreadyExists);
        // The half-landing this issue is about: the copy wrote the root at the target, the delete
        // leg never ran, and the source subtree is whole.
        (await Stored(target)).Should().NotBeNull("precondition: the copy created the root before the refused descendant");
        (await Stored(source)).Should().NotBeNull("a failed copy leg deletes nothing at the source");
        (await Stored($"{source}/Attachment")).Should().NotBeNull();
        (await Stored($"{source}/Notes")).Should().NotBeNull();
        (await Stored($"{target}/Notes"))!.Name.Should().Be("Someone else's notes",
            "a failed copy leg never overwrites a node it did not write");

        // The operator clears the collision — the only thing that changed between the attempts.
        var cleared = await ObserveNodeOperation(new DeleteNodeRequest($"{target}/Notes"))
            .Should().Within(TestTimeouts.Convergence).Emit(cancellationToken: TestContext.Current.CancellationToken);
        cleared.Message.Success.Should().BeTrue(cleared.Message.Error ?? "precondition: the foreign node is removed");

        var retry = await Move(source, target);
        Output.WriteLine($"retry: success={retry.Success} reason={retry.RejectionReason} error={retry.Error}");
        retry.Success.Should().BeTrue(retry.Error ?? "the retry resumes what landed and carries what did not");

        var root = await ReadExisting(target);
        root.Name.Should().Be("Submission");
        root.CreatedDate.Should().Be(sourceRoot.CreatedDate, "the node at the target is the moved node, not a second copy");
        (await ReadExisting($"{target}/Attachment")).Name.Should().Be("Attachment");
        (await ReadExisting($"{target}/Notes")).Name.Should().Be("Notes",
            "the descendant the first attempt could not carry is carried — the source's, not the foreign one");
        // Storage, not a hub read: the question is whether the delete leg removed the ROWS.
        (await Stored(source)).Should().BeNull("the move completed: the source is gone");
        (await Stored($"{source}/Attachment")).Should().BeNull();
        (await Stored($"{source}/Notes")).Should().BeNull();

        // Converged means a further retry has nothing to do and changes nothing.
        var again = await Move(source, target);
        Output.WriteLine($"after convergence: success={again.Success} reason={again.RejectionReason} error={again.Error}");
        again.Success.Should().BeFalse("there is no source left to move");
        again.RejectionReason.Should().Be(NodeMoveRejectionReason.SourceNotFound);
        (await Stored(target))!.Name.Should().Be("Submission", "a retry after convergence touches nothing");
        (await Stored($"{target}/Notes"))!.Name.Should().Be("Notes");
    }

    [Fact(Timeout = 90_000)]
    public async Task ARetry_OverATargetEditedAfterTheCopy_WithNoHistory_IsRefusedAndKeepsBothSides()
    {
        Access.SetCircuitContext(TestUsers.Admin);
        Mesh.ServiceProvider.GetRequiredService<IVersionQuery>().RetainsHistory.Should().BeFalse(
            "precondition: this arm is the store that cannot prove the source unchanged");
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var source = $"{TestPartition}/submission-{suffix}";
        var target = $"{TestPartition}/inbox-{suffix}";
        await Seed(source, "Submission");

        // The half-landing: the copy leg a move issues, and no delete leg.
        var copied = await ObserveNodeOperation(new CopyNodeRequest(source, target) { PreserveAuthorship = true })
            .Should().Within(TestTimeouts.Convergence).Emit(cancellationToken: TestContext.Current.CancellationToken);
        copied.Message.Success.Should().BeTrue(copied.Message.Error ?? "precondition: the copy leg lands");

        // Somebody works on the relocated node — the triage inbox picked it up — before the retry.
        await Mesh.GetWorkspace().GetMeshNodeStream(target).Update(node => node with { Name = "Submission, triaged" })
            .Should().Within(TestTimeouts.Convergence).Emit(cancellationToken: TestContext.Current.CancellationToken);

        var retry = await Move(source, target);
        Output.WriteLine($"retry over an edited target: success={retry.Success} reason={retry.RejectionReason} error={retry.Error}");

        retry.Success.Should().BeFalse(
            "with no history, nothing establishes that the source is unchanged since the copy — the retry is refused, never guessed");
        retry.RejectionReason.Should().Be(NodeMoveRejectionReason.TargetAlreadyExists);
        retry.Error.Should().Contain("relocated by an earlier attempt",
            "refused by the source-unchanged check, after the target was recognised as this node relocated");
        (await Stored(target))!.Name.Should().Be("Submission, triaged", "the target's newer edit is never overwritten");
        (await Stored(source))!.Name.Should().Be("Submission", "a refused retry deletes nothing");
    }

    private async Task<MoveNodeResponse> Move(string source, string target) =>
        (await ObserveNodeOperation(new MoveNodeRequest(source, target))
            .Should().Within(TestTimeouts.Convergence).Emit(cancellationToken: TestContext.Current.CancellationToken))
        .Message;

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
