using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Reactive.Linq;
using System.Threading.Tasks;
using Memex.Portal.Shared.Authentication;
using MeshWeaver.Fixture;
using MeshWeaver.Graph;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.PluginCatalog;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Memex.Portal.Shared.Test;

/// <summary>
/// Systemorph/MeshWeaver#3119 — the registry's instance-key resolution must not be a fresh owner
/// round-trip on the request hot path.
///
/// <para>Measured on memex-cloud 2026-09-02: under cross-schema fan-out load the owning per-node hub
/// of the CI instance's node did not answer the authenticator's one-shot read inside its ten-second
/// budget, so once a minute (the verdict cache) every fetch of every satellite gate and bake was told
/// <c>503 Instance-key resolution is temporarily unavailable</c>. The fix reads each leg through the
/// process-wide <see cref="IMeshNodeStreamCache"/> — a live listing for existence, the owner's mirror
/// for content — so only the FIRST request for an instance waits on an owner.</para>
///
/// <para>These pin the three properties that make that true, against the cache's OWN bookkeeping
/// rather than timing: (a) two resolutions leave one live mirror and evict nothing between them,
/// (b) a disable is seen by the next resolution with no restart and no <c>Invalidate</c>, (c) an
/// unknown key is a definitive no that opens no point read on the absent path.</para>
/// </summary>
public class InstanceKeyLiveResolutionTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder).AddPluginCatalog();

    private MeshWeaverInstanceService Service() => new(
        Mesh.ServiceProvider.GetRequiredService<IMeshService>(),
        Mesh,
        Mesh.ServiceProvider.GetRequiredService<ILogger<MeshWeaverInstanceService>>(),
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>()).Build());

    /// <summary>A REAL authenticator on the real mesh hub — no read seam, so what is exercised is the
    /// production listing-then-mirror composition.</summary>
    private InstanceRegistryAuthenticator Authenticator() => new(
        Mesh, Mesh.ServiceProvider.GetRequiredService<ILogger<InstanceRegistryAuthenticator>>());

    /// <summary>The process-wide cache, as its concrete type: its live-entry and eviction seams are
    /// the evidence that a read went through it rather than around it.</summary>
    private MeshNodeStreamCache Cache() =>
        (MeshNodeStreamCache)Mesh.ServiceProvider.GetRequiredService<IMeshNodeStreamCache>();

    private AccessService Access() => Mesh.ServiceProvider.GetRequiredService<AccessService>();

    private Task<InstanceRegistrationResult> Register(string instanceId) =>
        Service().Register("owner", "Owner", "owner@test.com", instanceId, instanceId)
            .Timeout(TestTimeouts.CrossSilo).Await();

    private static string IndexPath(string hash) =>
        $"{MeshWeaverInstanceNodeType.IndexNamespace}/{InstanceKeys.HashPrefix(hash)}";

    private static Task<InstanceAuthResult> Resolve(InstanceRegistryAuthenticator authenticator, string rawKey) =>
        authenticator.AuthenticateOutcome($"Bearer {rawKey}")
            .Should().Within(TestTimeouts.Convergence)
            .Emit("a resolution always reaches one of its three outcomes");

    /// <summary>
    /// The index trails the store. Registration is acknowledged when the nodes are WRITTEN; the
    /// listing the authenticator gates on sees them a beat later — in production that beat passed
    /// long before the key is first used, here it has to be waited out. Same id and query set as the
    /// authenticator, so this shares (and pre-hydrates) the very listing it will read.
    /// </summary>
    private Task AwaitListed(string path)
    {
        var parent = path[..path.LastIndexOf('/')];
        return Access().RunAsSystem(() => Mesh.GetQuery(
                $"instance-key-listing:{parent}", $"path:{parent} scope:children select:path"))
            .Where(nodes => nodes.Any(n => string.Equals(n.Path, path, StringComparison.Ordinal)))
            .Should().Within(TestTimeouts.Convergence)
            .Emit($"the listing of {parent} must catch up with the registration of {path}");
    }

    /// <summary>
    /// SHOULD-FAIL-IF: a resolution reads the instance with a one-shot request. Then no read-stream
    /// entry exists after the first resolution (the old code left none) and every request pays the
    /// owner again — the exact shape that produced the 503s.
    /// </summary>
    [Fact]
    public async Task TwoResolutions_OpenTheOwnerOnce_TheSecondReadsTheLiveMirror()
    {
        var registered = await Register("live-once");
        var instancePath = registered.Node.Path!;
        var indexPath = IndexPath(registered.Instance.KeyHash);
        await AwaitListed(indexPath);
        var cache = Cache();
        cache.IsReadStreamLive(instancePath).Should().BeFalse("nothing has read the instance yet");
        cache.IsReadStreamLive(indexPath).Should().BeFalse("the listing gate is not a point read");

        var evicted = ImmutableList<string>.Empty;
        using var evictions = cache.ReadStreamEvictions
            .Where(e => e.Path == instancePath || e.Path == indexPath)
            .Subscribe(e => ImmutableInterlocked.Update(ref evicted, list => list.Add(e.Path)));

        var authenticator = Authenticator();
        var first = await Resolve(authenticator, registered.RawKey);
        first.IsUnavailable.Should().BeFalse(first.UnavailableReason ?? "the first resolution reaches a verdict");
        first.Instance.Should().NotBeNull("a registered key authenticates");
        first.Instance!.Instance.InstanceId.Should().Be("live-once");
        cache.IsReadStreamLive(indexPath).Should().BeTrue(
            "the index leg read through the process-wide cache and left a live mirror behind");
        cache.IsReadStreamLive(instancePath).Should().BeTrue("so did the instance leg");

        var second = await Resolve(authenticator, registered.RawKey);
        second.IsUnavailable.Should().BeFalse(second.UnavailableReason ?? "the second resolution reaches a verdict");
        second.Instance!.Instance.InstanceId.Should().Be("live-once");
        cache.IsReadStreamLive(indexPath).Should().BeTrue("the second resolution reused the index mirror");
        cache.IsReadStreamLive(instancePath).Should().BeTrue("…and the instance mirror");
        evicted.Count.Should().Be(0,
            "no mirror was torn down between the two resolutions, so no second owner subscription "
            + "could have been opened — one owner read served both");
    }

    /// <summary>
    /// SHOULD-FAIL-IF: a verdict is memoised anywhere. The disable lands in the very mirror the
    /// authenticator reads, so the NEXT resolution must already refuse — no cache window, no
    /// <c>Invalidate</c>, no restart.
    /// </summary>
    [Fact]
    public async Task ADisable_IsSeenByTheNextResolution_WithoutARestartOrAnInvalidate()
    {
        var registered = await Register("live-disable");
        var instancePath = registered.Node.Path!;
        await AwaitListed(IndexPath(registered.Instance.KeyHash));
        var authenticator = Authenticator();
        var before = await Resolve(authenticator, registered.RawKey);
        before.Instance.Should().NotBeNull("the control: the key resolves while the instance is enabled");

        await Access().RunAsSystem(() => Service().SetDisabled(instancePath, true))
            .Timeout(TestTimeouts.Convergence).Await();
        // The authenticator reads the SAME mirror this waits on: once the disabled frame has landed
        // here, it has landed there.
        await Access().RunAsSystem(() => (IObservable<MeshNode>)Mesh.GetMeshNodeStream(instancePath))
            .Where(node => node.ContentAs<MeshWeaverInstance>(Mesh.JsonSerializerOptions)?.IsDisabled == true)
            .Should().Within(TestTimeouts.Convergence)
            .Emit("the disable reaches the live mirror");

        var after = await Resolve(authenticator, registered.RawKey);
        after.IsUnavailable.Should().BeFalse("a disabled instance is a VERDICT, not a stall");
        after.Instance.Should().BeNull(
            "…and the verdict is 'no' on the very next request — no cache window, no Invalidate");
    }

    /// <summary>
    /// SHOULD-FAIL-IF: an unknown key opens a point read on its (absent) index path. That is the
    /// framework's forbidden shape — a routing NotFound that arms the storm breaker — and it would
    /// also turn the definitive 401 into a 503. The listing decides, from memory.
    /// </summary>
    [Fact]
    public async Task AnUnknownKey_IsADefinitiveNo_ThatOpensNoPointRead()
    {
        // One registration so the index namespace exists and is listable — the production state.
        var registered = await Register("live-anchor");
        await AwaitListed(IndexPath(registered.Instance.KeyHash));
        var unknown = InstanceKeys.Generate();
        var unknownIndex = IndexPath(InstanceKeys.Hash(unknown));

        var outcome = await Resolve(Authenticator(), unknown);

        outcome.IsUnavailable.Should().BeFalse("an absent index entry IS an answer: this key is unknown");
        outcome.Instance.Should().BeNull();
        Cache().IsReadStreamLive(unknownIndex).Should().BeFalse(
            "the listing decided; no point stream was opened on a path that does not exist, so no "
            + "storm-breaker window was armed either");
    }
}

