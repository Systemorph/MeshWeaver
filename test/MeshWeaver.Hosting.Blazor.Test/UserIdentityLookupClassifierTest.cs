using System.Reactive;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Reactive.Threading.Tasks;
using MeshWeaver.Hosting.AspNetCore.Portal;
using MeshWeaver.Mesh;
using Xunit;

namespace MeshWeaver.Hosting.Blazor.Test;

/// <summary>
/// Pins the COLD-CACHE boundary of <see cref="UserIdentityCache"/> — the point where "no mesh User
/// node carries this email" has to stay distinguishable from "the index cannot answer yet"
/// (issue #974, split out of #637).
///
/// <para><b>The bug.</b> <c>TryGetByEmail</c> returned <c>null</c> for both. "User unknown" is the
/// input that drives onboarding and provisioning, so a cache that had simply not received its
/// first snapshot — a portal restart, a slow first query, a storage stall — was indistinguishable
/// from a user who genuinely has no account. That is exactly the collapse #637 opened with, where a
/// storage stall redirected a fully signed-in user to the sign-up form.</para>
///
/// <para><b>Why these are pure.</b> The decision is a function of three facts the cache holds
/// (hit / hydrated / subscription-faulted), so it is classified in one place and tested without a
/// mesh, a hub or a query subscription — the same reason <c>AreaErrorClassifier</c> is
/// dependency-free. It also makes the cold leg testable AT ALL: against a live mesh the index has
/// usually hydrated before a test can observe it, so the interesting window is unobservable there.</para>
/// </summary>
public class UserIdentityLookupClassifierTest
{
    private static MeshNode User(string id) => new(id) { Name = id };

    // ---- FOUND: a hit is a hit regardless of the index's state ----

    [Fact]
    public void Hit_IsFound()
    {
        var node = User("rbuergi");
        var lookup = UserIdentityLookup.Classify(node, hydrated: true, subscriptionFailure: null);

        Assert.False(lookup.IsUnavailable);
        Assert.Same(node, lookup.Node);
    }

    [Fact]
    public void Hit_OnANotYetHydratedIndex_IsStillFound()
    {
        // A hit is positive evidence and needs no snapshot to be trustworthy — the node is right
        // there. Only a MISS depends on whether the index is authoritative yet.
        var node = User("rbuergi");
        var lookup = UserIdentityLookup.Classify(node, hydrated: false, subscriptionFailure: null);

        Assert.False(lookup.IsUnavailable);
        Assert.Same(node, lookup.Node);
    }

    // ---- THE DEFECT: a miss before the first snapshot is NOT "unknown user" ----

    [Fact]
    public void Miss_BeforeTheFirstSnapshot_IsUnavailable_NotUnknownUser()
    {
        // 🚨 The regression pin. Before the fix this returned the same null an unknown user
        // produced, so a cold cache could route an already-onboarded user into sign-up.
        var lookup = UserIdentityLookup.Classify(hit: null, hydrated: false, subscriptionFailure: null);

        Assert.True(lookup.IsUnavailable);
        Assert.Null(lookup.Node);
        Assert.Contains("snapshot", lookup.UnavailableReason);
    }

    [Fact]
    public void Miss_AfterTheSubscriptionFaulted_IsUnavailable_AndKeepsTheReason()
    {
        // A faulted subscription never hydrates. Answering "unknown" off it would make a dead
        // index look exactly like an empty user directory — permanently, and silently.
        var lookup = UserIdentityLookup.Classify(
            hit: null, hydrated: false, subscriptionFailure: "user index subscription failed: TimeoutException: x");

        Assert.True(lookup.IsUnavailable);
        Assert.Contains("TimeoutException", lookup.UnavailableReason);
    }

    [Fact]
    public void Miss_AfterAFaultThatFollowedAGoodSnapshot_StillReportsTheFault()
    {
        // Both flags set: hydrated once, then the feed died. The contents are now arbitrarily
        // stale, so a miss has stopped being evidence — the fault outranks the hydration flag.
        var lookup = UserIdentityLookup.Classify(
            hit: null, hydrated: true, subscriptionFailure: "user index subscription failed: IOException: gone");

        Assert.True(lookup.IsUnavailable);
        Assert.Contains("IOException", lookup.UnavailableReason);
    }

    // ---- NO OVER-REACH: a hydrated index must still be able to say "no" ----

    [Fact]
    public void Miss_OnAHydratedIndex_IsADefinitiveUnknown()
    {
        // The other half of the fix, and the one that keeps it from degenerating into "everything
        // is retryable": once the snapshot has landed, an absence IS a fact about the directory,
        // and onboarding a genuinely-new user must still work.
        var lookup = UserIdentityLookup.Classify(hit: null, hydrated: true, subscriptionFailure: null);

        Assert.False(lookup.IsUnavailable);
        Assert.Null(lookup.Node);
        Assert.Null(lookup.UnavailableReason);
    }

