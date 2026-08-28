#pragma warning disable CS1591

using System;
using System.Collections.Generic;
using System.Reactive;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using MeshWeaver.Hosting.AspNetCore.Portal;
using Xunit;

namespace MeshWeaver.Hosting.Monolith.Test;

/// <summary>
/// The two temporal guarantees of <see cref="UserIdentityLookup.UntilDetermined"/>, which were the
/// point of the subscribe-before-read change and had no test (MeshWeaver#2302, finding 10).
///
/// <para>Both are pure: a <see cref="Subject{T}"/> for the hot feed and a lambda for the lookup —
/// no mesh, hub or query subscription, exactly as that method's own remarks promise. That matters
/// because the window under test is one a live-mesh test cannot hold open.</para>
///
/// <para>🚨 Without these, a future rewrite that restores the tempting <c>Defer</c>/<c>StartWith</c>
/// shape — read, then subscribe — reintroduces a 60-second hang for any visitor whose index
/// snapshot lands in that gap, and every build stays green.</para>
/// </summary>
public class UntilDeterminedTest
{
    /// <summary>
    /// An index snapshot that is applied WHILE the initial reading is being taken must still be
    /// delivered.
    ///
    /// <para>The lookup pushes the feed on its first call, which places the emission precisely in
    /// the gap the read-then-subscribe order leaves open: with the correct order the subscription
    /// already exists and the emission is seen; with the old order it is published to nobody and
    /// is unrecoverable, so the sequence never produces a determinate answer.</para>
    /// </summary>
    [Fact]
    public void An_emission_during_the_initial_lookup_is_still_delivered()
    {
        var feed = new Subject<Unit>();
        var calls = 0;

        UserIdentityLookup Lookup()
        {
            calls++;
            if (calls == 1)
            {
                // Applied *during* the initial read — the exact race this method exists to survive.
                feed.OnNext(Unit.Default);
                return UserIdentityLookup.Unavailable("index not hydrated");
            }
            return UserIdentityLookup.Unknown;
        }

        var seen = new List<UserIdentityLookup>();
        var completed = false;
        using var sub = UserIdentityLookup.UntilDetermined(feed, Lookup)
            .Subscribe(seen.Add, () => completed = true);

        Assert.True(
            completed,
            "UntilDetermined never produced a determinate answer. The snapshot applied during the "
            + "initial lookup was published to a feed nobody had subscribed to yet — the "
            + "read-then-subscribe order this method was changed away from. A visitor in that "
            + "window waits out the full request budget.");
        Assert.Single(seen);
        Assert.False(seen[0].IsUnavailable);
    }

    /// <summary>
    /// "Unknown" is a determinate answer and must terminate the sequence — continuing to listen
    /// would hold a subscription to a hot, process-wide stream open for every never-onboarded
    /// visitor.
    /// </summary>
    [Fact]
    public void Unknown_is_an_answer_and_ends_the_subscription()
    {
        var feed = new Subject<Unit>();
        var completed = false;
        using var sub = UserIdentityLookup.UntilDetermined(feed, () => UserIdentityLookup.Unknown)
            .Subscribe(_ => { }, () => completed = true);

        Assert.True(completed, "Unknown must terminate UntilDetermined, not keep listening.");
        Assert.False(feed.HasObservers, "the feed subscription must be released once determined");
    }

    /// <summary>
    /// A throwing initial lookup must tear the feed subscription down before surfacing the fault.
    ///
    /// <para>Subscribing FIRST is what creates this obligation: the subscription already exists when
    /// the reading throws, so simply letting the exception escape would leave a live subscription to
    /// a hot, process-wide stream that nobody holds — re-invoking the lookup for the life of the
    /// cache. <c>HasObservers</c> is the assertion that the teardown actually happened; asserting
    /// only the OnError would pass while the leak remained.</para>
    /// </summary>
    [Fact]
    public void A_throwing_initial_lookup_disposes_the_feed_subscription()
    {
        var feed = new Subject<Unit>();
        var boom = new InvalidOperationException("index read failed");

        Exception? caught = null;
        using var sub = UserIdentityLookup.UntilDetermined(feed, () => throw boom)
            .Subscribe(_ => { }, ex => caught = ex);

        Assert.Same(boom, caught);
        Assert.False(
            feed.HasObservers,
            "the hot-feed subscription outlived a throwing initial lookup — it would re-invoke the "
            + "lookup for the life of the cache with nobody holding the result");
    }
}
