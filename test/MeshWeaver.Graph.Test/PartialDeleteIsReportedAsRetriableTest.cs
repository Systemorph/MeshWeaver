using System.Reactive.Subjects;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// Issue #6351 - a recursive delete that fails AFTER it removed nodes has left a torn subtree, and its
/// failure must say so, and say that asking again is meaningful.
///
/// <para>Production shape: <c>[DeleteNode] unexpected path=Marketing partial-deleted=1383</c> - the operation
/// had removed 1,383 paths when one leaf's commit watchdog fired, and the failure the caller read was the
/// leaf's own sentence with nothing about the 1,383 nodes already gone. Here the store stops serving removals
/// after 40 of 160 (the write lane is held, whoever holds it), a leaf's no-progress watchdog fires, and the
/// caller is told the subtree is partially deleted and that the delete may be retried. A retry once the lane
/// serves again removes what is left.</para>
/// </summary>
public class PartialDeleteIsReportedAsRetriableTest(ITestOutputHelper output)
    : WideDeleteOnASerialisedWriteLaneTestBase(output)
{
    /// <inheritdoc />
    protected override int FanOutConcurrency => 8;

    [Fact(Timeout = 120000)]
    public async Task AFailureAfterRemovingNodes_SaysTheSubtreeIsPartial_AndThatARetryIsMeaningful()
    {
        var rootPath = await SeedWideTree("partial-reported");
        Storage.StallAfterDeletes = 40;

        var failure = new AsyncSubject<Exception>();
        using var deleting = NodeFactory.DeleteNode(rootPath).Subscribe(
            _ => { },
            ex =>
            {
                failure.OnNext(ex);
                failure.OnCompleted();
            });

        var reported = await failure.Should().Within(60.Seconds()).Emit(
            "a lane that stops serving removals must end the delete with a named failure");

        reported.Message.Should().Contain("made no progress",
            "the failure is still the leaf's own watchdog - the cause is not rewritten");
        (reported is UnauthorizedAccessException).Should().BeFalse(
            $"an availability failure must not reach the caller as a permission denial, got {reported.GetType().Name}");
        reported.Message.Should().Contain("partially deleted",
            "nodes were removed before the failure, so the caller has to be told the subtree is torn");
        reported.Message.Should().Contain("retrying",
            "the delete is idempotent, so the caller is told that asking again is meaningful");

        var left = await Storage.Inner.ListDescendantPaths(rootPath).Should().Within(10.Seconds()).Emit();
        left.Should().NotBeEmpty("the statement is only true if something really is left");
        left.Count.Should().BeLessThan(Leaves, "the statement is only true if something really was removed");
    }

    [Fact(Timeout = 120000)]
    public async Task ARetryOnceTheLaneServesAgain_RemovesWhatTheFailedDeleteLeftBehind()
    {
        var rootPath = await SeedWideTree("partial-retried");
        Storage.StallAfterDeletes = 40;

        var failure = new AsyncSubject<Exception>();
        using var deleting = NodeFactory.DeleteNode(rootPath).Subscribe(
            _ => { },
            ex =>
            {
                failure.OnNext(ex);
                failure.OnCompleted();
            });
        await failure.Should().Within(60.Seconds()).Emit("the first delete must fail while the lane is held");

        // The lane serves again.
        Storage.StallAfterDeletes = null;

        var deleted = await NodeFactory.DeleteNode(rootPath).Should().Within(90.Seconds()).Emit(
            "a retry must remove what the failed delete left behind");
        deleted.Should().BeTrue();

        (await Storage.Inner.ListDescendantPaths(rootPath).Should().Within(10.Seconds()).Emit())
            .Should().BeEmpty("every leaf must be gone after the retry");
        (await Storage.Inner.Exists(rootPath).Should().Within(10.Seconds()).Emit())
            .Should().BeFalse("the root must be gone after the retry");
    }
}
