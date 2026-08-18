using System;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace MeshWeaver.Messaging.Hub.Test;

/// <summary>
/// #1790 — impersonation is an <c>AsyncLocal</c> store/restore pair, so the two halves must land on
/// ONE logical flow. <c>Observable.Using(() =&gt; access.ImpersonateAsSystem(), _ =&gt; crossHubCall())</c>
/// splits them across two threads: Rx runs the resource factory on the SUBSCRIBING thread and
/// disposes the resource when the inner observable TERMINATES — for a cross-hub request/response,
/// the owning hub's response thread.
///
/// <para><b>Two distinct leaks, and they need two distinct fixes.</b></para>
/// <list type="number">
///   <item><description><b>The subscriber keeps <c>system-security</c> latched.</b> Nothing disposes
///   the scope on that thread, so every later statement on it runs elevated. Only the CALL SITE can
///   close this — <see cref="ImpersonationScopeExtensions.RunAsSystem{T}"/> opens and closes the
///   scope inside one synchronous <c>Subscribe</c>.</description></item>
///   <item><description><b>The terminating thread is handed a foreign "previous".</b> The scope's
///   <c>Dispose</c> writes the SUBSCRIBER's prior identity into a thread that never had it — a hub
///   action block, a transport handshake thread, an ASP.NET request thread. That is an identity
///   INJECTION, not a cleanup, and it is closed inside <see cref="AccessService"/> itself, so it
///   holds for every call site including the ones still written the old way.</description></item>
/// </list>
///
/// <para><b>Why the assertions are the NEGATIVE ones.</b> An identity leak has no failing operation
/// to observe — the work succeeds either way. The only thing that distinguishes the broken shape
/// from the fixed one is what each thread is left holding, so that is what is asserted.</para>
///
/// <para><b>Why a suppressed-flow thread.</b> <c>Thread.Start</c> and <c>Task.Run</c> both CAPTURE
/// the caller's <c>ExecutionContext</c>, which would hand the "foreign" thread the subscriber's
/// AsyncLocals and make it a poor model of a hub action block. <see cref="OnAForeignThread"/>
/// suppresses the flow so the thread starts with a genuinely empty context — exactly what a hub
/// thread has before its delivery pipeline stamps one.</para>
/// </summary>
public class ImpersonationScopeThreadAffinityTest
{
    private static readonly AccessContext Alice = new() { ObjectId = "alice", Name = "Alice" };
    private static readonly AccessContext Bob = new() { ObjectId = "bob", Name = "Bob" };

    private const string System = "system-security";

    // ── Leak 2: the terminating thread ──────────────────────────────────────────────────────────

    /// <summary>
    /// The premise, pinned across threads. <see cref="SystemScopeDoesNotEscapeTest"/> pins the
    /// SAME-thread case (where <c>Using</c> disposes correctly); this pins the cross-thread one,
    /// which is the shape every cross-hub call actually has. The latch is asserted as STILL PRESENT
    /// for the raw idiom — the <see cref="AccessService"/> fix deliberately does not invent a
    /// dispose on a thread nothing disposed on, so the raw idiom stays unsafe and the seal below is
    /// the answer.
    /// </summary>
    [Fact]
    public void TheRawIdiomLatchesTheImpersonatedIdentityOnTheSubscribingThread()
    {
        var access = new AccessService();
        access.SetContext(Alice);

        var terminatesElsewhere = new Subject<int>();
        using var subscription = Observable
            .Using(access.ImpersonateAsSystem, _ => terminatesElsewhere)
            .Subscribe(_ => { });

        access.Context?.ObjectId.Should().Be(System,
            "Observable.Using opened the scope on THIS thread and nothing will dispose it here — "
            + "the inner observable terminates on someone else's thread. Every later statement on "
            + "this thread therefore runs as System (#1790). This is why the raw idiom must be "
            + "replaced at the call site, not merely made polite on the far end");
    }