/// <summary>
/// The classification the live read applies, pinned without a mesh: which combinations of listing
/// and mirror become Present, Absent or Unavailable. The one that matters most is the first —
/// a frame that has NOT ARRIVED YET is <see cref="NodeReadStatus.Unavailable"/>, which the endpoints
/// render as the <c>503</c> the consumers retry on, never Absent, which they would read as a
/// definitive "your key is unknown".
/// </summary>
public class InstanceKeyFirstFrameTest
{
    private const string Path = "MeshWeaverInstance/0123456789ab";

    private static IEnumerable<MeshNode> Listing(params string[] paths) =>
        paths.Select(p => MeshNode.FromPath(p)).ToList();

    private static Task<NodeReadOutcome> Read(
        IObservable<IEnumerable<MeshNode>>? listing, Func<IObservable<MeshNode>> stream, TimeSpan? budget = null) =>
        InstanceRegistryAuthenticator.FirstFrame(listing, stream, Path, budget ?? TestTimeouts.Convergence)
            .Should().Within(TestTimeouts.Convergence)
            .Emit("FirstFrame always produces exactly one outcome");

    [Fact]
    public async Task NoFrameWithinTheBudget_IsUnavailable_NeverAbsent()
    {
        var outcome = await Read(
            Observable.Never<IEnumerable<MeshNode>>(),
            () => Observable.Never<MeshNode>(),
            budget: TimeSpan.FromMilliseconds(50));

        outcome.Status.Should().Be(NodeReadStatus.Unavailable,
            "a listing that has not hydrated establishes NOTHING about the key");
        (outcome.Failure is TimeoutException).Should().BeTrue("the budget elapsed");
        outcome.Failure!.Message.Should().Contain(Path, "the log line beside the 503 names the leg");
        outcome.Failure.Message.Should().Contain("NOT 'node not found'",
            "…and says what the stall is, so it cannot be mistaken for the old owner-never-answered diagnosis");
    }

