using System;
using System.Collections.Generic;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Threading.Tasks;
using Xunit;

namespace MeshWeaver.Messaging.Hub.Test;

/// <summary>
/// Issue #1444 — an impersonation scope that ESCAPES the operation it was opened for.
///
/// <para><b>The mechanism.</b> <c>Observable.Using(access.ImpersonateAsSystem, _ =&gt; work)</c> reads
/// as "run this work as System", and for the work itself it is. But Rx forwards <c>OnNext</c> to the
/// subscriber BEFORE <c>Using</c> disposes its resource, so anything the subscriber composes on that
/// emission is built while the impersonation is still open. The first test below pins that raw Rx
/// behaviour, because every conclusion here rests on it and it must fail loudly if a future Rx
/// changes it.</para>
///
/// <para><b>Why it does not stop at one hop.</b> The mesh write primitives eager-capture
/// <see cref="AccessService.Context"/> when CALLED and re-stamp it around their own emissions
/// (<c>CarryAccessContext</c>), so a leaked System identity is re-acquired at every hop after it —
/// which is how a whole install chain landed as <c>system-security</c> in education CI run
/// 31704948831 while <c>_UserActivity</c>, posted directly from the request thread rather than
/// composed on an emission, attributed correctly.</para>
///
/// <para><b>Why it is a permission concern, not only an audit one.</b>
/// <c>AccessControlPipeline.HandleGetPermission</c> already carries the scar: trusting the ambient
/// context there returned <c>Permission.All</c> for every caller including anonymous, because
/// <c>SecurityService</c>'s bootstrap-time system scope leaked past its using-block. That call site
/// defends itself; <see cref="ImpersonationScopeExtensions"/> fixes the class at the source.</para>
/// </summary>
public class SystemScopeDoesNotEscapeTest
{
    private static readonly AccessContext Alice = new() { ObjectId = "alice", Name = "Alice" };

    private const string System = "system-security";

    /// <summary>
    /// The premise, pinned. Not a test of our code — a test of the Rx ordering every other case here
    /// depends on. If this ever goes green-by-accident (Rx disposing before it forwards), the seal
    /// below stops being load-bearing and that is worth learning from a failure rather than from a
    /// production log.
    /// </summary>
    [Fact]
    public void TheIdiomaticSystemScopeIsStillOpenWhenTheSubscriberRuns()
    {
        var access = new AccessService();
        access.SetContext(Alice);

        string? seenInsideCallback = null;
        Observable
            .Using(access.ImpersonateAsSystem, _ => Observable.Return(1))
            .Subscribe(_ => seenInsideCallback = access.Context?.ObjectId);

        seenInsideCallback.Should().Be(System,
            "Rx forwards OnNext BEFORE Using disposes its resource — so a subscriber composing a "
            + "write on this emission builds it under System. That is #1444's mechanism, and the "
            + "seal exists because of it");
        access.Context?.ObjectId.Should().Be("alice",
            "the scope is correctly restored once the pipeline terminates — the leak is confined to "
            + "the notification window, which is exactly where composition happens");
    }

    /// <summary>
    /// The fix: the same work, the same System identity INSIDE, and the subscriber's own identity on
    /// the way out.
    /// </summary>
    [Fact]
    public void RunAsSystemRunsTheWorkAsSystemAndHandsBackTheCallersIdentity()
    {
        var access = new AccessService();
        access.SetContext(Alice);

        string? seenByTheWork = null;
        string? seenByTheSubscriber = null;

        access
            .RunAsSystem(() => Observable.Defer(() =>
            {
                seenByTheWork = access.Context?.ObjectId;
                return Observable.Return(1);
            }))
            .Subscribe(_ => seenByTheSubscriber = access.Context?.ObjectId);

        seenByTheWork.Should().Be(System,
            "the scope must still cover the work — sealing it must not make a system read fail "
            + "closed, which would trade one defect for another");
        seenByTheSubscriber.Should().Be("alice",
            "the identity was established FOR that read; it is never an inheritance");
    }

    /// <summary>
    /// The half that matters most: the leak reaches whatever the subscriber BUILDS, because the mesh
    /// write primitives capture the ambient when they are called. This is the shape the install path
    /// had.
    /// </summary>
    [Fact]
    public void WhatTheSubscriberCOMPOSESNoLongerCapturesSystem()
    {
        var access = new AccessService();
        access.SetContext(Alice);

        // Stands in for a write primitive: it snapshots the ambient identity at CALL time, exactly
        // as CreateNode/CreateNodes/Update do, and that snapshot is what would be authorised.
        IObservable<string?> WriteCapturingCallerIdentity()
        {
            var capturedWhenCalled = access.Context?.ObjectId;
            return Observable.Return(capturedWhenCalled);
        }

        var leaked = Observable
            .Using(access.ImpersonateAsSystem, _ => Observable.Return(1))
            .SelectMany(_ => WriteCapturingCallerIdentity())
            .Wait();

        var sealedOff = access
            .RunAsSystem(() => Observable.Return(1))
            .SelectMany(_ => WriteCapturingCallerIdentity())
            .Wait();

        leaked.Should().Be(System, "the unsealed idiom is what filed #1444");
        sealedOff.Should().Be("alice",
            "the write a user's operation composes must be authorised and attributed as the USER — "
            + "System silently succeeding where the user might have been refused is the serious half");
    }

