using System;
using System.Reactive.Linq;
using System.Threading.Tasks;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// The carrier for "one resolution per create operation" (MeshWeaver#4449 item 3): the checks of ONE
/// operation share a single view of whether the node's type owns its partition.
///
/// <para>Each property here is one the sharing depends on: the resolver runs once and only on
/// subscribe; a <c>null</c> ("could not be established") is a RECORDED decision rather than an
/// absent entry, so a starved resolution keeps every later check fail-closed instead of sending it
/// off to resolve again; and the key is the node TYPE, so a context copied for a different node can
/// never read an answer belonging to another type.</para>
/// </summary>
public sealed class PartitionOwnershipMemoTest
{
    private const string TypeA = "lib/Alpha";
    private const string TypeB = "lib/Beta";

    private static Task<bool?> Await(IObservable<bool?> source) =>
        source.FirstAsync().Timeout(TimeSpan.FromSeconds(10)).Await(TestContext.Current.CancellationToken);

    [Fact]
    public async Task SecondAsk_IsServedFromTheFirst_AndTheResolverRunsOnce()
    {
        var memo = new PartitionOwnershipMemo();
        var runs = 0;
        IObservable<bool?> Resolve()
        {
            runs++;
            return Observable.Return<bool?>(true);
        }

        var first = await Await(memo.Once(TypeA, Resolve));
        var second = await Await(memo.Once(TypeA, Resolve));

        Assert.True(first);
        Assert.True(second);
        Assert.Equal(1, runs);
        Assert.Equal(1, memo.Resolutions);
    }

    /// <summary>
    /// 🚨 The fail-closed half. <c>null</c> is the resolver's third answer — "the reads did not
    /// answer" — and every caller turns it into <c>Undetermined</c>, which refuses the operation. It
    /// has to be memoized AS an answer: re-resolving would let a second check reach a DIFFERENT
    /// verdict from the first inside one operation, which is precisely what sharing removes.
    /// </summary>
    [Fact]
    public async Task AnUnestablishedAnswer_IsRemembered_AsADecision()
    {
        var memo = new PartitionOwnershipMemo();
        var runs = 0;
        IObservable<bool?> Resolve()
        {
            runs++;
            return Observable.Return<bool?>(null);
        }

        var first = await Await(memo.Once(TypeA, Resolve));
        var second = await Await(memo.Once(TypeA, Resolve));

        Assert.Null(first);
        Assert.Null(second);
        Assert.Equal(1, runs);
        Assert.Equal(1, memo.Resolutions);
    }

    [Fact]
    public async Task ADifferentNodeType_IsResolvedSeparately()
    {
        var memo = new PartitionOwnershipMemo();

        var alpha = await Await(memo.Once(TypeA, () => Observable.Return<bool?>(true)));
        var beta = await Await(memo.Once(TypeB, () => Observable.Return<bool?>(false)));

        Assert.True(alpha);
        Assert.False(beta);
        Assert.Equal(2, memo.Resolutions);
    }

    /// <summary>
    /// Cold: a check that composes the memo's observable and never subscribes pays nothing and
    /// records nothing. A memo that resolved on CALL would charge every caller that builds a chain
    /// it later discards — and would count as a resolution in the diagnostic.
    /// </summary>
    [Fact]
    public void WithoutASubscription_NothingResolves()
    {
        var memo = new PartitionOwnershipMemo();
        var runs = 0;

        _ = memo.Once(TypeA, () =>
        {
            runs++;
            return Observable.Return<bool?>(true);
        });

        Assert.Equal(0, runs);
        Assert.Equal(0, memo.Resolutions);
    }
}
