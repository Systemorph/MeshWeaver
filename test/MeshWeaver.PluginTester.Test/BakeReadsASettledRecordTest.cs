using System;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Mesh.Services;
using MeshWeaver.PluginTester;
using Xunit;

namespace MeshWeaver.PluginTester.Test;

/// <summary>
/// MeshWeaver#3370 / #3333 — the bake must ship from a SETTLED record. It used to take the first
/// record it saw as "a replay — the compile gate already saw it settle", and the gate had not: the
/// installer's release request completes when the trigger is written, not when the compile it
/// starts has finished, so the bake read a record whose compile was still in flight — and the
/// store, which evicts superseded versions at write, no longer held the version that record named
/// ("claims a usable build at v7 but the run's assembly store has NO bytes for it", run
/// 34036079623, on a tree that already carried the #3341 stamp fix).
///
/// <para>These pin the predicate the bake now waits on: the three ways a record is not yet
/// settled, and the one shape that is.</para>
/// </summary>
public class BakeReadsASettledRecordTest
{
    private static NodeTypeDefinition Settled() => new()
    {
        CompilationStatus = CompilationStatus.Ok,
        DispatchedBuildInputs = null,
        RequestedReleaseAt = new DateTimeOffset(2026, 9, 6, 13, 30, 1, TimeSpan.Zero),
        LastReleaseRequestHandledAt = new DateTimeOffset(2026, 9, 6, 13, 30, 1, TimeSpan.Zero),
        LastCompiledVersion = 7,
    };

    [Fact]
    public void A_record_whose_compile_reached_Ok_with_nothing_in_flight_is_settled()
    {
        Assert.Null(BakeOutput.NotYetSettled(Settled()));
    }

    [Fact]
    public void A_handled_release_older_than_the_request_is_still_pending()
    {
        // The installer's release trigger landed (the observable completed) but the watcher has
        // not handled it yet — the compile it will start has not even been dispatched.
        var def = Settled() with
        {
            RequestedReleaseAt = new DateTimeOffset(2026, 9, 6, 13, 30, 2, TimeSpan.Zero),
        };
        var reason = BakeOutput.NotYetSettled(def);
        Assert.NotNull(reason);
        Assert.Contains("release request is pending", reason);
    }

    [Fact]
    public void A_compile_in_flight_is_not_settled_even_at_Ok()
    {
        // DispatchedBuildInputs is stamped at every Pending door and cleared on every terminal
        // status (#3390); while it is present the record will be re-stamped.
        var def = Settled() with { DispatchedBuildInputs = "fw=abc;mod=def;src=ghi" };
        var reason = BakeOutput.NotYetSettled(def);
        Assert.NotNull(reason);
        Assert.Contains("in flight", reason);
    }

    [Theory]
    [InlineData(CompilationStatus.Pending)]
    [InlineData(CompilationStatus.Compiling)]
    [InlineData(CompilationStatus.Error)]
    public void A_non_Ok_status_is_not_settled(CompilationStatus status)
    {
        var reason = BakeOutput.NotYetSettled(Settled() with { CompilationStatus = status });
        Assert.NotNull(reason);
        Assert.Contains(status.ToString(), reason);
    }

    [Fact]
    public void A_record_that_never_requested_a_release_is_settled_at_Ok()
    {
        var def = Settled() with { RequestedReleaseAt = null, LastReleaseRequestHandledAt = null };
        Assert.Null(BakeOutput.NotYetSettled(def));
    }
}
