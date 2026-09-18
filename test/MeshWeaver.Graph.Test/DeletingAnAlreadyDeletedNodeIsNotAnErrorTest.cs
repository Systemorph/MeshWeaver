using System;
using System.Collections.Generic;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Hosting.Persistence;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// Systemorph/MeshWeaver#4668 — deleting something that is ALREADY gone is the operation's
/// postcondition, not a failure.
///
/// <para><b>The production line</b>, memex 2026-09-17T16:54:55Z, verbatim:</para>
/// <code>
/// fail: MeshWeaver.Blazor.Components.CollaborativeMarkdownView[0]
///       Deleting comment on CollaborationNotus/PrereadToNotus20260918/_Comment/b1bbf6a2 …
///       System.InvalidOperationException: Node not found:
///           CollaborationNotus/PrereadToNotus20260918/_Comment/b1bbf6a2
/// </code>
///
/// <para><b>What the user did.</b> Clicked Delete on a comment. The comment was already gone —
/// deleted by the other person on the document, or by their own first click — and the platform
/// answered that it could not remove the thing they wanted removed BECAUSE it was already removed.
/// The comment list that fed the click is a live query subscription, so it reconciles on the very
/// next frame; the stale window is a race, and a race is not closable by asking first. A
/// client-side existence check has a negative that can be stale by the time the delete lands —
/// the same reason <c>CreateOrUpdateNodeRequest</c> exists on the create side.</para>
///
/// <para><b>What must NOT be the fix.</b> Swallowing the error. The whole point is that the two
/// outcomes stay distinguishable: the delete SUCCEEDS, and it says whether it removed anything
/// (<c>true</c>/<c>false</c> off <see cref="IMeshService.DeleteNode"/>,
/// <c>DeleteNodeResponse.AlreadyAbsent</c> on the wire). Every assertion below is written so that a
/// <c>catch</c>-and-continue would FAIL it: a swallow can produce "no exception", it cannot produce
/// a <c>false</c> next to a <c>true</c>, and it would take the third test's real refusal down with
/// it.</para>
///
/// <para><b>Why sequential rather than overlapped.</b> Two genuinely concurrent deletes race on
/// which one reads the root first, so "exactly one reports the removal" is not a deterministic
/// claim. It is also not the claim that matters: both the double-click and the stale-view case
/// present the handler with the SAME state — a delete arriving for a node that is already gone —
/// and that state is what the second delete here creates, deterministically.</para>
/// </summary>
public class DeletingAnAlreadyDeletedNodeIsNotAnErrorTest(ITestOutputHelper output)
    : MonolithMeshTestBase(output)
{
    /// <summary>
    /// THE SUBJECT. Before the fix the second delete errored with
    /// <c>InvalidOperationException: Node not found: …</c>, so the awaited emission never arrived
    /// and this test failed on the <c>Emit</c> — it is a control, not a restatement of the new
    /// behaviour.
    /// </summary>
    [Fact]
    public async Task DeletingTheSameNodeTwice_SucceedsBothTimes_AndOnlyTheFirstRemovedAnything()
    {
        const string Id = "already-deleted-twice";
        var path = $"{TestPartition}/{Id}";

        await NodeFactory.CreateNode(
                new MeshNode(Id, TestPartition) { Name = "Doomed", NodeType = "Markdown" })
            .Should().Within(TestTimeouts.Convergence).Emit(
                cancellationToken: TestContext.Current.CancellationToken);

        var first = await NodeFactory.DeleteNode(path)
            .Should().Within(TestTimeouts.WriteConvergence).Emit(
                "the node exists, so the first delete removes it",
                cancellationToken: TestContext.Current.CancellationToken);
        first.Should().BeTrue("this call is the one that actually removed the node");

        // The STORE OF RECORD, not the report about it — the second delete is only the
        // already-absent case if the node is genuinely gone by now.
        var raw = Mesh.ServiceProvider.GetRawStorageAdapter<InMemoryStorageAdapter>()!;
        (await raw.Exists(path).Should().Within(TestTimeouts.Quick).Emit(
                cancellationToken: TestContext.Current.CancellationToken))
            .Should().BeFalse("the first delete reported success, so the node must be gone");

        // 🚨 THE ASSERTION #4668 IS ABOUT. This emission is what the fix makes possible at all:
        // before it, the observable ERRORED here and nothing was ever emitted.
        var second = await NodeFactory.DeleteNode(path)
            .Should().Within(TestTimeouts.WriteConvergence).Emit(
                "deleting what is already gone satisfies the delete's postcondition — it is a "
                + "success, and the user who clicked Delete on a comment somebody else had "
                + "already deleted must not be shown an error for it",
                cancellationToken: TestContext.Current.CancellationToken);

        // 🚨 AND THE HONEST HALF — the discriminator a swallowed exception could never produce.
        // `false` says "there was nothing here to remove"; a catch-and-continue would have to
        // invent a value, and the only one it could invent is the same `true` the first call gave.
        second.Should().BeFalse(
            "already-absent is a success that REMOVED NOTHING, and the caller is told which of "
            + "the two happened — a prune that expected to remove something and removed nothing, "
            + "or a mistyped path, must still be legible");
    }

    /// <summary>
    /// The path that NEVER existed — same postcondition, same answer. Distinct from the test above
    /// because it reaches the handler without a preceding delete of its own, which is the shape a
    /// stale UI list or a re-run script produces.
    /// </summary>
    [Fact]
    public async Task DeletingAPathThatNeverExisted_SucceedsHavingRemovedNothing()
    {
        var path = $"{TestPartition}/never-existed-at-all";

        var removed = await NodeFactory.DeleteNode(path)
            .Should().Within(TestTimeouts.WriteConvergence).Emit(
                "there is nothing at that path, so the delete's postcondition already holds",
                cancellationToken: TestContext.Current.CancellationToken);

        removed.Should().BeFalse("nothing was there, so nothing was removed");
    }

    /// <summary>
    /// NEGATIVE CONTROL — the change makes ONE outcome a success, not deletes in general. A
    /// validator that refuses this node still takes the delete down, loudly, with its own reason
    /// intact. Without this, every assertion above would also pass on a <c>DeleteNode</c> that had
    /// simply stopped being able to fail.
    /// </summary>
    [Fact]
    public async Task ARefusedDelete_StillFails_SoTheChangeIsNotDeletesNeverError()
    {
        const string Id = "refused-delete";
        var path = $"{TestPartition}/{Id}";

        await NodeFactory.CreateNode(
                new MeshNode(Id, TestPartition) { Name = "Protected", NodeType = "Markdown" })
            .Should().Within(TestTimeouts.Convergence).Emit(
                cancellationToken: TestContext.Current.CancellationToken);

        refusing.Armed = true;

        // Producer -> test signal: the delete's ERROR arm completes an AsyncSubject the assertion
        // helpers await. A success emission leaves it empty and times the wait out, which is itself
        // the failure this test must report.
        var failure = new AsyncSubject<Exception>();
        using var deleting = NodeFactory.DeleteNode(path).Subscribe(
            _ => { },
            ex =>
            {
                failure.OnNext(ex);
                failure.OnCompleted();
            });

        var reported = await failure.Should().Within(TestTimeouts.WriteConvergence).Emit(
            "a validator refusal is a verdict about a node that IS there — nothing about #4668 "
            + "touches it",
            cancellationToken: TestContext.Current.CancellationToken);

        Output.WriteLine(reported.Message);
        reported.Message.Should().Contain(RefusalReason,
            "the refusing validator's own sentence has to survive to the caller");

        var raw = Mesh.ServiceProvider.GetRawStorageAdapter<InMemoryStorageAdapter>()!;
        (await raw.Exists(path).Should().Within(TestTimeouts.Quick).Emit(
                cancellationToken: TestContext.Current.CancellationToken))
            .Should().BeTrue("a refused delete removes nothing");
    }

    private const string RefusalReason = "this node is spoken for";

    private readonly RefuseOneDeletionValidator refusing =
        new($"{TestPartition}/refused-delete", RefusalReason);

    /// <inheritdoc />
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder).ConfigureServices(services => services
            .AddSingleton<INodeValidator>(refusing));
}

/// <summary>
/// Refuses the delete of ONE path, once armed, and accepts everything else. Instance state, never
/// static: the mesh owns this singleton for the life of the test's mesh, so nothing bleeds into
/// another suite in the same process.
/// </summary>
internal sealed class RefuseOneDeletionValidator(string refusedPath, string reason) : INodeValidator
{
    /// <summary>
    /// Off until the node is built: a create must not be refused, and the refusal is only
    /// meaningful once the delete asks.
    /// </summary>
    public bool Armed { get; set; }

    /// <inheritdoc />
    public IReadOnlyCollection<NodeOperation> SupportedOperations { get; } = [NodeOperation.Delete];

    /// <inheritdoc />
    public IObservable<NodeValidationResult> Validate(NodeValidationContext context)
        => Observable.Return(
            Armed && string.Equals(context.Node.Path, refusedPath, StringComparison.OrdinalIgnoreCase)
                ? NodeValidationResult.Invalid(reason)
                : NodeValidationResult.Valid());
}
