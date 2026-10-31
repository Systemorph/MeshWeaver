using System;
using System.Collections.Immutable;
using System.Linq;
using System.Reactive.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using MeshWeaver.Graph;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.PluginCatalog;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace MeshWeaver.Hosting.Test;

/// <summary>
/// 🚨 <b>The registry's per-request authentication opens no mesh read once it has answered once —
/// however long the consumer waits between polls (MeshWeaver#7354).</b>
///
/// <para>Every registry request an installation makes (<c>GET /api/plugins/bundles/index.json</c>
/// first among them) passes <see cref="InstanceRegistryAuthenticator"/>, which reads the index,
/// instance and grant nodes and then the plan ladder. #3119 moved the three node legs onto the
/// process-wide cache so that "only the first request for an instance waits on an owner" — but the
/// cache idle-releases a read entry with no live subscriber after ten minutes, every leg is a
/// <c>Take(1)</c>, and a consumer's registry safety net polls every thirty. So every poll found the
/// instance and grant mirrors released and paid a fresh owner subscription, bounded by the
/// ten-second budget the consumer saw elapse as a 503 (10.09 s, 2026-10-30). And the plan ladder
/// re-ran a full mesh query whenever its one-minute snapshot had expired — i.e. on every poll.</para>
///
/// <para>The idle window is shortened to the #6048 settings (500 ms window, 100 ms sweep) so "a
/// consumer that waits longer than the window" is a few hundred milliseconds here. The cache's own
/// eviction feed is the evidence: a mirror released between two resolutions IS a fresh owner read
/// on the next one.</para>
/// </summary>
public class RegistryAuthenticationOpensNoReadPerRequestTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private static readonly TimeSpan IdleWindow = TimeSpan.FromMilliseconds(500);

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder)
            .AddPluginCatalog()
            .ConfigureServices(services => services.AddSingleton(new MeshNodeStreamCacheOptions
            {
                ReadStreamIdleExpiration = IdleWindow,
                ReadStreamSweepInterval = TimeSpan.FromMilliseconds(100),
            }));

    private MeshNodeStreamCache Cache =>
        (MeshNodeStreamCache)Mesh.ServiceProvider.GetRequiredService<IMeshNodeStreamCache>();

    private AccessService Access => Mesh.ServiceProvider.GetRequiredService<AccessService>();

    private InstanceRegistryAuthenticator NewAuthenticator() => new(
        Mesh, Mesh.ServiceProvider.GetRequiredService<ILogger<InstanceRegistryAuthenticator>>());

    /// <summary>The three node legs of one registered instance, seeded as the registration writes
    /// them: index, instance (in its owner's partition), grant (in the Admin partition).</summary>
    private sealed record Registered(string RawKey, string IndexPath, string InstancePath, string GrantPath);

    private async Task<Registered> Register(string instanceId)
    {
        var ct = TestContext.Current.CancellationToken;
        var rawKey = InstanceKeys.Generate();
        var hash = InstanceKeys.Hash(rawKey);
        var instancePath = $"{TestPartition}/{MeshWeaverInstanceNodeType.NodeType}/{instanceId}";
        var indexPath = $"{MeshWeaverInstanceNodeType.IndexNamespace}/{InstanceKeys.HashPrefix(hash)}";
        var grantPath = MeshWeaverInstanceNodeType.GrantPath(instanceId);

        await Create(MeshNode.FromPath(instancePath) with
        {
            NodeType = MeshWeaverInstanceNodeType.NodeType,
            Content = new MeshWeaverInstance { InstanceId = instanceId, KeyHash = hash },
        });
        await Create(MeshNode.FromPath(indexPath) with
        {
            NodeType = MeshWeaverInstanceNodeType.NodeType,
            Content = new MeshWeaverInstanceIndex { KeyHash = hash, InstancePath = instancePath, InstanceId = instanceId },
        });
        await Create(MeshNode.FromPath(grantPath) with
        {
            NodeType = MeshWeaverInstanceNodeType.GrantNodeType,
            Content = new PluginGrant { InstanceId = instanceId },
        });

        foreach (var path in new[] { indexPath, instancePath, grantPath })
            await AwaitListed(path);
        return new Registered(rawKey, indexPath, instancePath, grantPath);

        Task Create(MeshNode node) =>
            Access.RunAsSystem(() => NodeFactory.CreateNode(node))
                .Should().Within(TestTimeouts.Convergence)
                .Emit($"seeding {node.Path}", cancellationToken: ct);
    }

    /// <summary>The index trails the store; the authenticator gates each leg on the very listing this
    /// waits on (same id, same query set), so once it shows the path the resolution will too.</summary>
    private Task AwaitListed(string path)
    {
        var parent = path[..path.LastIndexOf('/')];
        return Access.RunAsSystem(() => Mesh.GetQuery(
                $"instance-key-listing:{parent}", $"path:{parent} scope:children select:path"))
            .Where(nodes => nodes.Any(n => string.Equals(n.Path, path, StringComparison.Ordinal)))
            .Should().Within(TestTimeouts.Convergence)
            .Emit($"the listing of {parent} must catch up with {path}",
                cancellationToken: TestContext.Current.CancellationToken);
    }

    private static Task<InstanceAuthResult> Resolve(InstanceRegistryAuthenticator authenticator, string rawKey) =>
        authenticator.AuthenticateOutcome(InstanceKeys.AuthorizationHeader(rawKey))
            .Should().Within(TestTimeouts.Convergence)
            .Emit("a resolution always reaches one of its outcomes",
                cancellationToken: TestContext.Current.CancellationToken);

    /// <summary>
    /// Proves the idle sweep has run over everything read before this call: a throwaway node is read
    /// once and released, then a second one — two releases in sequence mean at least one full sweep
    /// pass started after every earlier read's window had elapsed. A positive signal, never a sleep.
    /// </summary>
    private async Task AwaitAFullIdleSweep(string tag)
    {
        var ct = TestContext.Current.CancellationToken;
        foreach (var round in new[] { 1, 2 })
        {
            var path = $"{TestPartition}/idle-probe-{tag}-{round}-{Guid.NewGuid():N}";
            await Access.RunAsSystem(() => NodeFactory.CreateNode(MeshNode.FromPath(path) with { NodeType = "Markdown" }))
                .Should().Within(TestTimeouts.Convergence).Emit($"seeding the idle probe {path}", cancellationToken: ct);
            var released = Cache.ReadStreamEvictions.Where(e => e.Path == path).Replay(1);
            using var connected = released.Connect();
            await Access.RunAsSystem(() => (IObservable<MeshNode>)Mesh.GetMeshNodeStream(path))
                .Where(n => n is not null).Take(1)
                .Should().Within(TestTimeouts.Convergence).Emit("the probe is read once", cancellationToken: ct);
            await released.Should().Within(TestTimeouts.Convergence)
                .Emit($"the idle sweep releases the once-read probe {path} after its {IdleWindow.TotalMilliseconds} ms window",
                    cancellationToken: ct);
        }
    }

    /// <summary>
    /// SHOULD-FAIL-IF: the authenticator reads its mirrors with nothing holding them. Then the idle
    /// sweep releases the instance and grant mirrors between two resolutions, and the second one —
    /// like every consumer poll in production — opens a fresh owner subscription.
    /// </summary>
    [Fact]
    public async Task ResolutionsFurtherApartThanTheIdleWindow_ReadTheMirrorsFromMemory()
    {
        var registered = await Register("idle-held");
        using var authenticator = NewAuthenticator();
        var legs = ImmutableHashSet.Create(registered.IndexPath, registered.InstancePath, registered.GrantPath);

        var first = await Resolve(authenticator, registered.RawKey);
        first.IsUnavailable.Should().BeFalse(first.UnavailableReason ?? "the first resolution reaches a verdict");
        first.Instance.Should().NotBeNull("a registered key authenticates");

        var released = ImmutableList<string>.Empty;
        using var evictions = Cache.ReadStreamEvictions
            .Where(e => legs.Contains(e.Path))
            .Subscribe(e => ImmutableInterlocked.Update(ref released, list => list.Add(e.Path)));

        // The consumer waits longer than the idle window before it polls again.
        await AwaitAFullIdleSweep("between-polls");

        released.Should().BeEmpty(
            "a mirror the idle sweep releases between two polls is a fresh owner read on the next poll — "
            + "the per-request round-trip #3119 removed, back by way of the sweep (MeshWeaver#7354)");
        foreach (var leg in legs)
            Cache.IsReadStreamLive(leg).Should().BeTrue($"the mirror of {leg} is held live across the idle window");

        // Ten concurrent polls, all from memory: no leg is released or re-opened by them.
        var outcomes = await Enumerable.Range(0, 10)
            .Select(_ => authenticator.AuthenticateOutcome(InstanceKeys.AuthorizationHeader(registered.RawKey)).Take(1))
            .Merge()
            .ToList()
            .Should().Within(TestTimeouts.Convergence)
            .Emit("ten concurrent resolutions all answer", cancellationToken: TestContext.Current.CancellationToken);
        outcomes.Should().HaveCount(10);
        outcomes.Should().OnlyContain(o => !o.IsUnavailable && o.Instance != null);
        released.Should().BeEmpty("concurrent polls read the held mirrors and open no owner read of their own");
    }

    /// <summary>
    /// NEGATIVE CONTROL — holding the mirrors must not freeze them: an ENTITLEMENT change (a grant
    /// entry added, then the instance disabled) written through the node stream after an idle window
    /// is what the very next resolution decides with.
    /// </summary>
    [Fact]
    public async Task AnEntitlementChange_AfterTheIdleWindow_IsWhatTheNextResolutionDecidesWith()
    {
        var ct = TestContext.Current.CancellationToken;
        var registered = await Register("idle-entitled");
        using var authenticator = NewAuthenticator();

        var before = await Resolve(authenticator, registered.RawKey);
        before.Instance.Should().NotBeNull("the control: the key resolves");
        before.Instance!.Allows("Plugins", "Edu").Should().BeFalse("the grant starts empty");

        await AwaitAFullIdleSweep("before-grant");

        var entry = new PluginGrantEntry { Source = "Plugins", PackageId = PluginGrantEntry.AllPackages };
        await Access.RunAsSystem(() => Mesh.GetMeshNodeStream(registered.GrantPath)
                .Update(node => node with { Content = new PluginGrant { InstanceId = "idle-entitled", Entries = [entry] } }))
            .Should().Within(TestTimeouts.Convergence).Emit("the grant is widened", cancellationToken: ct);
        await Access.RunAsSystem(() => (IObservable<MeshNode>)Mesh.GetMeshNodeStream(registered.GrantPath))
            .Where(n => n.ContentAs<PluginGrant>(Mesh.JsonSerializerOptions)?.Entries.Count == 1)
            .Should().Within(TestTimeouts.Convergence).Emit("the widened grant reaches the mirror", cancellationToken: ct);

        var widened = await Resolve(authenticator, registered.RawKey);
        widened.IsUnavailable.Should().BeFalse(widened.UnavailableReason ?? "a verdict");
        widened.Instance!.Allows("Plugins", "Edu").Should().BeTrue(
            "the grant change is what the next resolution decides with — the held mirror follows the change feed");

        await Access.RunAsSystem(() => Mesh.GetMeshNodeStream(registered.InstancePath)
                .Update(node => node with
                {
                    Content = node.ContentAs<MeshWeaverInstance>(Mesh.JsonSerializerOptions)! with { IsDisabled = true },
                }))
            .Should().Within(TestTimeouts.Convergence).Emit("the instance is disabled", cancellationToken: ct);
        await Access.RunAsSystem(() => (IObservable<MeshNode>)Mesh.GetMeshNodeStream(registered.InstancePath))
            .Where(n => n.ContentAs<MeshWeaverInstance>(Mesh.JsonSerializerOptions)?.IsDisabled == true)
            .Should().Within(TestTimeouts.Convergence).Emit("the disable reaches the mirror", cancellationToken: ct);

        var disabled = await Resolve(authenticator, registered.RawKey);
        disabled.IsUnavailable.Should().BeFalse("a disabled instance is a verdict, not a stall");
        disabled.Instance.Should().BeNull("…and the verdict is 'no' on the very next request");
    }

    /// <summary>
    /// SHOULD-FAIL-IF: the plan ladder is a query re-run per read (or per expired window). Then no
    /// listing of the tier namespace exists in the process-wide cache, and each read that misses the
    /// window opens a mesh query of its own. NEGATIVE CONTROL in the same test: a tier added after
    /// the first read is what a later read returns — the listing follows the change feed.
    /// </summary>
    [Fact]
    public async Task ThePlanLadder_IsOneLiveListing_AndATierChangeReachesTheNextRead()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedTier("personal", 1);
        var ladder = Mesh.ServiceProvider.GetRequiredService<PlanTierLadder>();

        var first = await ladder.Read().Should().Within(TestTimeouts.Convergence).Emit("the ladder is read", cancellationToken: ct);
        first.Ids.Should().Contain("personal");
        (Cache.GetQuery(PlanTierLadder.ListingId) is not null).Should().BeTrue(
            "the ladder is ONE listing in the process-wide cache, hydrated once and kept current by the change feed");
        var connections = Cache.LiveQueryConnections;

        var reads = await Enumerable.Range(0, 10).Select(_ => ladder.Read()).Merge().ToList()
            .Should().Within(TestTimeouts.Convergence).Emit("ten concurrent reads answer", cancellationToken: ct);
        reads.Should().HaveCount(10).And.OnlyContain(r => r.Ids.Contains("personal"));
        Cache.LiveQueryConnections.Should().Be(connections, "ten reads share the one listing and open no query of their own");

        await SeedTier("pro", 2);
        await Observable.Interval(TimeSpan.FromMilliseconds(50)).StartWith(0L)
            .SelectMany(_ => ladder.Read())
            .Where(r => r.Ids.Contains("pro"))
            .Should().Within(TestTimeouts.Convergence)
            .Emit("a tier added after the first read reaches a later read through the listing", cancellationToken: ct);

        Task SeedTier(string id, int rank) =>
            Access.RunAsSystem(() => NodeFactory.CreateNode(MeshNode.FromPath($"{PlanTierLadder.Namespace}/{id}") with
                {
                    NodeType = "Markdown",
                    Content = JsonSerializer.SerializeToElement(new { rank, allAccess = false }),
                }))
                .Should().Within(TestTimeouts.Convergence).Emit($"seeding tier {id}", cancellationToken: ct);
    }
}