    [Fact]
    public async Task AListingWithoutThePath_IsAbsent_AndNeverOpensTheMirror()
    {
        var opened = false;
        var outcome = await Read(
            Observable.Return(Listing("MeshWeaverInstance/someoneelse")),
            () => { opened = true; return Observable.Never<MeshNode>(); });

        outcome.Status.Should().Be(NodeReadStatus.Absent, "the listing is empty-on-absent and that IS the verdict");
        opened.Should().BeFalse("a point read on a path the listing does not hold is the forbidden shape");
    }

    [Fact]
    public async Task AListedPath_ReadsTheMirrorsFirstFrame()
    {
        var node = MeshNode.FromPath(Path) with { Content = new MeshWeaverInstanceIndex { KeyHash = "abc" } };
        var outcome = await Read(Observable.Return(Listing(Path)), () => Observable.Return(node));

        outcome.Status.Should().Be(NodeReadStatus.Present);
        outcome.Node.Should().Be(node);
    }

    [Fact]
    public async Task AListedPath_WhoseMirrorCompletesWithoutAFrame_IsAbsent()
    {
        // A tombstoned delete: the index still lists it for a beat, the owner has nothing to emit.
        var outcome = await Read(Observable.Return(Listing(Path)), Observable.Empty<MeshNode>);

        outcome.Status.Should().Be(NodeReadStatus.Absent, "a record that is going away is 'unknown key', not a stall");
    }

    [Fact]
    public async Task AFaultedMirror_IsUnavailable_WithTheFaultAttached()
    {
        var outcome = await Read(
            Observable.Return(Listing(Path)),
            () => Observable.Throw<MeshNode>(new InvalidOperationException("owner rejected the subscription")));

        outcome.Status.Should().Be(NodeReadStatus.Unavailable, "a fault is not a fact about the key");
        outcome.Failure!.Message.Should().Contain("owner rejected");
    }
}
