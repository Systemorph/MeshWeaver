#pragma warning disable CS1591

using System;
using System.Linq;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.AI;
using MeshWeaver.Data;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Orleans.TestingHost;
using Xunit;

namespace MeshWeaver.Hosting.Orleans.Test;

/// <summary>
/// DETERMINISTIC 2-silo repro of the multi-replica residual of issue #325 (symptom 2): a mirror on
/// the NON-owner silo, kept alive across the owner grain's idle-recycle, DROPS the owner's
/// post-recycle resubscribe Full and stays orphaned at the pre-recycle version.
///
/// <para><b>The mechanism.</b> Every frame the owner's data-source sync stream emits carries the
/// stream's <c>Hub.Version</c> (<c>ApplyChanges</c>), which RESETS to 0 on every (re)activation. A
/// mirror on another silo caches the high pre-recycle frame version in <c>Current.Version</c>. When
/// the owner grain idle-recycles and takes a post-recycle write, the reactivated owner stamps the
/// write frame with a fresh LOW <c>Hub.Version</c>; the mirror's resubscribe pulls that frame as its
/// initial Full and the receive-side monotonicity guard (<c>SynchronizationStream.UpdateStream</c>,
/// <c>version &lt; Current.Version</c>) DROPS it — the split-brain / "get says not found" residual.</para>
///
/// <para><b>Why cross-silo + an explicit notification.</b> The mirror's resubscribe is triggered by
/// the mesh change feed, which is per-silo (each silo's <c>IMeshChangeFeed</c> is a local Rx
/// Subject); an owner write on silo A never reaches silo B's feed (in prod the PG LISTEN/NOTIFY
/// bridge is the intended transport, deliberately NOT a per-write cross-silo broadcast — that
/// double-fires co-hosted consumers and storms). To isolate the FRAME-VERSION FLOOR as the sole
/// variable under test, the driver delivers the change event to the mirror silo's feed directly —
/// exactly the signal a healthy transport would deliver — so the mirror's existing version-gated
/// resubscribe fires. The ONLY thing that then decides accept-vs-drop is the frame version.</para>
///
/// <para><b>The assertion.</b> With the floor (<c>MeshDataSource.WithOwnerVersionBaseline</c> →
/// <c>SynchronizationStream.OwnerVersionFloor</c>) the post-recycle frame is floored at the OWN
/// node's persisted <c>Version</c> loaded at activation, which dominates the mirror's stale version,
/// so the resubscribe Full is ACCEPTED and the mirror converges. On <c>main</c> (no floor) the low
/// frame is dropped and the convergence wait times out (RED). Every wait is bounded on a real
/// condition — a wedge/orphan surfaces as a deterministic timeout, never a hang.</para>
/// </summary>
public class TwoSiloRecycleConvergenceTest : IClassFixture<TwoSiloCacheUpdateFixture>
{
    private readonly TwoSiloCacheUpdateFixture _fixture;
    private readonly ITestOutputHelper _output;

    public TwoSiloRecycleConvergenceTest(TwoSiloCacheUpdateFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        _output = output;
    }

    private static IMessageHub SiloMeshHub(SiloHandle silo)
    {
        var siloHost = silo.GetType().GetProperty("SiloHost")?.GetValue(silo) as IHost
            ?? throw new InvalidOperationException("Could not access silo host");
        return siloHost.Services.GetRequiredService<IMessageHub>();
    }

    /// <summary>Enumerates the silo's hosted hubs (reflection — same handle used by
    /// <c>SharedOrleansFixture.CleanupSiloHubsWithPrefix</c>) and returns those whose address
    /// starts with <paramref name="prefix"/>.</summary>
    private static IMessageHub[] HostedHubsWithPrefix(IMessageHub meshHub, string prefix)
    {
        var field = meshHub.GetType().GetField("hostedHubs", BindingFlags.Instance | BindingFlags.NonPublic);
        var hosted = field?.GetValue(meshHub) as HostedHubsCollection;
        if (hosted is null) return [];
        return hosted.Hubs
            .Where(h => h.Address.ToString().StartsWith(prefix, StringComparison.Ordinal))
            .ToArray();
    }

