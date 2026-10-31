using System;
using System.Reactive.Linq;
using System.Threading;
using Xunit;

namespace MeshWeaver.Hosting.Orleans.Test;

/// <summary>
/// Review of #7734: leaving <see cref="MessageHubGrain.WhenStopping"/> must never WAIT for a stop
/// callback that is running on another thread. Under <c>Amb</c> the readiness arm disposes this arm
/// while holding Amb's gate; if the stop's callback is waiting for that gate, a disposal that waits
/// for the callback deadlocks both threads, <c>ApplicationStopping</c> included.
///
/// <para>The window is held open deterministically: the callback parks (a volatile flag under a
/// bounded <see cref="SpinWait.SpinUntil(Func{bool}, TimeSpan)"/>, released in a <c>finally</c>),
/// and the unsubscribe runs on a second thread. With <c>CancellationTokenRegistration.Dispose()</c>
/// the unsubscribe blocks until the callback is released; with <c>Unregister()</c> it returns at
/// once.</para>
/// </summary>
public class HostStopSignalTest
{
    [Fact]
    public void UnsubscribingWhileTheStopCallbackRuns_DoesNotWaitForIt()
    {
        using var stopping = new CancellationTokenSource();
        var entered = 0;
        var release = 0;
        var unsubscribed = 0;

        var subscription = MessageHubGrain.WhenStopping(stopping.Token).Subscribe(_ =>
        {
            Volatile.Write(ref entered, 1);
            SpinWait.SpinUntil(() => Volatile.Read(ref release) == 1, TimeSpan.FromSeconds(30));
        });

        var stopper = new Thread(() => stopping.Cancel()) { IsBackground = true };
        var leaver = new Thread(() =>
        {
            subscription.Dispose();
            Volatile.Write(ref unsubscribed, 1);
        }) { IsBackground = true };
        try
        {
            stopper.Start();
            Assert.True(SpinWait.SpinUntil(() => Volatile.Read(ref entered) == 1, TimeSpan.FromSeconds(10)),
                "the stop callback must be running before the subscriber leaves");

            leaver.Start();
            Assert.True(SpinWait.SpinUntil(() => Volatile.Read(ref unsubscribed) == 1, TimeSpan.FromSeconds(5)),
                "unsubscribing must not wait for the stop callback still running on another thread — "
                + "under Amb's gate that wait is a deadlock");
        }
        finally
        {
            Volatile.Write(ref release, 1);
            stopper.Join(TimeSpan.FromSeconds(10));
            leaver.Join(TimeSpan.FromSeconds(10));
        }
    }

    [Fact]
    public void AStop_IsSignalledOnce()
    {
        using var stopping = new CancellationTokenSource();
        var signals = 0;
        var completed = false;
        using var subscription = MessageHubGrain.WhenStopping(stopping.Token)
            .Subscribe(_ => Interlocked.Increment(ref signals), () => completed = true);

        stopping.Cancel();

        Assert.Equal(1, Volatile.Read(ref signals));
        Assert.True(completed);
    }
}