    // ---- THE WAIT: UntilDetermined must be LISTENING before it takes its first reading ----

    /// <summary>
    /// 🚨 #2031 / #2185. <see cref="UserIdentityLookup.UntilDetermined"/> exists to cover the window
    /// between a caller's own unavailable lookup and this subscription — and it used to be written
    /// <c>indexChanged.Select(_ =&gt; lookup()).StartWith(lookup())</c>, which gets the order exactly
    /// backwards. <c>StartWith</c>'s argument is evaluated while the chain is being BUILT, and
    /// <c>Concat</c> subscribes to the change feed only after the prepended value is delivered, so the
    /// window was moved rather than closed.
    ///
    /// <para><b>Why that is fatal rather than merely unlucky.</b> The index is a hot, non-replaying
    /// stream that fires once per APPLIED snapshot. A mesh that has finished writing never fires
    /// again, so a lost emission has no second chance: the observable never emits at all, and the
    /// caller reports a 60 s <c>TimeoutException</c> — not a wrong answer, which is precisely the
    /// signature of both open issues (full-assembly only, green in isolation, because in isolation
    /// the snapshot lands long after the subscribe and cannot fall in the window).</para>
    ///
    /// <para><b>Why this is deterministic and not a timing test.</b> Firing the index change from
    /// INSIDE the first reading reproduces the exact interleaving a preemption on a loaded shard
    /// produces — the snapshot landing between "take the reading" and "start listening" — with no
    /// sleeps, no scheduler and no load. Under the old order the emission has no observer and is
    /// dropped; under the fixed order the subscription is already live and sees it.</para>
    /// </summary>
    [Fact]
    public async Task UntilDetermined_SeesAnIndexChangeThatLandsWhileTheFirstReadingIsBeingTaken()
    {
        var indexChanged = new Subject<Unit>();   // hot + non-replaying, exactly like IndexChanged
        var node = User("rbuergi");
        var hydrated = false;

        UserIdentityLookup Lookup()
        {
            if (hydrated)
                return UserIdentityLookup.Classify(node, hydrated: true, subscriptionFailure: null);

            // The snapshot lands DURING the first reading: applied first (so a re-ask would now
            // succeed), then announced — the same order UserIdentityCache.Apply uses.
            hydrated = true;
            indexChanged.OnNext(Unit.Default);
            return UserIdentityLookup.Classify(hit: null, hydrated: false, subscriptionFailure: null);
        }

        var determined = await UserIdentityLookup.UntilDetermined(indexChanged, Lookup)
            .Timeout(TimeSpan.FromSeconds(10))
            .FirstAsync()
            .ToTask(TestContext.Current.CancellationToken);

        Assert.False(determined.IsUnavailable);
        Assert.Same(node, determined.Node);
    }

    /// <summary>
    /// The ordinary path stays ordinary: an index that is already determinate at subscribe time is
    /// answered from the first reading, without waiting for any change at all.
    /// </summary>
    [Fact]
    public async Task UntilDetermined_AnswersFromTheFirstReading_WhenTheIndexIsAlreadyDeterminate()
    {
        var node = User("rbuergi");
        var determined = await UserIdentityLookup
            .UntilDetermined(Observable.Never<Unit>(),
                () => UserIdentityLookup.Classify(node, hydrated: true, subscriptionFailure: null))
            .Timeout(TimeSpan.FromSeconds(10))
            .FirstAsync()
            .ToTask(TestContext.Current.CancellationToken);

        Assert.Same(node, determined.Node);
    }

    /// <summary>
    /// Subscribing to the change feed BEFORE taking the first reading has a cost the old
    /// <c>Defer</c>/<c>StartWith</c> shape did not: the subscription already exists when the reading
    /// runs. If that reading throws and the exception is simply allowed to propagate out of the
    /// subscribe factory, the handle is never returned to anyone — leaving a live subscription to a
    /// hot, process-wide stream that keeps re-invoking the reading for the life of the cache, with no
    /// way to dispose it. So a throwing reading must tear the feed down and surface as the sequence's
    /// own <c>OnError</c>. (Copilot review, #2259.)
    /// </summary>
    [Fact]
    public async Task UntilDetermined_AThrowingReading_FaultsTheSequence_AndLeavesNoSubscriptionBehind()
    {
        var indexChanged = new Subject<Unit>();

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await UserIdentityLookup
                .UntilDetermined(indexChanged, () => throw new InvalidOperationException("index read failed"))
                .Timeout(TimeSpan.FromSeconds(10))
                .FirstAsync()
                .ToTask(TestContext.Current.CancellationToken));

        Assert.False(indexChanged.HasObservers);
    }
}