    [Fact(Timeout = 180_000)]
    public async Task PostRecycleUpdate_NonOwnerSiloMirror_ConvergesInsteadOfOrphaning()
    {
        var ct = new CancellationTokenSource(TimeSpan.FromSeconds(150)).Token;
        var cluster = _fixture.Cluster;
        Assert.True(cluster.Silos.Count >= 2, "repro needs two silos");
        var siloHubs = cluster.Silos.Select(SiloMeshHub).ToArray();

        var ns = $"acme-{Guid.NewGuid():N}/_Provider";
        var path = $"{ns}/Anthropic";

        // 1. Create the provider node via silo 0's mesh hub — activates the owner grain on its hash silo.
        var node = new MeshNode("Anthropic", ns)
        {
            NodeType = ModelProviderNodeType.NodeType,
            Name = "Anthropic",
            State = MeshNodeState.Active,
            Content = new ModelProviderConfiguration
            {
                Provider = "Anthropic",
                ApiKey = "sk-v0",
                CreatedAt = DateTimeOffset.UtcNow,
            }
        };
        var createResp = await siloHubs[0]
            .Observe(new CreateNodeRequest(node), o => o.WithTarget(siloHubs[0].Address))
            .FirstAsync().ToTask(ct);
        createResp.Message.Success.Should().BeTrue(createResp.Message.Error ?? "");

        // 2. Warm-read from both silos so the owner grain is definitely active, then locate the owner silo.
        foreach (var hub in siloHubs)
            await hub.GetWorkspace().GetMeshNodeStream(path)
                .Where(n => n is not null).FirstAsync().Timeout(30.Seconds()).ToTask(ct);

        var ownerSilo = Enumerable.Range(0, siloHubs.Length)
            .FirstOrDefault(i => HostedHubsWithPrefix(siloHubs[i], path).Length > 0, -1);
        ownerSilo.Should().BeGreaterThanOrEqualTo(0, "the owner per-node hub must be hosted on some silo");
        var mirrorSilo = 1 - ownerSilo;
        _output.WriteLine($"[setup] owner grain on silo {ownerSilo}; mirror opened on silo {mirrorSilo}");

        var mirrorHub = siloHubs[mirrorSilo];
        var mirrorWorkspace = mirrorHub.GetWorkspace();

        // 3. Pin a LIVE mirror on the non-owner silo — keeps the cache upstream sync stream alive
        //    across the owner's recycle (the orphaned-mirror precondition).
        var pin = new CompositeDisposable(
            mirrorWorkspace.GetMeshNodeStream(path).Subscribe(_ => { }, _ => { }));
        try
        {
            IObservable<MeshNode> MirrorWhere(Func<ModelProviderConfiguration, bool> pred)
                => mirrorWorkspace.GetMeshNodeStream(path)
                    .Where(n => n is not null
                        && n.Content is ModelProviderConfiguration cfg && pred(cfg))
                    .Select(n => n!);

            // 4. Several writes so the mirror's cached Current.Version (the owner's data-source frame
            //    clock) climbs well ABOVE the low value a freshly reactivated owner will stamp.
            const int preRecycleWrites = 6;
            for (var k = 1; k <= preRecycleWrites; k++)
            {
                var key = $"sk-v{k}";
                await UpdateApiKey(mirrorHub, path, key, ct);
                await MirrorWhere(c => c.ApiKey == key).FirstAsync().Timeout(30.Seconds()).ToTask(ct);
            }
            var preRecycleNode = await MirrorWhere(c => c.ApiKey == $"sk-v{preRecycleWrites}")
                .FirstAsync().Timeout(30.Seconds()).ToTask(ct);
            var preRecycleVersion = preRecycleNode.Version;
            _output.WriteLine($"[pre-recycle] mirror at ApiKey=sk-v{preRecycleWrites}, node.Version={preRecycleVersion}");

            // Persistence is debounced (~200 ms Sample); wait until the store carries the pre-recycle
            // version so the reactivated owner loads it verbatim as its per-activation baseline.
            await WaitForPersistedVersion(path, preRecycleVersion, ct);

            // 5. Force the owner grain to idle-recycle: dispose its hosted hubs (→ DeactivateOnIdle).
            //    Only the owner silo — leaves the mirror silo's cache upstream untouched.
            var disposed = 0;
            foreach (var hub in HostedHubsWithPrefix(siloHubs[ownerSilo], path))
            {
                try { hub.Dispose(); disposed++; } catch { /* best-effort */ }
            }
            _output.WriteLine($"[recycle] disposed {disposed} owner hosted hub(s) on silo {ownerSilo}");

            // 6. Post-recycle write — reactivates the owner (fresh Hub.Version) and stamps the write
            //    frame. Resilient: the first write can race the grain's dispose→DeactivateOnIdle
            //    window ("Hub is shutting down"); retry on the reactive tick until it lands on the
            //    freshly reactivated grain.
            await UpdateApiKeyResilient(mirrorHub, path, "sk-post-recycle", ct);

            // Capture the authoritative post-recycle node (its persisted Version drives the change event).
            var postNode = await WaitForPersistedBeyond(path, preRecycleVersion, ct);
            _output.WriteLine($"[post-recycle] persisted node.Version={postNode.Version}");

            // 7. Deliver the cross-silo change notification to the mirror silo's feed (the signal a
            //    healthy transport would deliver), repeatedly, so the mirror's version-gated
            //    resubscribe fires. Reactive interval — no Task.Delay. Self-limiting: once the mirror
            //    catches up, receivedVersion == announcedVersion closes the gate.
            var mirrorFeed = mirrorHub.ServiceProvider.GetRequiredService<IMeshChangeFeed>();
            var notify = Observable.Interval(TimeSpan.FromSeconds(2)).StartWith(0L)
                .Subscribe(_ =>
                {
                    try { mirrorFeed.Publish(MeshChangeEvent.Updated(postNode)); } catch { /* best-effort */ }
                });
            pin.Add(notify);

            // 8. Convergence: the pinned mirror (same cached upstream) must emit the post-recycle value.
            //    On main the low resubscribe Full is dropped → this times out (RED). With the floor the
            //    floored Full is accepted → the mirror converges (GREEN).
            var converged = await MirrorWhere(c => c.ApiKey == "sk-post-recycle")
                .FirstAsync().Timeout(60.Seconds()).ToTask(ct);

            converged.Content.Should().BeOfType<ModelProviderConfiguration>()
                .Which.ApiKey.Should().Be("sk-post-recycle",
                    "the non-owner-silo mirror must converge on the post-recycle value — the floored "
                    + "resubscribe Full must not be dropped by the monotonicity guard (issue #325 symptom 2)");
        }
        finally
        {
            pin.Dispose();
        }
    }