    /// <summary>
    /// 🚨 The <see cref="AccessService"/> half: a scope disposed on a thread that never had its
    /// store must write NOTHING. Before the fix this test reads "alice" on a thread that has never
    /// heard of Alice — the subscriber's previous identity injected into a hub thread.
    /// </summary>
    [Fact]
    public void ARawIdiomDisposalOnAForeignThreadInjectsNothingIntoIt()
    {
        var access = new AccessService();
        access.SetContext(Alice);

        var terminatesElsewhere = new Subject<int>();
        using var subscription = Observable
            .Using(access.ImpersonateAsSystem, _ => terminatesElsewhere)
            .Subscribe(_ => { });

        string? foreignBefore = null;
        string? foreignAfter = null;
        var terminatedThere = false;

        OnAForeignThread(() =>
        {
            foreignBefore = access.Context?.ObjectId;
            // Completing here is what makes Observable.Using dispose the impersonation scope —
            // on THIS thread, which never opened it.
            terminatesElsewhere.OnCompleted();
            terminatedThere = true;
            foreignAfter = access.Context?.ObjectId;
        });

        terminatedThere.Should().BeTrue(
            "non-vacuity: the disposal must actually have been triggered on the foreign thread — "
            + "an assertion about a scope that was never disposed proves nothing");
        foreignBefore.Should().BeNull(
            "the model must be honest: a hub action block starts with no ambient identity, so the "
            + "flow-suppressed thread must too — if this is non-null the thread inherited the "
            + "subscriber's context and is not a foreign thread at all");
        foreignAfter.Should().BeNull(
            "the scope's store never happened on THIS thread, so its restore must not happen here "
            + "either. Writing the subscriber's 'previous' identity onto a hub/transport thread is "
            + "an identity injection: the next thing that thread does reads a principal that has "
            + "nothing to do with the message it is processing (#1790)");
    }

    // ── Leak 1: the subscribing thread ──────────────────────────────────────────────────────────

    /// <summary>
    /// 🚨 The seam half. Same cross-thread termination, but through
    /// <see cref="ImpersonationScopeExtensions.RunAsSystem{T}"/>: the subscribing thread is handed
    /// back exactly what it had. Against the pre-fix seam (which was <c>Observable.Using</c> +
    /// <c>ContainIdentity</c>) this reads "system-security" — <c>ContainIdentity</c> restores around
    /// NOTIFICATIONS only, never around the Subscribe that opened the scope.
    /// </summary>
    [Fact]
    public void RunAsSystemLeavesTheSubscribersOwnIdentityOnTheSubscribingThread()
    {
        var access = new AccessService();
        access.SetContext(Alice);

        var terminatesElsewhere = new Subject<int>();
        var subscribedUnder = (string?)null;

        using var subscription = access
            .RunAsSystem(() => Observable.Defer(() =>
            {
                subscribedUnder = access.Context?.ObjectId;
                return terminatesElsewhere;
            }))
            .Subscribe(_ => { });

        subscribedUnder.Should().Be(System,
            "non-vacuity: the scope must still COVER the work — a seal that fixed the thread by not "
            + "impersonating at all would trade an escalation for a fail-closed read");
        access.Context?.ObjectId.Should().Be("alice",
            "the impersonation was established FOR that read and must not outlive the Subscribe "
            + "that opened it. Observing 'system-security' here means the rest of this thread's "
            + "work — the rest of an HTTP request, of a script run, of a hub handler — executes "
            + "with Permission.All (#1790)");
    }

