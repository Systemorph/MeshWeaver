using System.Collections.Immutable;
using System.Reactive.Subjects;
using MeshWeaver.Hosting;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// 🚨 The triggers that re-decide a followed standing Read (the node-stream cache's
/// <c>FollowStandingRead</c>) must reach the switch ONE AT A TIME.
///
/// <para>The follow attached its access signals and then emitted the initial re-check on the
/// subscribing thread, straight into the observer it had just handed to the signals. A signal
/// that committed on another thread at that moment (a fold-input commit, an access commit on the
/// invalidation feed) therefore called the same observer CONCURRENTLY — and the next operator is
/// Rx's <c>Switch</c>, which takes the new inner's id under its gate but swaps the inner
/// subscription OUTSIDE it. Interleaved, the older trigger can install its inner last: it disposes
/// the newest re-check, its own answer is then ignored because its id is no longer the latest, and
/// the standing Read stays at its pending marker until the permission-establishment budget refuses
/// it. Measured on core CI (Graph.Test <c>OpenSubscriptionEndsWithTheReadTest</c>, two different
/// tests on two shards): a reader whose grant stands was refused with
/// <see cref="UnauthorizedAccessException"/> exactly one budget (20 s) after it opened, before
/// receiving anything.</para>
///
/// <para>The concurrency is driven here deterministically: the initial trigger is HELD inside the
/// observer while another thread emits a signal. With the triggers serialized that signal waits
/// until the initial trigger has returned; without, it enters beside it.</para>
/// </summary>
public class StandingReadReDecisionTriggersTest
{
    // How long the held initial trigger waits for a signal to enter beside it. On the serialized
    // path nothing can enter, so a passing run spends this whole window (sanctioned negative wait:
    // there is no positive signal for "the other thread is blocked on the gate").
    private static readonly TimeSpan OverlapWindow = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan ThreadBudget = TimeSpan.FromSeconds(30);

    [Fact]
    public void ASignalNeverEntersBesideTheInitialReCheck()
    {
        var signals = new Subject<bool>();
        var inside = 0;
        var overlapped = 0;
        var initialHeld = 0;
        var signalSent = 0;
        var received = ImmutableList<bool>.Empty;

        var signaller = new Thread(() =>
        {
            SpinWait.SpinUntil(() => Volatile.Read(ref initialHeld) == 1, ThreadBudget);
            signals.OnNext(true);
            Volatile.Write(ref signalSent, 1);
        }) { IsBackground = true };
        signaller.Start();

        using var subscription = MeshNodeStreamCache.ReDecisionTriggers(signals).Subscribe(bypassCache =>
        {
            if (Interlocked.Increment(ref inside) > 1)
                Volatile.Write(ref overlapped, 1);
            try
            {
                if (!bypassCache)
                {
                    // The initial re-check, on the subscribing thread: hold it until a signal entered
                    // beside it, or the window passed.
                    Volatile.Write(ref initialHeld, 1);
                    SpinWait.SpinUntil(() => Volatile.Read(ref overlapped) == 1, OverlapWindow);
                }
                ImmutableInterlocked.Update(ref received, list => list.Add(bypassCache));
            }
            finally
            {
                Interlocked.Decrement(ref inside);
            }
        });

        Assert.True(SpinWait.SpinUntil(() => Volatile.Read(ref signalSent) == 1, ThreadBudget),
            "the signalling thread delivered its signal");
        Assert.True(signaller.Join(ThreadBudget), "the signalling thread finished");
        Assert.Equal(0, Volatile.Read(ref overlapped));
        Assert.Equal([false, true], received);
    }

    /// <summary>
    /// CONTROL for the ordering the follow exists to keep: the signals are attached BEFORE the
    /// initial re-check is taken, so a commit that lands while that re-check reads is announced
    /// (a fix that emitted the initial trigger first — <c>StartWith</c> — would lose it).
    /// </summary>
    [Fact]
    public void TheSignalsAreAttachedBeforeTheInitialReCheck()
    {
        var signals = new Subject<bool>();
        bool? attachedAtInitial = null;
        using var subscription = MeshNodeStreamCache.ReDecisionTriggers(signals).Subscribe(bypassCache =>
        {
            if (!bypassCache)
                attachedAtInitial = signals.HasObservers;
        });

        Assert.True(attachedAtInitial, "the initial re-check is emitted only after the signals are attached");
        // …and a later signal still reaches the follow.
        var later = false;
        using var second = MeshNodeStreamCache.ReDecisionTriggers(signals).Subscribe(b => later |= b);
        signals.OnNext(true);
        Assert.True(later);
    }
}
