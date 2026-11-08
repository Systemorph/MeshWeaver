using System;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Threading;
using MeshWeaver.Mesh.Threading;
using Xunit;

namespace MeshWeaver.Hosting.Test;

/// <summary>
/// Issue #7992 — <see cref="IoPoolRegistry.OwnUntilDrain"/>: a subscription owned for the mesh's
/// lifetime is disposed by its owner's release BEFORE the pool it runs on is cancelled, on whichever
/// drain comes first (<see cref="IoPoolRegistry.Dispose"/> at a silo's stop,
/// <see cref="IoPoolRegistry.DrainAll()"/> in mesh teardown). Its <c>.Finally</c> then runs on the
/// drain caller's thread, and the pool's cancel finds nothing attached.
/// </summary>
public class IoPoolOwnUntilDrainTest
{
    private static readonly TimeSpan Timeout10 = TimeSpan.FromSeconds(10);

    private static (IDisposable Subscription, Func<int> FinallyThread, Func<bool> Subscribed) OpenPooledLeg(
        IoPool pool, Subject<int> feed)
    {
        var subscribed = 0;
        var finallyThread = 0;
        var subscription = pool
            .SubscribeThroughPool(Observable.Create<int>(o =>
            {
                var d = feed.Subscribe(o);
                Volatile.Write(ref subscribed, 1);
                return d;
            }))
            .Finally(() => Volatile.Write(ref finallyThread, Environment.CurrentManagedThreadId))
            .Subscribe(_ => { }, _ => { });
        return (subscription, () => Volatile.Read(ref finallyThread), () => Volatile.Read(ref subscribed) == 1 && pool.CurrentInFlight == 0);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AnOwnedLeg_IsReleasedBeforeTheCancel_OnEitherDrain(bool siloPath)
    {
        var registry = new IoPoolRegistry();
        var pool = (IoPool)registry.Get(IoPoolNames.Query);
        var feed = new Subject<int>();
        var (subscription, finallyThread, subscribed) = OpenPooledLeg(pool, feed);
        SpinWait.SpinUntil(subscribed, Timeout10).Should().BeTrue("precondition: the pooled leg is attached");

        registry.OwnUntilDrain(subscription, "test-owner");
        registry.OwnedUntilDrainCount.Should().Be(1);

        if (siloPath)
            registry.Dispose();
        else
            registry.DrainAll().Should().Be(0);

        finallyThread().Should().Be(Environment.CurrentManagedThreadId,
            "the owner's release ran on the drain caller's thread, before the cancel");
        feed.HasObservers.Should().BeFalse("the release tore the leg down to its source");
        pool.UnownedLeaks.Should().Be(0, "an owned leg is released, never found attached by the cancel");
        registry.OwnedUntilDrainCount.Should().Be(0);
        registry.Dispose();
    }

    [Fact]
    public void WithoutOwnership_TheSameLegIsAnUnownedLeak()
    {
        var registry = new IoPoolRegistry();
        var pool = (IoPool)registry.Get(IoPoolNames.Query);
        var feed = new Subject<int>();
        var (subscription, finallyThread, subscribed) = OpenPooledLeg(pool, feed);
        SpinWait.SpinUntil(subscribed, Timeout10).Should().BeTrue("precondition: the pooled leg is attached");

        registry.Dispose();
        SpinWait.SpinUntil(() => pool.UnownedLeaks == 1, Timeout10)
            .Should().BeTrue("the negative control: nobody released the leg, so the cancel found it attached");
        finallyThread().Should().Be(0, "the cancel delivers nothing (#7968)");
        subscription.Dispose();
    }

    [Fact]
    public void AnEarlyDispose_EndsTheLegOnce_AndAnOwnAfterTheReleaseIsDisposedAtOnce()
    {
        var registry = new IoPoolRegistry();
        var disposals = 0;
        var handle = registry.OwnUntilDrain(System.Reactive.Disposables.Disposable.Create(() => Interlocked.Increment(ref disposals)), "early");
        handle.Dispose();
        handle.Dispose();
        Volatile.Read(ref disposals).Should().Be(1);
        registry.OwnedUntilDrainCount.Should().Be(0);

        registry.Dispose();
        var late = 0;
        registry.OwnUntilDrain(System.Reactive.Disposables.Disposable.Create(() => Interlocked.Increment(ref late)), "late");
        Volatile.Read(ref late).Should().Be(1, "owned after the release began: nothing outlives the pools");
    }
}
