using System;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using Xunit;

namespace MeshWeaver.Hosting.Monolith.Test;

/// <summary>
/// 🚨 <b>The core claim of #2202: whoever confirms a recycle must SURVIVE it.</b>
///
/// <para>Recycle used to be a confirmation layout area hosted on the very hub it tears down. Its
/// confirm button pushed a <c>RedirectControl</c> into the area stream and then posted
/// <see cref="DisposeRequest"/> to that same hub, so the redirect had to outrun a teardown of the
/// stream carrying it. In-process queue order is not wire order: the dispose reached the hub before
/// the area update flushed, the hub recycled correctly, and the user saw a dead button
/// (memex, 2026-08-25, <c>OpenStreetMap/Recycle</c>). Cancel — a client-side
/// <c>NavigateToHref</c> — worked, which is what isolated the defect to the stream-delivered
/// redirect racing its own dispose. Re-ordering the two posts (2026-07) did not help: the race is
/// structural, because a dying hub cannot be relied on to deliver its own last frame.</para>
///
/// <para>So the contract is not "the redirect wins the race" — it is <b>that there is no race</b>:
/// the flow runs on a hub that outlives the target. <c>HubRecycleExtensions.RecycleNode</c> is that
/// flow, and the portal shell (<c>PortalLayoutBase</c>) calls it with the CIRCUIT's hub. This test
/// stands in for the circuit with a client hub and asserts all three halves in one run: the target
/// really was torn down, the caller was still alive to see it, and the caller got an answer served
/// by a FRESH activation of that address — which is the moment the page may safely redirect.</para>
/// </summary>
public class RecycleSurvivesItsOwnDisposeTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string NodeId = "recycle-survivor";

    [Fact(Timeout = 120_000)]
    public async Task TheHubThatConfirmsTheRecycle_OutlivesTheHubItRecycles()
    {
        var ct = TestContext.Current.CancellationToken;
        var path = $"{TestPartition}/{NodeId}";
        var address = new Address(path);

        await NodeFactory
            .CreateNode(new MeshNode(NodeId, TestPartition) { Name = "Original", NodeType = "Markdown" })
            .Should().Within(30.Seconds()).Emit();

        // The SURVIVING hub — the test's stand-in for the portal circuit. It is emphatically NOT
        // the hub at `path`; that separation is the whole fix.
        var caller = GetClient(c => c.AddData());
        caller.Address.ToString().Should().NotBe(address.ToString(),
            "the recycle must be issued from a hub that is not the one being recycled");

        // Activate the target so there is a live instance to tear down (otherwise the test would
        // pass against a recycle that never happened).
        await caller.GetWorkspace().GetMeshNodeStream(path)
            .Should().Within(30.Seconds()).Match(n => n is { Name: "Original" });
        var target = Mesh.GetHostedHub(address, HostedHubCreation.Never);
        target.Should().NotBeNull("the read must have activated the target hub");
        var targetInstance = target!;

        // Act — the page-level flow, exactly as PortalLayoutBase runs it.
        var recycled = await caller.RecycleNode(path)
            .FirstAsync()
            .Timeout(TimeSpan.FromSeconds(60))
            .ToTask(ct);

        // 1. The recycle REALLY happened: that instance is terminal, not merely quiet.
        //    DisposalCompleted, not RunLevelChanged: the latter is a BehaviorSubject that COMPLETES
        //    at Dead, so a subscriber arriving after the teardown gets OnCompleted with no value.
        //    DisposalCompleted is a ReplaySubject(1) and MessageHub sets Dead BEFORE signalling it,
        //    so this is deterministic however the two race.
        await targetInstance.DisposalCompleted
            .FirstAsync()
            .Timeout(TimeSpan.FromSeconds(60))
            .ToTask(ct);
        targetInstance.RunLevel.Should().Be(MessageHubRunLevel.Dead,
            "the hub the recycle targeted must actually be torn down");

        // 2. The caller — the thing that showed the confirmation — is untouched by the teardown it
        //    ordered. Pre-fix this was the confirmation's own host, and it died mid-flush.
        caller.RunLevel.Should().Be(MessageHubRunLevel.Started,
            "the hub that confirms a recycle must not be torn down by it");

        // 3. And it holds the answer it needs before sending the user anywhere: the node, served by
        //    a re-activated address. This is what replaces "push a redirect and hope".
        recycled.Should().NotBeNull("the recycled address must answer again within the budget");
        recycled!.Name.Should().Be("Original", "a recycle changes no content");
        Mesh.GetHostedHub(address, HostedHubCreation.Never)
            .Should().NotBeSameAs(targetInstance,
                "the answer must come from a FRESH activation, not the corpse of the recycled hub");
    }
}
