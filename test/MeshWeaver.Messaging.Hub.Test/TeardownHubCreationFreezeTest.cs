using System.Threading;
using MeshWeaver.Fixture;
using Xunit;

namespace MeshWeaver.Messaging.Hub.Test;

/// <summary>
/// Pins the teardown hub-creation freeze CASCADE (issue #613, the FutuRe.Test
/// exit=139 teardown SIGSEGV).
///
/// <para>The defect: <c>MessageHub.Dispose</c> froze creation only on the disposing
/// hub's OWN <c>HostedHubsCollection</c>; every descendant collection stayed open
/// until the async dispose cascade reached it. A straggler emission (a workspace
/// Reduce, a routed enrichment) landing on a mid-tree hub in that window entered
/// full hub CONSTRUCTION — <c>MessageHubConfiguration</c> → <c>TypeRegistry</c> ctor
/// → <c>XmlDocs.Summary</c> — walking type metadata of compiled-NodeType assemblies
/// whose collectible ALCs were already unloading. Best case: an
/// ObjectDisposedException storm off the disposed Autofac scope (the
/// teardown-straggler log). Worst case: SIGSEGV in <c>String.Ctor</c> over a span
/// into the unloaded assembly image (the CI dump, thread 22, "address not mapped").</para>
///
/// <para>The freeze must therefore be visible on EVERY level of the subtree
/// SYNCHRONOUSLY when <c>root.Dispose()</c> returns — before the async shutdown
/// pipeline has run at all. To pin exactly that (and not accidentally pass because
/// the async cascade already reached the children), the root's single-threaded
/// action block is deliberately stalled by a gated handler for the duration of the
/// assertions: the posted ShutdownRequest cannot be processed, so the ONLY thing
/// that can freeze the descendants is the synchronous cascade inside Dispose().</para>
/// </summary>
public class TeardownHubCreationFreezeTest(ITestOutputHelper output) : HubTestBase(output)
{
    private record Blocker;

    [Fact]
    public void DisposingRoot_FreezesHubCreation_AcrossTheWholeSubtree_BeforeAsyncShutdownRuns()
    {
        using var handlerEntered = new ManualResetEventSlim(false);
        using var releaseHandler = new ManualResetEventSlim(false);

        var client = GetClient();
        // Independent root so disposing it cannot interfere with the fixture's own hubs.
        var root = client.ServiceProvider.CreateMessageHub(
            new Address("freeze-root", "1"),
            c => c
                .WithPostingIdentity(PostingIdentity.System)
                .WithTypes(typeof(Blocker))
                .WithHandler<Blocker>((_, request) =>
                {
                    handlerEntered.Set();
                    // Deliberately ignores cancellation: keeps the root's action
                    // block busy so the ShutdownRequest posted by Dispose() cannot
                    // be processed until the test releases the gate.
                    releaseHandler.Wait(TimeSpan.FromSeconds(30));
                    return request.Processed();
                }));

        var child = root.GetHostedHub(new Address("freeze-child", "1"));
        var grandchild = child.GetHostedHub(new Address("freeze-grandchild", "1"));
        child.Should().NotBeNull();
        grandchild.Should().NotBeNull();

        try
        {
            // Stall the root's message loop, then dispose. The async shutdown
            // pipeline is provably NOT running during the assertions below —
            // any freeze observed on the descendants came from the synchronous
            // cascade inside Dispose().
            root.Post(new Blocker(), o => o.WithTarget(root.Address));
            handlerEntered.Wait(TimeSpan.FromSeconds(10)).Should().BeTrue("the blocker handler must be running");

            root.Dispose();

            root.GetHostedHub(new Address("freeze-child", "2"), HostedHubCreation.Always)
                .Should().BeNull("the disposing root must refuse new hosted hubs");
            child.GetHostedHub(new Address("freeze-grandchild", "2"), HostedHubCreation.Always)
                .Should().BeNull("root disposal must freeze creation on child collections synchronously");
            grandchild.GetHostedHub(new Address("freeze-greatgrandchild", "1"), HostedHubCreation.Always)
                .Should().BeNull("root disposal must freeze creation arbitrarily deep in the subtree");

            // Only CREATION is refused — the drain still needs to resolve live hubs.
            root.GetHostedHub(new Address("freeze-child", "1"), HostedHubCreation.Never)
                .Should().BeSameAs(child);
            child.GetHostedHub(new Address("freeze-grandchild", "1"), HostedHubCreation.Never)
                .Should().BeSameAs(grandchild);
        }
        finally
        {
            releaseHandler.Set();
        }
    }
}
