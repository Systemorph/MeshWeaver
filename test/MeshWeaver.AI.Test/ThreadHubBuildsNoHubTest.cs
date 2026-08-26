using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using MeshWeaver.Graph;
using MeshWeaver.ShortGuid;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.AI.Test;

/// <summary>
/// 🚨 <b>#1868 — <c>Build</c> must not construct another hub.</b> This is the AI half of the
/// adoption, and the last framework site that still violated it.
///
/// <para><b>The fact.</b> <c>MessageHubConfiguration.Build</c> runs <c>SyncBuildupActions</c> inline,
/// before <c>StartMessageProcessing()</c>. <c>ThreadExecution.AddThreadExecution</c> registered
/// <c>InstallExecutionHub</c> — which calls <c>GetHostedHub(…/_Exec,
/// HostedHubCreation.Always)</c> — on the SYNCHRONOUS overload, so EVERY thread hub in the product
/// built a child hub, and a second Autofac container, from inside its own <c>Build</c>. #2045
/// adopted the workspace and kernel sites (<c>DataExtensions</c>, <c>KernelContainer</c>) and its
/// pin, <c>MeshWeaver.Data.Test.BuildMustNotConstructHubsTest</c>, covers a data-enabled hub — it
/// cannot see this one, which needs the AI stack.</para>
///
/// <para><b>Why it matters</b> — and explicitly NOT as a crash claim, which #1867 withdrew: <i>a
/// disposal that races a construction races a TREE of constructions, not one frame.</i> That is the
/// shape behind the whole shutdown-race family (#645, #715, #967, #1573), each of which had to widen
/// its guard to cover work started by a construction that had itself been started by a construction.
/// <c>HostedHubsCollection</c>'s in-flight counter tracks the OUTER creation; the inner one it spawns
/// is a second entry, on the same thread, whose refusal/finish semantics are only correct because the
/// guards were extended by hand, one incident at a time. Threads are the hottest per-node hub in the
/// product, so this site was paying that tax on every round.</para>
///
/// <para><b>What the fix does NOT change.</b> <c>_Exec</c> is still created EAGERLY, and still before
/// the submission watcher — it just happens on the <c>InitializeHubRequest</c> turn instead of inside
/// <c>Build</c>. <c>BuildupActions</c> are <c>Concat</c>-ed in registration order and the Initialize
/// gate opens only after they complete, so no message can overtake them; the eager-creation
/// requirement documented on <c>InstallExecutionHub</c> ("if _Exec isn't running yet, the
/// StartingExecution emission has no subscriber and the round stalls") still holds. The rest of the
/// class's thread tests are what prove that end to end.</para>
/// </summary>
public class ThreadHubBuildsNoHubTest(ITestOutputHelper output) : AITestBase(output)
{
    /// <summary>
    /// Activating a thread hub must construct no hub of its own.
    ///
    /// <para><b>Non-vacuity is asserted, not assumed.</b> The test first requires that the thread hub
    /// really did come up AND that its <c>_Exec</c> child really was created — otherwise "constructed
    /// nothing during Build" would also be satisfied by a hub that constructed nothing at all. On
    /// <c>origin/main</c> that same <c>_Exec</c> address is what the violation list names, and this
    /// fails with it in the message.</para>
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task ActivatingAThreadHub_BuildsNoHubOfItsOwn()
    {
        var threadId = Guid.NewGuid().AsString();
        var threadPath = $"{MonolithMeshTestBase.TestPartition}/{ThreadNodeType.ThreadPartition}/{threadId}";
        await NodeFactory.CreateNode(MeshNode.FromPath(threadPath) with
        {
            Name = $"Nesting probe {threadId}",
            NodeType = ThreadNodeType.NodeType,
            MainNode = MonolithMeshTestBase.TestPartition,
            Content = new Thread { CreatedBy = "rbuergi@systemorph.com" }
        }).Should().Emit();

        // Reading the node through its stream is what routes a message to the per-node hub and so
        // activates it — the same path the GUI takes.
        (await ReadNode(threadPath).Should().Emit()).Should().NotBeNull();

        var hubs = Walk(Mesh).ToArray();
        foreach (var h in hubs.Where(h => h.Address.ToString().StartsWith(threadPath, StringComparison.Ordinal)))
            Output.WriteLine($"hub: {h.Address}");

        var threadHub = hubs.SingleOrDefault(h =>
            string.Equals(h.Address.ToString(), threadPath, StringComparison.Ordinal));
        threadHub.Should().NotBeNull(
            $"the thread hub {threadPath} must have been activated, or this test proves nothing");

        hubs.Select(h => h.Address.ToString())
            .Should().Contain($"{threadPath}/_Exec",
                "InstallExecutionHub must still create _Exec EAGERLY — moving it to the observable "
                + "WithInitialization overload changes WHEN, never WHETHER");

        var violations = hubs
            .Where(h => h.Address.ToString().StartsWith(threadPath, StringComparison.Ordinal))
            .OfType<MessageHub>()
            .SelectMany(h => h.HubsConstructedDuringBuild.Select(a => $"{h.Address} → {a}"))
            .ToArray();

        violations.Should().BeEmpty(
            "a SyncBuildupAction reached hub construction: Build runs those inline, before "
            + "StartMessageProcessing, so a disposal racing this hub's creation races a TREE of "
            + "constructions rather than one frame (#1868). The initialization belongs on the "
            + "OBSERVABLE WithInitialization overload, which runs on InitializeHubRequest after "
            + "Build has returned. Constructed during Build: " + string.Join(", ", violations));
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
