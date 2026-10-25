using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Reactive.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Fixture;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Orleans.TestingHost;
using Xunit;

namespace MeshWeaver.Hosting.Orleans.Test;

/// <summary>
/// Issue #6623: the per-node hubs a boot pass activates must end when the pass's reads end — not
/// linger for the length of Orleans' idle collection after the last reader has gone.
///
/// <para><b>The shape.</b> A boot step (the bake sweep, an install pass, a GitSync pass) reads
/// many nodes as SYSTEM through the process's mesh-node cache:
/// <c>GetMeshNodeStream(path).Take(1)</c>. Each read builds the node's hub (its grain activation)
/// and leaves a cache mirror subscribed to it. The mirror heart-beats the owner every 45 s and is
/// released by the cache's idle sweep after ten subscriber-free minutes; after that, nothing
/// ended the activation except Orleans' own idle collection, another fifteen minutes on. So every
/// node a boot pass touched stayed resident for about 25 minutes after the pass, all of them at
/// once on the first replica of a generation — the ~1,000 node hubs at ~5 MB each the issue
/// measured.</para>
///
/// <para><b>The rule under test.</b> An activation that has served nobody but the platform
/// (System, hub and heartbeat traffic) has no reason to outlive its last subscriber: when the last
/// server-side stream it serves ends, it asks Orleans to deactivate it. An activation a PERSON has
/// used keeps today's idle collection, so a reader navigating away and back does not rebuild the
/// hub each time.</para>
///
/// <para>The "pass ends" step is <see cref="IMeshNodeStreamCache.ReleaseIfUnwatched"/> on the
/// silo's cache — the event the cache's idle sweep also ends in, minus its ten-minute wait — so
/// the test does not sit out the window.</para>
/// </summary>
public class AnInfrastructureActivationEndsWithItsLastSubscriberTest(ITestOutputHelper output)
    : OrleansMeshTestBase(output)
{
    private const int PassSize = 4;

    private IMessageHub SiloMesh =>
        ((InProcessSiloHandle)Cluster.Silos[0]).SiloHost.Services.GetRequiredService<IMessageHub>();

    private IMessageHub GetClient([CallerMemberName] string? name = null)
        => base.GetClient($"retire-{name}-{Guid.NewGuid():N}"[..40], "TestUser");

    [Fact(Timeout = 120_000)]
    public async Task ABootPassActivation_EndsWhenThePassReleasesItsReads()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = GetClient();
        var prefix = $"TestUser/boot6623-{Guid.NewGuid():N}";
        var paths = await CreateNodes(client, prefix, PassSize);
        await NoHubLive(paths, "precondition: the pass starts from cold addresses");

        // THE PASS: one-shot SYSTEM reads through the silo's mesh-node cache, exactly as a boot
        // sweep reads content.
        var access = SiloMesh.ServiceProvider.GetRequiredService<AccessService>();
        var workspace = SiloMesh.GetWorkspace();
        foreach (var path in paths)
            await access.RunAsSystem(() => workspace.GetMeshNodeStream(path).Where(n => n is not null).Take(1))
                .Should().Within(TestTimeouts.Convergence).Emit($"the pass reads {path}", ct);

        var hubs = paths.Select(LiveHub).ToImmutableList();
        hubs.Should().AllSatisfy(h => h.Should().NotBeNull(
            "the pass's read activates the node's hub — the cost this test is about"));
        Output.WriteLine($"[pass] {hubs.Count} node hubs live after the pass");

        // THE PASS ENDS: its mirrors go (the cache's idle sweep, without its ten-minute wait).
        var cache = SiloServices().GetRequiredService<IMeshNodeStreamCache>();
        foreach (var path in paths)
            await ReleaseWhenUnwatched(cache, path);

        foreach (var hub in hubs)
            await hub!.DisposalCompleted.Take(1)
                .Should().Within(TestTimeouts.Convergence).Emit(
                    $"{hub.Address} was activated only by the pass's System read; once that read's "
                    + "mirror has gone it serves nobody, so the activation must end now — not after "
                    + "Orleans' idle collection, which kept every boot-touched hub resident for ~25 "
                    + "minutes on the first replica of a generation (#6623)", ct);
    }

    [Fact(Timeout = 120_000)]
    public async Task AnActivationAPersonUsed_KeepsIdleCollection()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = GetClient();
        var prefix = $"TestUser/person6623-{Guid.NewGuid():N}";
        var path = (await CreateNodes(client, prefix, 1)).Single();
        await NoHubLive([path], "precondition: a cold address");

        // A PERSON opens a view of it from ANOTHER process, under the user's identity: the subscribe
        // itself carries the person through the grain's delivery seam.
        //
        // 🚨 Not through that process's mesh-node cache. The cache subscribes as ITSELF
        // (MeshNodeCacheIdentity), by design, so a cache read never marks the owner as person-used —
        // the cache's own idle window is the reuse for that reader (Doc/Architecture/SyncHubPopulation
        // §10.2). This fact used to read through the client's cache and passed only when the cache's
        // later teardown happened to be stamped with this single-identity test host's standing
        // identity before the owner's last lease dropped; a teardown stamped any other way retired
        // the hub and turned main red (#6623 follow-up).
        var access = ClientServices.GetRequiredService<AccessService>();
        var person = new AccessContext { ObjectId = "TestUser", Name = "TestUser" };
        ISynchronizationStream<System.Text.Json.JsonElement> stream;
        using (access.SwitchAccessContext(person))
            stream = client.GetWorkspace().GetRemoteStream<System.Text.Json.JsonElement, LayoutAreaReference>(
                new Address(path), new LayoutAreaReference("Overview"));
        await stream.Take(1)
            .Should().Within(TestTimeouts.Convergence).Emit($"TestUser's view of {path} renders", ct);
        var hub = LiveHub(path);
        hub.Should().NotBeNull("the person's subscribe activated the hub");

        // Every stream ends: the person's view, and the silo's own activation mirror.
        stream.Dispose();
        await ReleaseWhenUnwatched(SiloServices().GetRequiredService<IMeshNodeStreamCache>(), path);

        await hub!.DisposalCompleted.Take(1).Should().NotEmit(TimeSpan.FromSeconds(5),
            "an activation a person has used keeps Orleans' idle collection: retiring it the moment "
            + "its last stream ends would rebuild the hub on every navigate-away-and-back", ct);
    }

    [Fact(Timeout = 120_000)]
    public async Task ALastSubscriberLeavingDuringAHold_RetiresWhenTheHoldEnds()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = GetClient();
        var path = (await CreateNodes(client, $"TestUser/hold6623-{Guid.NewGuid():N}", 1)).Single();
        await NoHubLive([path], "precondition: a cold address");

        var access = SiloMesh.ServiceProvider.GetRequiredService<AccessService>();
        await access.RunAsSystem(() => SiloMesh.GetWorkspace().GetMeshNodeStream(path).Where(n => n is not null).Take(1))
            .Should().Within(TestTimeouts.Convergence).Emit($"the pass reads {path}", ct);
        var hub = LiveHub(path);
        hub.Should().NotBeNull("the pass's read activated the hub");

        // A long-running operation holds the activation while its last subscriber goes.
        var hold = hub!.Configuration.Get<GrainLongRunningOperationCallback>()!.BeginOperation();
        try
        {
            await ReleaseWhenUnwatched(SiloServices().GetRequiredService<IMeshNodeStreamCache>(), path);
            await hub.DisposalCompleted.Take(1).Should().NotEmit(TimeSpan.FromSeconds(2),
                "a held activation is not retired under its operation", ct);
        }
        finally
        {
            hold.Dispose();
        }

        await hub.DisposalCompleted.Take(1).Should().Within(TestTimeouts.Convergence).Emit(
            "the last subscriber left while the hold was up; that decision is kept pending and taken "
            + "when the hold ends — otherwise the activation waits out idle collection after all", ct);
    }

    [Fact(Timeout = 120_000)]
    public async Task ASameSiloPersonRead_KeepsIdleCollection()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = GetClient();
        var path = (await CreateNodes(client, $"TestUser/silo6623-{Guid.NewGuid():N}", 1)).Single();
        await NoHubLive([path], "precondition: a cold address");

        // A PERSON subscribes from the SILO's own process — the shape of a Blazor view's stream on a
        // portal co-hosted with its silo. The subscribe reaches the hosted hub through the mesh hub's
        // hosted-hub routing and never passes the grain's DeliverMessage.
        var access = SiloMesh.ServiceProvider.GetRequiredService<AccessService>();
        var person = new AccessContext { ObjectId = "TestUser", Name = "TestUser" };
        ISynchronizationStream<System.Text.Json.JsonElement> stream;
        using (access.SwitchAccessContext(person))
            stream = SiloMesh.GetWorkspace().GetRemoteStream<System.Text.Json.JsonElement, LayoutAreaReference>(
                new Address(path), new LayoutAreaReference("Overview"));
        await stream.Take(1)
            .Should().Within(TestTimeouts.Convergence).Emit($"TestUser's view of {path} on the silo renders", ct);
        var hub = LiveHub(path);
        hub.Should().NotBeNull("the person's subscribe activated the hub");

        // Every stream ends: the person's, and the silo's own activation mirror.
        stream.Dispose();
        await ReleaseWhenUnwatched(SiloServices().GetRequiredService<IMeshNodeStreamCache>(), path);

        await hub!.DisposalCompleted.Take(1).Should().NotEmit(TimeSpan.FromSeconds(5),
            "a person reached this activation on the silo itself; it keeps Orleans' idle collection", ct);
    }

    private async Task<ImmutableList<string>> CreateNodes(IMessageHub client, string prefix, int count)
    {
        var ct = TestContext.Current.CancellationToken;
        var parent = new Address(prefix.Split('/')[0]);
        var paths = ImmutableList<string>.Empty;
        for (var i = 0; i < count; i++)
        {
            var id = $"{prefix.Split('/')[1]}-n{i}";
            var created = await client.Observe(
                    new CreateNodeRequest(new MeshNode(id, "TestUser") { Name = $"Boot read {i}", NodeType = "Markdown" }),
                    o => o.WithTarget(parent))
                .FirstAsync().Timeout(TestTimeouts.Convergence).Await(ct);
            created.Message.Success.Should().BeTrue(created.Message.Error ?? "");
            paths = paths.Add(created.Message.Node!.Path!);
        }

        // Creation may itself have activated the new nodes' hubs; end those activations so the
        // pass below is what activates them.
        foreach (var path in paths)
            Fixture.CleanupSiloHubsWithPrefix(path);
        return paths;
    }

    private IMessageHub? LiveHub(string path) =>
        SiloMesh.GetHostedHub(new Address(path), HostedHubCreation.Never);

    private Task NoHubLive(IReadOnlyCollection<string> paths, string because) =>
        Observable.Interval(TimeSpan.FromMilliseconds(50)).StartWith(0L)
            .Select(_ => paths.Where(p => LiveHub(p) is not null).ToImmutableList())
            .Where(live => live.IsEmpty)
            .Take(1)
            .Should().Within(TestTimeouts.Convergence).Emit(because, TestContext.Current.CancellationToken);

    private static Task ReleaseWhenUnwatched(IMeshNodeStreamCache cache, string path) =>
        Observable.Interval(TimeSpan.FromMilliseconds(50)).StartWith(0L)
            .Select(_ => cache.ReleaseIfUnwatched(path))
            .Where(released => released)
            .Take(1)
            .Should().Within(TestTimeouts.Convergence).Emit(
                $"the mirror of {path} has no reader left once the pass's Take(1) completed",
                TestContext.Current.CancellationToken);
}