    /// <summary>
    /// Terminal notifications are composed on too (<c>Catch</c>, <c>Finally</c>, a completion arm
    /// that writes a manifest), so the seal has to cover them or it just moves the leak.
    /// </summary>
    [Fact]
    public void TheSealCoversErrorAndCompletionToo()
    {
        var access = new AccessService();
        access.SetContext(Alice);

        string? onCompleted = null;
        access.RunAsSystem(() => Observable.Empty<int>())
            .Subscribe(_ => { }, () => onCompleted = access.Context?.ObjectId);

        string? onError = null;
        access.RunAsSystem<int>(() => Observable.Throw<int>(new InvalidOperationException("boom")))
            .Subscribe(_ => { }, _ => onError = access.Context?.ObjectId, () => { });

        onCompleted.Should().Be("alice", "a completion arm that writes must write as the caller");
        onError.Should().Be("alice", "so must an error arm that records the failure");
    }

    /// <summary>
    /// 🚨 The property that makes adopting this safe at an existing call site: a subscriber with NO
    /// identity keeps whatever the framework leaves it. Nothing is invented and nothing is clamped,
    /// so a background flow that never had a user is not fail-closed by the seal.
    /// </summary>
    [Fact]
    public void ASubscriberWithNoIdentityIsLeftExactlyAsItWas()
    {
        var access = new AccessService();
        access.SetContext(null);

        string? seen = null;
        access.RunAsSystem(() => Observable.Return(1))
            .Subscribe(_ => seen = access.Context?.ObjectId);

        seen.Should().Be(System,
            "with nothing to restore, the seal is a pass-through — clamping to null here would "
            + "fail-close background flows that never had a user, which is not what #1444 is about");
    }

    /// <summary>
    /// Nested scopes restore to the ENCLOSING identity, not to a null or to the process default —
    /// the case a system-initiated install hits (System all the way down, and correctly so).
    /// </summary>
    [Fact]
    public void ASystemInitiatedFlowStaysSystem()
    {
        var access = new AccessService();
        access.SetContext(Alice);

        string? inner = null;
        using (access.ImpersonateAsSystem())
        {
            access.RunAsSystem(() => Observable.Return(1))
                .Subscribe(_ => inner = access.Context?.ObjectId);
        }

        inner.Should().Be(System,
            "the seal restores the SUBSCRIBER's identity, which here genuinely is System — a "
            + "system-initiated install must not be rewritten into a user's");
        access.Context?.ObjectId.Should().Be("alice");
    }

    /// <summary>
    /// A host without an <see cref="AccessService"/> (minimal test fixtures) must still run the work,
    /// and must still run it COLD — the side effect belongs to Subscribe, not to composition.
    /// </summary>
    [Fact]
    public void WithoutAnAccessServiceTheWorkStillRunsAndStaysCold()
    {
        AccessService? none = null;
        var runs = 0;

        var pipeline = none.RunAsSystem(() =>
        {
            runs++;
            return Observable.Return(1);
        });

        runs.Should().Be(0, "composition must not run the work — every framework primitive is cold");
        pipeline.Wait().Should().Be(1);
        runs.Should().Be(1);
    }

    /// <summary>
    /// <see cref="ImpersonationScopeExtensions.ContainIdentity{T}"/> on an already-composed chain —
    /// the case where the impersonated region is several calls and it is the LAST one's emission the
    /// caller builds on.
    /// </summary>
    [Fact]
    public async Task ContainIdentitySealsAnAlreadyComposedChainAcrossThreads()
    {
        var access = new AccessService();
        access.SetContext(Alice);

        var source = new Subject<int>();
        var seen = new List<string?>();
        var done = new TaskCompletionSource<bool>();

        source.ContainIdentity(access).Subscribe(_ =>
        {
            seen.Add(access.Context?.ObjectId);
            done.TrySetResult(true);
        });

        // Emit from a thread that never had the AsyncLocal set AND is inside a system scope, which is
        // what a hub action block running an impersonated write looks like from the subscriber's side.
        await Task.Run(() =>
        {
            using (access.ImpersonateAsSystem())
                source.OnNext(1);
        }, TestContext.Current.CancellationToken);

        await done.Task;
        seen.Should().ContainSingle().Which.Should().Be("alice",
            "the emitting side's identity is its own business — what the SUBSCRIBER observes is the "
            + "identity it subscribed with");
    }
}
