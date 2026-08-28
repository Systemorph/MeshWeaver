using System;
using System.Reactive.Linq;
using MeshWeaver.Messaging;
using Xunit;

namespace MeshWeaver.Messaging.Hub.Test;

/// <summary>
/// <c>RunAsSystem</c> / <c>RunAsHub</c> / <c>RunAs</c> must be drop-in replacements for
/// <see cref="Observable.Defer{T}"/> on the fault channel: a synchronous throw while COMPOSING is
/// an <c>OnError</c>, not an exception escaping <c>Subscribe</c>.
///
/// <para>🚨 Why this is load-bearing rather than pedantic. Callers classify faults off the
/// SEQUENCE. <c>IdentityRead.Bounded</c> maps <c>OnError</c> to
/// <c>IdentityReadOutcome.Unavailable</c> — the distinction issue #637 exists to preserve, where
/// "we could not find out" must never be reported as "we found out, and the answer is no". An
/// escaping throw bypasses that mapping entirely. So swapping a <c>Defer</c> for a
/// <c>RunAsSystem</c> silently moved the fault out of the channel the caller watches, which is
/// exactly the migration MeshWeaver#2583 performed on the two identity reads; review caught it and
/// this pins it for the other ~65 call sites.</para>
/// </summary>
public class ImpersonationScopeFaultChannelTest
{
    private static AccessService NewAccessService() => new();

    [Fact]
    public void A_synchronous_composition_throw_becomes_OnError_not_an_escaping_exception()
    {
        var access = NewAccessService();
        var boom = new InvalidOperationException("composition failed");

        Exception? captured = null;
        var threw = Record.Exception(() =>
            access.RunAsSystem<int>(() => throw boom).Subscribe(_ => { }, ex => captured = ex));

        Assert.Null(threw);
        Assert.Same(boom, captured);
    }

    /// <summary>
    /// The same guarantee with a caller identity present, which takes the OTHER branch of
    /// <c>Subscribe</c> (<c>SubscribeRestoring</c>) — the factory is guarded before either branch
    /// is chosen, so both must behave alike.
    /// </summary>
    [Fact]
    public void The_guarantee_holds_with_a_caller_identity_present()
    {
        var access = NewAccessService();
        access.SetContext(new AccessContext { ObjectId = "someone", Name = "Someone" });
        var boom = new InvalidOperationException("composition failed");

        Exception? captured = null;
        var threw = Record.Exception(() =>
            access.RunAsSystem<int>(() => throw boom).Subscribe(_ => { }, ex => captured = ex));

        Assert.Null(threw);
        Assert.Same(boom, captured);
    }

    /// <summary>
    /// Only the FACTORY is guarded — the same contract <see cref="Observable.Defer{T}"/> offers. A
    /// fault raised by the composed sequence still arrives as OnError, and normal values still flow,
    /// so the catch has not swallowed anything it should not.
    /// </summary>
    [Fact]
    public void A_normal_sequence_is_unaffected()
    {
        var access = NewAccessService();

        var seen = 0;
        access.RunAsSystem(() => Observable.Return(42)).Subscribe(v => seen = v);
        Assert.Equal(42, seen);

        Exception? captured = null;
        access.RunAsSystem<int>(() => Observable.Throw<int>(new InvalidOperationException("later")))
            .Subscribe(_ => { }, ex => captured = ex);
        Assert.IsType<InvalidOperationException>(captured);
    }
}