    /// <summary>The same guarantee for the hub-identity and explicit-identity overloads.</summary>
    [Fact]
    public void EveryRunAsOverloadLeavesTheSubscribingThreadAsItFoundIt()
    {
        var access = new AccessService();
        access.SetContext(Alice);

        var explicitIdentitySawBob = false;
        using (access.RunAs(Bob, () => Observable.Defer(() =>
               {
                   explicitIdentitySawBob = access.Context?.ObjectId == "bob";
                   return new Subject<int>();
               })).Subscribe(_ => { }))
        {
            explicitIdentitySawBob.Should().BeTrue("RunAs(identity) must switch for the work");
            access.Context?.ObjectId.Should().Be("alice", "…and switch back on the way out");
        }

        var resolverRanOnTheSubscribingThread = false;
        var resolvedIdentitySawBob = false;
        using (access.RunAs(
                   () =>
                   {
                       // The resolver overload exists precisely so the identity can be read on the
                       // SUBSCRIBING thread (BlazorView.ResolveCircuitUser reads AsyncLocals).
                       resolverRanOnTheSubscribingThread = access.Context?.ObjectId == "alice";
                       return Bob;
                   },
                   () => Observable.Defer(() =>
                   {
                       resolvedIdentitySawBob = access.Context?.ObjectId == "bob";
                       return new Subject<int>();
                   })).Subscribe(_ => { }))
        {
            resolverRanOnTheSubscribingThread.Should().BeTrue(
                "the resolver must run BEFORE the switch, on the subscribing thread — resolving it "
                + "after would read the identity we are about to install");
            resolvedIdentitySawBob.Should().BeTrue("RunAs(resolver) must switch for the work");
            access.Context?.ObjectId.Should().Be("alice", "…and switch back on the way out");
        }
    }

    /// <summary>
    /// The other end of the seal: a <see cref="ImpersonationScopeExtensions.RunAsSystem{T}"/>
    /// pipeline that terminates on a foreign thread must leave that thread untouched too — nothing
    /// is disposed there at all any more.
    /// </summary>
    [Fact]
    public void RunAsSystemTouchesNothingOnTheTerminatingThread()
    {
        var access = new AccessService();
        access.SetContext(Alice);

        var terminatesElsewhere = new Subject<int>();
        using var subscription = access
            .RunAsSystem(() => (IObservable<int>)terminatesElsewhere)
            .Subscribe(_ => { });

        string? foreignAfter = null;
        var terminatedThere = false;
        OnAForeignThread(() =>
        {
            terminatesElsewhere.OnCompleted();
            terminatedThere = true;
            foreignAfter = access.Context?.ObjectId;
        });

        terminatedThere.Should().BeTrue("non-vacuity: the termination must have happened there");
        foreignAfter.Should().BeNull("the seal disposes on the subscribing thread and nowhere else");
    }

    /// <summary>
    /// 🚨 The property the whole fix rests on, and the one that would make it a trade rather than a
    /// fix if it did not hold: closing the scope at the end of <c>Subscribe</c> must NOT un-elevate
    /// work that was already scheduled inside it. An <c>ExecutionContext</c> captured while the
    /// scope was open is an immutable snapshot; restoring the subscribing flow afterwards cannot
    /// reach into it. This is what lets a query provider capture lazily on a pooled thread and still
    /// read <c>system-security</c>.
    /// </summary>
    [Fact]
    public void WorkScheduledInsideTheScopeStillRunsAsSystemAfterTheThreadIsRestored()
    {
        var access = new AccessService();
        access.SetContext(Alice);

        string? seenByAFlowedContinuation = null;
        Thread? flowed = null;

        using var subscription = access
            .RunAsSystem(() => Observable.Defer(() =>
            {
                // Models an IIoPool / scheduler hop taken while subscribing: the ExecutionContext
                // is captured HERE, inside the scope.
                flowed = new Thread(() => seenByAFlowedContinuation = access.Context?.ObjectId)
                {
                    IsBackground = true
                };
                flowed.Start();
                return new Subject<int>();
            }))
            .Subscribe(_ => { });

        flowed.Should().NotBeNull("non-vacuity: the hop must have been taken inside the scope");
        flowed!.Join(TimeSpan.FromSeconds(30)).Should().BeTrue("the flowed continuation must finish");

        seenByAFlowedContinuation.Should().Be(System,
            "a captured ExecutionContext is a snapshot — the restore on the subscribing thread "
            + "creates a NEW context for that thread and cannot rewrite one already taken. If this "
            + "were not so, sealing the scope would fail-close every provider that reads the "
            + "identity lazily off a pooled thread");
        access.Context?.ObjectId.Should().Be("alice", "and the subscribing thread is still restored");
    }

    // ── The cases thread-affinity must NOT break ────────────────────────────────────────────────

