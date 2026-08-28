using System;
using System.Diagnostics;
using MeshWeaver.Messaging;
using Xunit;

namespace MeshWeaver.Messaging.Hub.Test;

/// <summary>
/// The classification walker shared by <see cref="HubDisposingException.IsHubDisposal"/> and
/// MessageHub's scope-teardown classifier (review on #2604).
///
/// <para>🚨 The bug this pins is invisible to the obvious test. <c>AggregateException.InnerException</c>
/// returns <c>InnerExceptions[0]</c>, so an <c>InnerException</c>-only walk finds a
/// single-fault aggregate perfectly well and misses a multi-fault one — and which fault lands at
/// index 0 is decided by whichever branch of a reactive <c>Merge</c> faulted first. A teardown
/// racing a real error would then be classified as an init FAILURE some of the time and as
/// shutdown the rest, from the same code path.</para>
/// </summary>
public class ExceptionChainTest
{
    private sealed class Marker : Exception;

    [Fact]
    public void A_chain_of_plain_wrappers_is_walked()
        => Assert.True(ExceptionChain.Contains<Marker>(
            new InvalidOperationException("outer", new InvalidOperationException("inner", new Marker()))));

    /// <summary>The discriminator: the marker sits at index 1, where an InnerException-only walk
    /// cannot reach it — InnerException would hand back the InvalidOperationException at index 0
    /// and the walk would end there.</summary>
    [Fact]
    public void An_aggregate_carrying_the_marker_at_a_LATER_index_is_found()
    {
        var aggregate = new AggregateException(
            new InvalidOperationException("the fault that happened to arrive first"),
            new Marker());

        Assert.Same(aggregate.InnerExceptions[0], aggregate.InnerException);   // the premise
        Assert.True(ExceptionChain.Contains<Marker>(aggregate));
    }

    [Fact]
    public void A_marker_nested_under_an_aggregate_under_a_wrapper_is_found()
        => Assert.True(ExceptionChain.Contains<Marker>(
            new InvalidOperationException("outer", new AggregateException(
                new InvalidOperationException("first"),
                new InvalidOperationException("second", new Marker())))));

    [Fact]
    public void An_absent_marker_is_absent()
        => Assert.False(ExceptionChain.Contains<Marker>(
            new AggregateException(new InvalidOperationException("a"), new InvalidOperationException("b"))));

    [Fact]
    public void Null_is_not_a_match() => Assert.False(ExceptionChain.Contains<Marker>(null));

    /// <summary>
    /// 🚨 An exception graph is caller-supplied data, so the walk must terminate on one that is
    /// deep, shared or outright cyclic — cheaply, not merely eventually.
    ///
    /// <para>This test earned its keep on the first run: the walker extracted from
    /// <c>IsHubDisposal</c> pinned a core at 100% CPU here, indefinitely. A <c>for</c> loop over
    /// <c>InnerException</c> that ALSO recurses into <c>InnerExceptions</c> re-walks the same
    /// chain once per position — <c>AggregateException.InnerException</c> IS
    /// <c>InnerExceptions[0]</c> — so nested aggregates cost exponentially, and the depth cap did
    /// not help because the blow-up is in re-walking, not depth. The bound is a wall-clock one on
    /// purpose: a walker that returns the right answer in a minute is still a defect.</para>
    /// </summary>
    [Fact]
    public void A_pathologically_deep_graph_terminates_QUICKLY()
    {
        Exception deep = new InvalidOperationException("floor");
        for (var i = 0; i < 200; i++)
            deep = new AggregateException(deep);

        var started = Stopwatch.StartNew();
        Assert.False(ExceptionChain.Contains<Marker>(deep));
        Assert.True(started.Elapsed < TimeSpan.FromSeconds(1),
            $"classification took {started.Elapsed} — a linear walk over 201 exceptions is "
            + "microseconds; anything near a second means the graph is being re-walked");
    }

    /// <summary>A cyclic graph must terminate too — nothing stops a caller building one, and a
    /// visited-set walk is what makes that a non-event rather than a hang.</summary>
    [Fact]
    public void A_CYCLIC_graph_terminates()
    {
        var a = new AggregateException(new InvalidOperationException("a"));
        var cycle = new AggregateException(a, a, a);   // the same instance reached three ways

        Assert.False(ExceptionChain.Contains<Marker>(cycle));
    }

    /// <summary>The two callers must keep answering their own question — the shared walker changed
    /// how the graph is traversed, not what counts as a match.</summary>
    [Fact]
    public void IsHubDisposal_still_recognises_only_hub_disposal()
    {
        Assert.True(HubDisposingException.IsHubDisposal(
            new AggregateException(new Marker(), new HubDisposingException(new Address("test", "1"), "what"))));
        Assert.False(HubDisposingException.IsHubDisposal(new AggregateException(new Marker())));
    }
}
