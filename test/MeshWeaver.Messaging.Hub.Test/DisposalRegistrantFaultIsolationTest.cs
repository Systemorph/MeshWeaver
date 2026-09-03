using System;
using System.Reactive.Linq;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using Xunit;

namespace MeshWeaver.Messaging.Hub.Test;

/// <summary>
/// 🚨 <b>One registered cleanup that throws must not silently cancel every cleanup behind it.</b>
///
/// <para>A hub's synchronous cleanups all live in one Rx <c>CompositeDisposable</c>, and
/// <c>CompositeDisposable.Dispose</c> walks its list with NO per-item guard: the first registrant
/// that throws ends the walk. Everything registered after it is skipped — a subscription left live,
/// a NACK never minted — and the only trace is one warning attributed to <c>DisposeImpl</c> as a
/// whole, naming neither the registrant that threw nor the work that was dropped.</para>
///
/// <para><b>What that cost, measured.</b> On main shard 4, 2026-09-02 (run 33630685580) a per-node
/// owner hub went down through the disposal watchdog's out-of-band teardown. One of its disposal
/// actions resolved a service out of a lifetime scope that was already closed —
/// <c>ObjectDisposedException: Instances cannot be resolved … from this LifetimeScope</c> — the walk
/// stopped there, and the <c>OwnerDisposing</c> NACK behind it was never minted. Its writer heard
/// nothing and burned the full 31 s <c>WriteVerdictBound</c> before reporting
/// <c>OwnerUnreachable</c>: an acked write lost to a teardown that had truncated itself
/// (<c>LateNackReenqueueTest</c>). The registrant that threw was a real bug; skipping every OTHER
/// registrant was the framework turning that bug into silent data loss.</para>
///
/// <para>🚨 This is isolation, not tolerance. Nothing is swallowed: the fault is logged NAMING the
/// registrant that raised it, and the remaining cleanups still run — the same per-leg isolation the
/// reactive dispose actions in <c>DisposeImpl</c> already had, and which the synchronous ones did
/// not.</para>
/// </summary>
public class DisposalRegistrantFaultIsolationTest(ITestOutputHelper output) : HubTestBase(output)
{
    /// <summary>Throws on disposal — the registrant that used to end the walk.</summary>
    private sealed class ThrowsOnDispose : IDisposable
    {
        public bool WasDisposed { get; private set; }

        public void Dispose()
        {
            WasDisposed = true;
            throw new ObjectDisposedException(
                "SomeLifetimeScope",
                "Instances cannot be resolved and nested lifetimes cannot be created from this "
                + "LifetimeScope as it (or one of its parent scopes) has already been disposed.");
        }
    }

    /// <summary>Records that it ran — the cleanup that used to be skipped.</summary>
    private sealed class RecordsItsDisposal : IDisposable
    {
        public bool WasDisposed { get; private set; }

        public void Dispose() => WasDisposed = true;
    }

    /// <summary>
    /// The contract: a throwing registrant is isolated, and every LATER registrant still runs —
    /// both the <see cref="IDisposable"/> overload and the <c>Action&lt;IMessageHub&gt;</c> one,
    /// because the NACK that was actually lost was registered through the second.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task AThrowingCleanup_DoesNotCancelTheCleanupsRegisteredAfterIt()
    {
        var host = GetHost();
        var hub = host.GetHostedHub(new Address("dispose-registrant-isolation", "1"), c => c);

        var thrower = new ThrowsOnDispose();
        var afterDisposable = new RecordsItsDisposal();
        var afterActionRan = false;

        // Registration ORDER is the whole subject: the composite walks in insertion order, so the
        // thrower has to go in first for the two behind it to be at risk at all.
        hub.RegisterForDisposal(thrower);
        hub.RegisterForDisposal(afterDisposable);
        hub.RegisterForDisposal(_ => afterActionRan = true);

        hub.Dispose();
        await hub.DisposalCompleted.FirstOrDefaultAsync().Await().WaitAsync(TimeSpan.FromSeconds(120));

        Assert.True(thrower.WasDisposed, "precondition: the throwing registrant was reached at all");
        Assert.True(afterDisposable.WasDisposed,
            "a registrant that throws is a bug in that registrant; it must not also cancel the "
            + "IDisposable cleanups registered behind it — that is how a teardown truncates itself "
            + "and leaves subscriptions live");
        Assert.True(afterActionRan,
            "nor the Action<IMessageHub> cleanups: RegisterOwnerDisposingNack registers through "
            + "this overload, and skipping it is what lost an acked write for 31 s on main "
            + "shard 4, 2026-09-02");
    }
}
