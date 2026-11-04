using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Messaging;
using MeshWeaver.PluginCatalog;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace MeshWeaver.Hosting.Test;

/// <summary>
/// 🚨 <b>The registry's sync-token signing key is HELD, never re-read per request
/// (MeshWeaver#7354).</b> Every instance-key and sync-token check on <c>/api/plugins</c> verifies
/// against <c>Admin/SyncTokenSigningKey/current</c>. It used to be a one-shot <c>GetMeshNode</c>
/// behind a one-minute TTL, so once a minute a request paid a cross-hub round-trip to the key's
/// owner, and any round-trip unanswered for ten seconds answered that request 503 — 13 of the 18
/// registry-side 503s measured over 43 h on memex-cloud.
///
/// <para>Pinned here on a real mesh: after the first read, stepping the clock far past the old TTL
/// issues NO further read; a rotation written by ANOTHER service instance (another replica's
/// writer) reaches the held key through the node's change feed, still with no read; and a hold
/// whose mirror terminates is re-taken by the next request while the last key keeps answering —
/// never a 10-second read on the request path. Negative control: before the fix, the first
/// <c>Existing()</c> past the TTL issued a read (<see cref="SyncTokenSigningKeyService.ReadsIssued"/>
/// would step).</para>
/// </summary>
public class SyncTokenSigningKeyIsHeldTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private static readonly string MasterKey =
        Convert.ToBase64String(Enumerable.Range(1, 32).Select(i => (byte)i).ToArray());

    private DateTimeOffset clock = DateTimeOffset.UtcNow;

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder)
            .AddPluginCatalog()
            .ConfigureServices(services => services.AddSingleton<IConfiguration>(
                new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Ai:KeyProtection:MasterKey"] = MasterKey,
                }).Build()));

    private SyncTokenSigningKeyService NewService(Func<IObservable<MeshNode>>? hold = null) =>
        new(Mesh, NullLogger<SyncTokenSigningKeyService>.Instance)
        {
            Now = () => clock,
            HoldOverride = hold,
        };

    [Fact]
    public async Task AHeldKey_IsAnsweredWithoutARead_AndFollowsARotationFromAnotherWriter()
    {
        var ct = TestContext.Current.CancellationToken;
        using var registry = NewService();
        using var otherReplica = NewService();

        var minted = await registry.Resolve().Should().Within(TestTimeouts.WriteConvergence)
            .Emit("the first resolve mints the key", cancellationToken: ct);

        await Observable.Interval(TimeSpan.FromMilliseconds(50)).StartWith(0L)
            .Select(_ => registry.IsHeld)
            .Where(isHeld => isHeld)
            .Should().Within(TestTimeouts.Convergence)
            .Emit("the key node exists now, so its mirror is held", cancellationToken: ct);
        var readsAfterMint = registry.ReadsIssued;

        // Far past the window the old TTL re-read on.
        clock += SyncTokenSigningKeyService.CacheDuration * 10;
        var later = await registry.Existing().Should().Within(TestTimeouts.Convergence)
            .Emit("a held key answers from memory", cancellationToken: ct);
        later!.Current.Should().Equal(minted.Current);
        registry.ReadsIssued.Should().Be(readsAfterMint,
            "a held key is never re-read on the request path — that read was the 503 (#7354)");

        // Another replica rotates. The held mirror carries it here through the change feed.
        var rotated = await otherReplica.Rotate("test").Should().Within(TestTimeouts.WriteConvergence)
            .Emit("the other replica rotates", cancellationToken: ct);
        rotated.Current.SequenceEqual(minted.Current).Should().BeFalse("a rotation mints fresh material");

        await Observable.Interval(TimeSpan.FromMilliseconds(50)).StartWith(0L)
            .SelectMany(_ =>
            {
                clock += SyncTokenSigningKeyService.CacheDuration * 2; // re-materialise the held record
                return registry.Existing();
            })
            .Where(material => material is not null && material.Current.SequenceEqual(rotated.Current))
            .Should().Within(TestTimeouts.Convergence)
            .Emit("the rotation reaches the held key through the node's change feed", cancellationToken: ct);
        registry.ReadsIssued.Should().Be(readsAfterMint,
            "the rotation arrived through the hold, not through a re-read");
    }

    [Fact]
    public async Task AHoldThatTerminates_IsRetakenByTheNextRequest_WhileTheLastKeyKeepsAnswering()
    {
        var ct = TestContext.Current.CancellationToken;
        var mirrors = new List<Subject<MeshNode>>();
        IObservable<MeshNode> Mirror()
        {
            var mirror = new Subject<MeshNode>();
            lock (mirrors)
                mirrors.Add(mirror);
            // The real mirror's current frame, then a stream the test terminates on cue.
            return Mesh.ServiceProvider.GetRequiredService<AccessService>()
                .RunAsSystem(() => (IObservable<MeshNode>)Mesh.GetMeshNodeStream(SyncTokenSigningKeys.Path))
                .Where(node => node is not null)
                .Take(1)
                .Concat(mirror);
        }

        using var writer = NewService();
        await writer.Resolve().Should().Within(TestTimeouts.WriteConvergence)
            .Emit("the key is minted", cancellationToken: ct);

        using var registry = NewService(Mirror);
        var first = await registry.Existing().Should().Within(TestTimeouts.Convergence)
            .Emit("a cold registry reads once", cancellationToken: ct);
        first.Should().NotBeNull();
        await Observable.Interval(TimeSpan.FromMilliseconds(50)).StartWith(0L)
            .Where(_ => registry.IsHeld)
            .Should().Within(TestTimeouts.Convergence)
            .Emit("the hold delivered its first frame", cancellationToken: ct);
        var reads = registry.ReadsIssued;

        // The owner fails: the mirror faults and the hold is dropped.
        Subject<MeshNode> faulted;
        lock (mirrors)
            faulted = mirrors.Single();
        faulted.OnError(new InvalidOperationException("the owner failed"));
        registry.IsHeld.Should().BeFalse("a faulted mirror releases its hold");

        // Within the window the last key answers at once, and the request re-takes the hold.
        var answered = await registry.Existing().Should().Within(TestTimeouts.Convergence)
            .Emit("the last key keeps answering", cancellationToken: ct);
        answered!.Current.Should().Equal(first!.Current);
        registry.ReadsIssued.Should().Be(reads, "the request was answered from memory");
        await Observable.Interval(TimeSpan.FromMilliseconds(50)).StartWith(0L)
            .Where(_ => registry.IsHeld)
            .Should().Within(TestTimeouts.Convergence)
            .Emit("the request re-took the hold", cancellationToken: ct);
        lock (mirrors)
            mirrors.Should().HaveCount(2, "exactly one new hold was taken");

        clock += SyncTokenSigningKeyService.CacheDuration * 10;
        (await registry.Existing().Should().Within(TestTimeouts.Convergence)
                .Emit("the re-taken hold answers", cancellationToken: ct))!
            .Current.Should().Equal(first.Current);
        registry.ReadsIssued.Should().Be(reads, "the re-taken hold answers without a read");
    }

    private static byte[] KeyBytes(byte seed) => Enumerable.Repeat(seed, SyncTokenSigningKeys.KeyByteLength).ToArray();

    /// <summary>A key node as a mirror frame would carry it. Untagged material is read as-is by the
    /// protector, so the bytes a frame carries are the bytes the service must answer with.</summary>
    private static MeshNode KeyNode(byte seed) =>
        new(SyncTokenSigningKeys.Id, SyncTokenSigningKeys.Namespace)
        {
            NodeType = SyncTokenSigningKeys.NodeType,
            Content = new SyncTokenSigningKey
            {
                ProtectedCurrent = Convert.ToBase64String(KeyBytes(seed)),
                CurrentIssuedAt = DateTimeOffset.UtcNow,
                RotateAfter = DateTimeOffset.UtcNow.AddDays(30),
            },
        };

    /// <summary>Every mirror the service subscribes is a subject the test drives.</summary>
    private sealed class Mirrors
    {
        private readonly List<Subject<MeshNode>> all = new();

        public IObservable<MeshNode> Open()
        {
            var mirror = new Subject<MeshNode>();
            lock (all)
                all.Add(mirror);
            return mirror;
        }

        public int Count
        {
            get
            {
                lock (all)
                    return all.Count;
            }
        }

        public Subject<MeshNode> Last
        {
            get
            {
                lock (all)
                    return all[^1];
            }
        }
    }

    private async Task<(SyncTokenSigningKeyService Registry, Mirrors Mirrors, byte[] Stored)> HeldOnSeed(
        byte seed, CancellationToken ct)
    {
        var writer = NewService();
        var stored = await writer.Resolve().Should().Within(TestTimeouts.WriteConvergence)
            .Emit("the key is minted", cancellationToken: ct);
        writer.Dispose();

        var mirrors = new Mirrors();
        var registry = NewService(mirrors.Open);
        (await registry.Existing().Should().Within(TestTimeouts.Convergence)
            .Emit("a cold registry reads once", cancellationToken: ct)).Should().NotBeNull();
        mirrors.Count.Should().Be(1, "the read took the hold");
        mirrors.Last.OnNext(KeyNode(seed));
        registry.IsHeld.Should().BeTrue("the hold delivered a record");
        return (registry, mirrors, stored.Current);
    }

    /// <summary>Review of #7889, P1: a run of reattachments that fail WITHOUT delivering must not keep
    /// extending the fallback window — once the window opened by losing the healthy hold has elapsed,
    /// the next request reads.</summary>
    [Fact]
    public async Task FailedReattachments_DoNotExtendTheFallback_PastItsExpiryARequestReads()
    {
        var ct = TestContext.Current.CancellationToken;
        var (registry, mirrors, _) = await HeldOnSeed(7, ct);
        using var owned = registry;
        var reads = registry.ReadsIssued;

        mirrors.Last.OnError(new InvalidOperationException("the owner failed"));
        var step = SyncTokenSigningKeyService.CacheDuration / 3;
        for (var attempt = 1; attempt <= 2; attempt++)
        {
            clock += step;
            (await registry.Existing().Should().Within(TestTimeouts.Convergence)
                    .Emit("inside the window the newest key answers", cancellationToken: ct))!
                .Current.Should().Equal(KeyBytes(7));
            registry.ReadsIssued.Should().Be(reads, "inside the window nothing is read");
            mirrors.Count.Should().Be(1 + attempt, "each request re-took the hold");
            mirrors.Last.OnError(new InvalidOperationException("the reattachment failed before delivering"));
        }

        clock += step + TimeSpan.FromSeconds(1);
        await registry.Existing().Should().Within(TestTimeouts.Convergence)
            .Emit("past the window the request reads", cancellationToken: ct);
        registry.ReadsIssued.Should().Be(reads + 1,
            "failed reattachments must not have renewed the window that the lost healthy hold opened");
    }

    /// <summary>Review of #7889, P2: a rotation the hold delivered AFTER the last request survives the
    /// hold's loss — the fallback serves the newest record, never the older materialisation.</summary>
    [Fact]
    public async Task ARotationDeliveredBeforeTheMirrorFaults_IsWhatTheFallbackServes()
    {
        var ct = TestContext.Current.CancellationToken;
        var (registry, mirrors, _) = await HeldOnSeed(7, ct);
        using var owned = registry;
        (await registry.Existing().Should().Within(TestTimeouts.Convergence)
                .Emit("the held key answers", cancellationToken: ct))!
            .Current.Should().Equal(KeyBytes(7));
        var reads = registry.ReadsIssued;

        mirrors.Last.OnNext(KeyNode(9)); // the rotation arrives with no request in between
        mirrors.Last.OnError(new InvalidOperationException("the owner failed"));

        (await registry.Existing().Should().Within(TestTimeouts.Convergence)
                .Emit("the fallback answers", cancellationToken: ct))!
            .Current.Should().Equal(KeyBytes(9), "the newest delivered record is what this replica knows");
        registry.ReadsIssued.Should().Be(reads, "answered from memory");
    }

    /// <summary>Review of #7889, P2: a frame with no usable key drops what this replica knew — the next
    /// request reads instead of serving the superseded key.</summary>
    [Fact]
    public async Task AFrameWithNoUsableKey_DropsTheKnownKey_AndTheNextRequestReads()
    {
        var ct = TestContext.Current.CancellationToken;
        var (registry, mirrors, stored) = await HeldOnSeed(7, ct);
        using var owned = registry;
        var reads = registry.ReadsIssued;

        mirrors.Last.OnNext(KeyNode(7) with { Content = null });
        registry.IsHeld.Should().BeFalse("an unusable frame releases the hold");

        var answered = await registry.Existing().Should().Within(TestTimeouts.Convergence)
            .Emit("the request reads the node", cancellationToken: ct);
        registry.ReadsIssued.Should().Be(reads + 1, "nothing known about the key may be served any more");
        answered!.Current.Should().Equal(stored, "the read answers with what the store holds");
        answered.Current.SequenceEqual(KeyBytes(7)).Should().BeFalse("the superseded key is gone");
    }
}