    private static IObservable<MeshNode> UpdateApiKey(IMessageHub hub, string path, string apiKey)
    {
        var cache = hub.ServiceProvider.GetRequiredService<IMeshNodeStreamCache>();
        return cache.Update(path, n => n with
        {
            Content = (n.Content as ModelProviderConfiguration
                       ?? new ModelProviderConfiguration { Provider = "Anthropic" })
                with { ApiKey = apiKey }
        }, hub.JsonSerializerOptions)
        .Take(1);
    }

    private static Task UpdateApiKey(IMessageHub hub, string path, string apiKey, CancellationToken ct)
        => UpdateApiKey(hub, path, apiKey).Timeout(30.Seconds()).ToTask(ct);

    /// <summary>Retries the write on the reactive tick until it lands — the post-recycle write can
    /// race the owner grain's dispose→<c>DeactivateOnIdle</c> shutdown window ("Hub is shutting
    /// down") before Orleans reactivates a fresh activation. Bounded; no <c>Task.Delay</c>.</summary>
    private static Task UpdateApiKeyResilient(IMessageHub hub, string path, string apiKey, CancellationToken ct)
        => Observable.Interval(TimeSpan.FromMilliseconds(500)).StartWith(0L)
            .SelectMany(_ => UpdateApiKey(hub, path, apiKey)
                .Select(_ => true)
                .Catch<bool, Exception>(_ => Observable.Return(false)))
            .Where(ok => ok)
            .FirstAsync().Timeout(60.Seconds()).ToTask(ct);

    /// <summary>Polls the shared in-memory store until the node at <paramref name="path"/> carries at
    /// least <paramref name="minVersion"/>.</summary>
    private static async Task WaitForPersistedVersion(string path, long minVersion, CancellationToken ct)
        => await Observable.Interval(TimeSpan.FromMilliseconds(50)).StartWith(0L)
            .Where(_ => TwoSiloCacheUpdateFixture.SharedNodes.TryGetValue(path, out var n) && n.Version >= minVersion)
            .FirstAsync().Timeout(30.Seconds()).ToTask(ct);

    /// <summary>Polls the shared in-memory store until the node's version has advanced beyond
    /// <paramref name="beyondVersion"/> (the post-recycle write persisted) and returns it.</summary>
    private static async Task<MeshNode> WaitForPersistedBeyond(string path, long beyondVersion, CancellationToken ct)
        => await Observable.Interval(TimeSpan.FromMilliseconds(50)).StartWith(0L)
            .Select(_ => TwoSiloCacheUpdateFixture.SharedNodes.TryGetValue(path, out var n) ? n : null)
            .Where(n => n is not null && n.Version > beyondVersion)
            .Select(n => n!)
            .FirstAsync().Timeout(30.Seconds()).ToTask(ct);
}
