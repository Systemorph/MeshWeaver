using MeshWeaver.Blazor.Infrastructure;
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
}
