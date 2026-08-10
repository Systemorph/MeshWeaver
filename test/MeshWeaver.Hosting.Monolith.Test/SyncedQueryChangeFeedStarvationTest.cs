using System;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Reactive.Threading.Tasks;
using System.Threading.Tasks;
using MeshWeaver.Graph;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Hosting.Monolith.Test;

/// <summary>
/// End-to-end pin for issue #1053: a LIVE children query must keep re-emitting after a create,
/// no matter what state some OTHER subscriber of the change feed is in.
///
/// <para>The symptom was a 30 s timeout where a live listing never re-emitted although the create
/// completed successfully — reported against
/// <c>MeshNodeAgentFileStoreTest.ListChildren_StaysLive_AndReEmitsWhenAFileAppears</c> /
/// <c>Search_StaysLive_AndReEmitsWhenMatchingContentAppears</c>, and intermittent because it needs
/// a second subscriber to be mid-teardown when the write lands.</para>
///
/// <para><b>What this test makes deterministic.</b> Every live synced query owns a
/// <c>persistence.Changes → changeBuffer</c> pipeline (<c>StorageAdapterMeshQueryProvider</c>), and
/// a pipeline caught in its teardown window has a DISPOSED <c>changeBuffer</c> while its feed
/// subscription is still delivering. One-shot queries — <c>IMeshService.QueryAsync</c>,
/// autocomplete, path resolution — open and tear one down constantly, so on a busy mesh that
/// window is hit routinely; here it is staged explicitly, and placed BEFORE the listing's own
/// subscription because a plain <see cref="Subject{T}"/> fan-out aborts at the first throwing
/// observer and starves everyone after it. The in-memory adapter then swallowed the
/// <see cref="ObjectDisposedException"/> in a <c>catch { }</c>, so the dropped notification left no
/// trace anywhere.</para>
///
/// <para>The listing here is a plain <c>hub.GetQuery</c> — the same synced-query surface every
/// layout area and Blazor view data-binds to — so this is a liveness guarantee of the binding
/// path, not a property of the agent file store that reported it.</para>
/// </summary>
public class SyncedQueryChangeFeedStarvationTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Stages a query pipeline caught in its teardown window: the buffer it feeds is already
    /// disposed while the feed subscription still delivers into it.
    /// </summary>
    private static IDisposable SubscribeDeadPipeline(IStorageAdapter adapter)
    {
        var deadBuffer = new Subject<DataChangeNotification>();
        var feedSub = adapter.Changes.Subscribe(deadBuffer);
        deadBuffer.Dispose();
        return feedSub;
    }

    [Fact(Timeout = 120_000)]
    public async Task ALiveChildrenQuery_ReEmits_EvenWhenATornDownPipelineSitsOnTheChangeFeed()
    {
        var folder = $"{TestPartition}/live-{Guid.NewGuid():N}";
        await NodeFactory
            .CreateNode(new MeshNode("one", folder) { Name = "one", NodeType = "Markdown" })
            .FirstAsync().Timeout(Bound).ToTask();

        // 🚨 ORDER IS THE POINT: the dead pipeline attaches BEFORE the listing opens its own, so a
        // plain-Subject fan-out aborts before ever reaching the listing.
        using var dead = SubscribeDeadPipeline(
            Mesh.ServiceProvider.GetRequiredService<IStorageAdapter>());

        var listing = Mesh.GetQuery(
            $"starvation:{folder}", $"path:{folder} scope:children select:path,id,name,nodeType");

        // Establish the live subscription AND its snapshot before the second create — otherwise a
        // late Initial could carry "two" and the test would pass without ever exercising a delta.
        var seeded = await listing
            .Where(nodes => nodes.Any(n => n.Path == $"{folder}/one"))
            .FirstAsync().Timeout(Bound).ToTask();
        seeded.Select(n => n.Path).Should().Contain($"{folder}/one");

        var next = listing
            .Where(nodes => nodes.Any(n => n.Path == $"{folder}/two"))
            .FirstAsync().Timeout(Bound).ToTask();

        await NodeFactory
            .CreateNode(new MeshNode("two", folder) { Name = "two", NodeType = "Markdown" })
            .FirstAsync().Timeout(Bound).ToTask();

        (await next).Select(n => n.Path).Should().Contain([$"{folder}/one", $"{folder}/two"],
            "the create completed, so an Added was OWED to every live subscriber of the change "
            + "feed — a sibling subscriber caught mid-teardown must never be able to starve it "
            + "(issue #1053: a data-bound view that silently stops updating)");
    }
}
