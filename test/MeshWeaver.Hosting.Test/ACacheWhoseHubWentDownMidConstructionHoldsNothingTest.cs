using System;
using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace MeshWeaver.Hosting.Test;

/// <summary>
/// 🚨 A node-stream cache whose hub goes down while the cache is still being constructed must
/// end up holding NOTHING (#6078, review of MeshWeaver#6437).
///
/// <para><b>How the hub goes down mid-construction.</b> The cache is a lazily resolved singleton,
/// and its hub is a hosted hub of the mesh. A first resolution that overlaps the mesh teardown
/// makes that hub a LATE construction, and the owner disposes a late hub as soon as its
/// construction returns — before the cache constructor has finished. The hub's registrants are
/// disposed at its ShutDown, and a registrant added after that is disposed on the spot.</para>
///
/// <para><b>The defect.</b> The constructor registered its own <c>Dispose()</c> on the hub FIRST and
/// only then created the idle sweep and the two change-feed subscriptions. When the hub's ShutDown
/// came in between, <c>Dispose()</c> ran with those still unassigned; they were created a moment
/// later on a disposed cache and nothing ever released them — the process-wide feed held the dead
/// cache for the life of the process.</para>
///
/// <para><b>The repro, deterministic.</b> The cache is constructed against a hub of the test's own
/// whose invalidation feed is the mesh's real feed behind a counting wrapper. The wrapper parks the
/// CACHE's subscription (its handler's target is the cache) until the test has torn the cache hub
/// all the way down. With the registration first, the cache has already disposed itself by then
/// and the two feed subscriptions it then makes stay live; with the registration last, the
/// registration finds the hub gone and disposes the cache, releasing both.</para>
///
/// <para><b>Positive control.</b> A cache whose hub stays up holds both subscriptions — the
/// counter reads live subscriptions, so the zero above is not an instrument that cannot count.</para>
/// </summary>
public class ACacheWhoseHubWentDownMidConstructionHoldsNothingTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    /// <summary>
    /// The mesh's real invalidation feed behind a wrapper that counts the subscriptions a
    /// <see cref="MeshNodeStreamCache"/> holds on it, and can park the cache's first subscription.
    /// Every call is delegated, so the feed behaves exactly as in production.
    /// </summary>
    private sealed class CountingFeed(IMeshInvalidationFeed inner) : IMeshInvalidationFeed
    {
        private int live;
        private int parkArmed;
        private int release;

        /// <summary>Completed when the cache's subscription has reached the feed and is parked.</summary>
        public AsyncSubject<Unit> CacheSubscriptionParked { get; } = new();

        /// <summary>Live subscriptions a cache holds on this feed (handler and gap stream).</summary>
        public int LiveCacheSubscriptions => Volatile.Read(ref live);

        public void ArmPark() => Volatile.Write(ref parkArmed, 1);

        public void Release() => Volatile.Write(ref release, 1);

        public IDisposable Subscribe(Action<MeshChangeEvent> handler, MeshChangeKind? filter = null)
        {
            if (handler.Target is not MeshNodeStreamCache)
                return inner.Subscribe(handler, filter);
            if (Interlocked.Exchange(ref parkArmed, 0) == 1)
            {
                CacheSubscriptionParked.OnNext(Unit.Default);
                CacheSubscriptionParked.OnCompleted();
                // The release travels INTO this parked thread, so it is a volatile flag under a
                // bounded SpinUntil, written in the test's finally.
                SpinWait.SpinUntil(() => Volatile.Read(ref release) == 1, TestTimeouts.Convergence);
            }
            return Counted(inner.Subscribe(handler, filter));
        }

        public IObservable<ChangeFeedGap> Gaps => Observable.Create<ChangeFeedGap>(observer =>
            Counted(inner.Gaps.Subscribe(observer)));

        private IDisposable Counted(IDisposable subscription)
        {
            Interlocked.Increment(ref live);
            var once = 0;
            return Disposable.Create(() =>
            {
                if (Interlocked.Exchange(ref once, 1) == 1) return;
                Interlocked.Decrement(ref live);
                subscription.Dispose();
            });
        }
    }

    private (IMessageHub Owner, CountingFeed Feed) CreateOwner()
    {
        var feed = new CountingFeed(Mesh.ServiceProvider.GetRequiredService<IMeshInvalidationFeed>());
        var owner = Mesh.GetHostedHub(
            new Address("cacheowner", Guid.NewGuid().ToString("N")),
            config => config.WithServices(services => services.AddSingleton<IMeshInvalidationFeed>(feed)),
            HostedHubCreation.Always);
        owner.Should().NotBeNull("precondition: the owner hub is hosted by the mesh");
        return (owner!, feed);
    }

    private MeshNodeStreamCache Construct(IMessageHub owner) =>
        new(owner, Mesh.ServiceProvider.GetRequiredService<ILogger<MeshNodeStreamCache>>());

    [Fact(Timeout = 240_000)]
    public async Task ACacheWhoseHubShutDownDuringItsConstruction_ReleasesEveryFeedSubscription()
    {
        var ct = TestContext.Current.CancellationToken;
        var (owner, feed) = CreateOwner();
        feed.ArmPark();

        var construction = Task.Run(() => Construct(owner), ct);
        try
        {
            await feed.CacheSubscriptionParked.Should().Within(TestTimeouts.Convergence)
                .Emit("the cache constructor must reach its change-feed subscription", ct);

            // The cache hub was handed back to the constructor; take it all the way down while
            // the constructor is still running — the late-hub teardown, deterministically.
            var cacheHub = owner.GetHostedHub(new Address("cache", owner.Address.Id), HostedHubCreation.Never);
            cacheHub.Should().NotBeNull("precondition: the constructor created its hub before subscribing");
            cacheHub!.Dispose();
            await cacheHub.DisposalCompleted.ObserveCompletion(
                ex => FileOutput.WriteLine($"cache hub disposal fault: {ex}"), ct);
            cacheHub.RunLevel.Should().Be(MessageHubRunLevel.Dead,
                "precondition: the cache hub is past ShutDown, so its registrants are already released");
        }
        finally
        {
            feed.Release();
        }

        var cache = await construction.WaitAsync(TestTimeouts.Convergence, ct);

        cache.IsDisposed.Should().BeTrue("a cache whose hub is gone is torn down with it");
        feed.LiveCacheSubscriptions.Should().Be(0,
            "a cache whose hub went down during its construction must release every change-feed "
            + "subscription — registered before they existed, its Dispose() ran too early and both "
            + "were created on a dead cache, held by the process-wide feed forever");
    }

    /// <summary>
    /// 🚨 POSITIVE CONTROL. A cache whose hub stays up holds its handler and gap subscriptions on
    /// the feed, and releases both when the hub goes down — so the counter reads live
    /// subscriptions, and the zero above is a measurement.
    /// </summary>
    [Fact(Timeout = 240_000)]
    public async Task ACacheWhoseHubStaysUp_HoldsItsFeedSubscriptions_UntilTheHubGoes()
    {
        var ct = TestContext.Current.CancellationToken;
        var (owner, feed) = CreateOwner();

        var cache = Construct(owner);
        feed.LiveCacheSubscriptions.Should().Be(2,
            "CONTROL: a live cache holds the change handler and the gap stream on the feed");
        cache.IsDisposed.Should().BeFalse();

        var cacheHub = owner.GetHostedHub(new Address("cache", owner.Address.Id), HostedHubCreation.Never);
        cacheHub.Should().NotBeNull();
        cacheHub!.Dispose();
        await cacheHub.DisposalCompleted.ObserveCompletion(
            ex => FileOutput.WriteLine($"cache hub disposal fault: {ex}"), ct);

        cache.IsDisposed.Should().BeTrue("the cache hub owns the cache's lifetime");
        feed.LiveCacheSubscriptions.Should().Be(0, "the cache's Dispose() releases both subscriptions");
    }
}