    /// <summary>
    /// The overwhelmingly common shape: open and close on one thread. Thread-affinity must be
    /// invisible here.
    /// </summary>
    [Fact]
    public void ASynchronousScopeStillRestoresExactlyAsBefore()
    {
        var access = new AccessService();
        access.SetContext(Alice);

        using (access.ImpersonateAsSystem())
            access.Context?.ObjectId.Should().Be(System);

        access.Context?.ObjectId.Should().Be("alice");

        // Nested, in order — each restores to its enclosing identity, not to null or the default.
        using (access.SwitchAccessContext(Bob))
        {
            using (access.ImpersonateAsSystem())
                access.Context?.ObjectId.Should().Be(System);
            access.Context?.ObjectId.Should().Be("bob");
        }

        access.Context?.ObjectId.Should().Be("alice");
    }

    /// <summary>
    /// A scope that spans an <c>await</c> is ONE logical flow even when the continuation resumes on
    /// another thread: the ExecutionContext carries both the context and the scope marker forward,
    /// so the restore is still ours to make. Thread-affinity that keyed on the thread id alone
    /// would silently stop restoring here — which is why the marker exists.
    /// </summary>
    [Fact]
    public async Task AScopeSpanningAnAwaitStillRestores()
    {
        var access = new AccessService();
        access.SetContext(Alice);

        int threadInside;
        using (access.ImpersonateAsSystem())
        {
            await Task.Yield();
            await Task.Run(() => { }, TestContext.Current.CancellationToken);
            threadInside = Environment.CurrentManagedThreadId;
            access.Context?.ObjectId.Should().Be(System,
                "the impersonation flows with the continuation — that is what AsyncLocal is for");
        }

        access.Context?.ObjectId.Should().Be("alice",
            $"the scope was disposed on thread {threadInside} inside the same logical flow, so the "
            + "restore is ours to make even if the physical thread changed");
    }

    /// <summary>
    /// 🚨 The case that decides the marker is not sufficient on its own.
    /// <c>AccessContextCaptureExtensions</c> wraps every subscriber callback in a
    /// <see cref="AccessService.SwitchAccessContext"/> scope and documents that "AsyncLocal is
    /// touched ONLY for the duration of the callback". If the callback opens an impersonation that
    /// is never disposed on this thread (exactly what a nested <c>Observable.Using</c> does), the
    /// marker no longer names the outer scope — so the outer restore must fall back to the thread
    /// it was opened on, or the leaked identity would outlive the callback.
    /// </summary>
    [Fact]
    public void AnUndisposedNestedScopeDoesNotStrandTheEnclosingOne()
    {
        var access = new AccessService();
        access.SetContext(Alice);

        using (access.SwitchAccessContext(Bob))
        {
            // Deliberately never disposed on this thread — the nested-Observable.Using shape.
            _ = access.ImpersonateAsSystem();
            access.Context?.ObjectId.Should().Be(System);
        }

        access.Context?.ObjectId.Should().Be("alice",
            "the enclosing scope must still clamp the thread back on the way out. Anything else "
            + "would turn CarryAccessContext's per-callback scope into a leak of its own — the "
            + "opposite of what it exists for");
    }

    /// <summary>
    /// Runs <paramref name="action"/> on a thread whose <c>ExecutionContext</c> is genuinely empty —
    /// the model of a hub action block or a transport handshake thread. Flow suppression is what
    /// makes it honest: both <c>Thread.Start</c> and <c>Task.Run</c> would otherwise capture the
    /// caller's AsyncLocals and the "foreign" thread would silently be the subscriber's own flow.
    /// </summary>
    private static void OnAForeignThread(Action action)
    {
        Exception? failure = null;
        Thread thread;
        using (ExecutionContext.SuppressFlow())
        {
            thread = new Thread(() =>
            {
                try { action(); }
                catch (Exception ex) { failure = ex; }
            })
            { IsBackground = true };
            thread.Start();
        }

        thread.Join(TimeSpan.FromSeconds(30)).Should().BeTrue(
            "the foreign thread must finish — a timeout here means the termination wedged, not that "
            + "the identity is fine");
        if (failure is not null)
            throw failure;
    }
}
