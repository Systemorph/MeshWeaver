using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Threading.Tasks;
using MeshWeaver.Graph;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Hosting.Monolith.Test;

/// <summary>
/// 🚨 <b>#1868 — <c>Build</c> must not construct another hub</b>, for the Build node's own hub.
///
/// <para><c>BuildNodeType.CreateMeshNode</c> registered <c>InstallClaimArbiter</c> on the
/// SYNCHRONOUS <c>WithInitialization</c> overload. That runs inside
/// <c>MessageHubConfiguration.Build</c>, before <c>StartMessageProcessing()</c> — and the arbiter
/// resolves the hub's own node stream (<c>workspace.GetMeshNodeStream()</c> →
/// <c>SynchronizationStream..ctor</c> → <c>GetHostedHub(sync/{clientId},
/// HostedHubCreation.Always)</c>), so the Build node built a child hub, and a second Autofac
/// container, from inside its own construction.</para>
///
/// <para>Two things are wrong with that, and only the first is #1868's general one. <b>(a)</b> A
/// disposal racing this hub's creation races a TREE of constructions rather than one frame — the
/// shape behind the whole shutdown-race family (#645/#715/#967/#1573). <b>(b)</b> Specific to this
/// site: the arbiter's triggers were wired against a workspace whose data sources had not been
/// started yet, because <c>DataExtensions</c> moved exactly that work
/// (<c>StartDataSourcesAndOpenGate</c>) onto the init turn in #2045. The arbiter is what re-grants a
/// claim whose holder has gone away — the recovery #2076 needed and never got — so wiring it
/// against a half-built workspace is not a tidiness question.</para>
///
/// <para>The observable overload runs on the <c>InitializeHubRequest</c> turn, after <c>Build</c>
/// has returned and still before the Initialize gate opens, so nothing observable to a message
/// changes; <c>BuildCoordinationTest</c> is what proves the arbiter still arbitrates.</para>
/// </summary>
public class BuildNodeHubBuildsNoHubTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    /// <summary>
    /// Activating the Build root's hub must construct no hub of its own.
    ///
    /// <para><b>Non-vacuity.</b> The test asserts first that the Build hub is up AND that it opened a
    /// <c>sync/</c> stream — without that there would be nothing that COULD have nested. On
    /// <c>origin/main</c> that same <c>sync/</c> address is what the violation list names.</para>
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task ActivatingTheBuildNodeHub_BuildsNoHubOfItsOwn()
    {
        // Materialise the root, then READ it through its own node stream — the read is what routes
        // a message to the per-node hub and therefore activates it (the same path the GUI takes;
        // the create alone goes through the mesh router and activates nothing).
        (await Mesh.EnsureBuildNode().Take(1).Timeout(TimeSpan.FromSeconds(30)).ToTask())
            .Should().NotBeNull();
        (await ReadNode(BuildNodeType.RootPath).Should().Emit()).Should().NotBeNull();

        var hubs = Walk(Mesh).ToArray();
        var buildHub = hubs.SingleOrDefault(h =>
            string.Equals(h.Address.ToString(), BuildNodeType.RootPath, StringComparison.Ordinal));
        buildHub.Should().NotBeNull(
            $"the Build node's hub {BuildNodeType.RootPath} must have been activated, or this test "
            + "proves nothing");

        // 🚨 The subtree is walked from the hub, not filtered by address prefix: a sync sub-hub's
        // address is a bare `sync/{clientId}`, so a prefix filter would silently miss the very
        // hub whose creation this test is about.
        var subtree = Walk(buildHub!).ToArray();
        foreach (var h in subtree)
            Output.WriteLine($"hub: {h.Address}");

        subtree.Select(h => h.Address.ToString())
            .Should().Contain(a => a.StartsWith("sync/", StringComparison.Ordinal),
                "the arbiter must still open the hub's own node stream (which always creates a "
                + "sync/{clientId} sub-hub) — moving it to the observable WithInitialization "
                + "overload changes WHEN, never WHETHER");

        var violations = subtree
            .OfType<MessageHub>()
            .SelectMany(h => h.HubsConstructedDuringBuild.Select(a => $"{h.Address} → {a}"))
            .ToArray();

        violations.Should().BeEmpty(
            "a SyncBuildupAction reached hub construction: Build runs those inline, before "
            + "StartMessageProcessing, so a disposal racing this hub's creation races a TREE of "
            + "constructions rather than one frame (#1868) — and here it also wired the claim "
            + "arbiter against a workspace whose data sources had not been started yet. Constructed "
            + "during Build: " + string.Join(", ", violations));
    }

    /// <summary>
    /// Every hub in the hosted-hub tree rooted at <paramref name="root"/>, deduplicated by
    /// collection identity — a hub that shares its parent's collection must not be walked twice
    /// (the double-count trap <c>ProbeHubCostTest.HubCreationCounter</c> documents).
    /// </summary>
    private static IEnumerable<IMessageHub> Walk(IMessageHub root)
    {
        var seen = new HashSet<HostedHubsCollection>();
        var stack = new Stack<IMessageHub>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var hub = stack.Pop();
            yield return hub;
            var collection = hub.ServiceProvider.GetService<HostedHubsCollection>();
            if (collection is null || !seen.Add(collection))
                continue;
            foreach (var child in collection.Hubs.ToArray())
                stack.Push(child);
        }
    }
}
