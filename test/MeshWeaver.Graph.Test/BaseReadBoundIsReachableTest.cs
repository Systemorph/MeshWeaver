using System;
using System.Collections.Generic;
using System.Reactive.Linq;
using MeshWeaver.Data;
using MeshWeaver.Mesh;
using Microsoft.Reactive.Testing;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// 🚨 A write's base read must reach a terminal even when its mirror never stops talking —
/// issue #2543, the sibling #3001 left open.
///
/// <para><b>The defect.</b> Both cross-hub write paths composed the base read as
/// <c>mirror.Timeout(30s).Where(c =&gt; c.Value is not null).Select(...)</c>: the bound applied to the
/// RAW mirror, upstream of the filter. Rx's <c>Timeout(TimeSpan)</c> is an INTER-EMISSION deadline —
/// it restarts on every <c>OnNext</c> it sees — so a mirror that keeps emitting change items whose
/// <c>Value</c> is <c>null</c> resets that clock forever while the filter discards every one of
/// them. The subscriber downstream never runs, and the bound written to rescue it can never fire.
/// The comment it sat under said the opposite: "a 30 s outer timeout bounds the wait so a missing
/// per-node hub surfaces with a precise TimeoutException".</para>
///
/// <para><b>Why silence and not a slow write.</b> <c>UpdateRemote</c> raises EVERY verdict a caller
/// can receive from inside the base read's <c>onNext</c>/<c>onError</c> — including the outer
/// <c>VERDICT_TIMEOUT</c>, which is armed inside the response wait a write with no base never
/// reaches. So this is not a late write: no patch is posted, nothing is logged, and the writer parks
/// for the life of the process. <c>RequireBaseState</c> (#3001) closes the case where the mirror
/// COMPLETES having carried nothing; this test covers the case where it never completes.</para>
///
/// <para><b>What it cost.</b> The CD bake+seal gate's release wave is
/// <c>nodeTypePaths.Select(ObserveNodeTypeRelease).Merge().ToList()</c> — no per-leg bound and no
/// outer bound — so one parked leg parks the whole package install until the gate's own 600 s
/// <c>InstallTimeout</c> reports <c>install: TimeoutException</c> against a package that installed
/// in 111 s eight minutes earlier (Doc/Architecture/BakeSealNodeOpsSaturation).</para>
///
/// <para>Deterministic by construction: a <see cref="TestScheduler"/> supplies virtual time, so the
/// assertions are about ORDER and CAUSE, never about wall-clock duration. No mesh, no cluster,
/// no sleep.</para>
/// </summary>
public class BaseReadBoundIsReachableTest
{
    private const string Path = "Hosting/LogEntry";

    /// <summary>What a subscriber actually observed — all three Rx terminations, separately.</summary>
    private sealed record Observed(List<MeshNode> Values, Exception? Error, bool Completed);

    private static Observed Run(
        Func<TestScheduler, IObservable<ChangeItem<MeshNode>>> mirror,
        TimeSpan runFor)
    {
        var scheduler = new TestScheduler();
        var values = new List<MeshNode>();
        Exception? error = null;
        var completed = false;

        // `.Take(1)` exactly as BOTH production call sites compose it — directly at the
        // WriteViaSyncStream site, and inside RebaseSource at the UpdateRemote one. A write wants ONE
        // base; without it the bound would keep running against a mirror the writer stopped reading.
        using var subscription = MeshNodeStreamHandle
            .BaseStateSource(mirror(scheduler), scheduler)
            .Take(1)
            .Subscribe(values.Add, ex => error = ex, () => completed = true);

        scheduler.AdvanceBy(runFor.Ticks);
        return new Observed(values, error, completed);
    }

    /// <summary>A change item the null-filter drops — the mirror is talking, carrying nothing.</summary>
    private static ChangeItem<MeshNode> Empty() => new(null, StreamId: Path, Version: 0);

    /// <summary>A change item that carries the node — a usable base.</summary>
    private static ChangeItem<MeshNode> Carrying(long version) =>
        new(new MeshNode("LogEntry", "Hosting") { Version = version }, StreamId: Path, Version: version);

    /// <summary>
    /// 🚨 THE REGRESSION. The mirror never stops emitting, and never carries the node. RED before
    /// the fix: the inter-emission timer was reset by every dropped emission, so nothing was ever
    /// raised and the writer parked forever. GREEN: the bound measures the wait the caller actually
    /// has — for a USABLE base — and faults on it.
    /// </summary>
    [Fact]
    public void MirrorThatNeverCarriesTheNode_FaultsOnTheBound_InsteadOfParkingForever()
    {
        // One null-valued change every 10 virtual seconds — three of them inside a 30 s bound, so
        // an upstream (pre-filter) timer is reset twice and can never expire.
        var observed = Run(
            s => Observable.Interval(TimeSpan.FromSeconds(10), s).Select(_ => Empty()),
            runFor: TimeSpan.FromSeconds(120));

        observed.Values.Should().BeEmpty("no emission ever carried the node, so there is no base");
        observed.Completed.Should().BeFalse(
            "a live mirror does not end, and a fault is not a completion — that is the whole point");
        observed.Error.Should().BeOfType<TimeoutException>(
            "the bound must measure the wait for a USABLE base; applied upstream of the null-filter "
            + "it was reset by every emission the filter dropped and could never fire, so the write "
            + "raised no terminal at all and its caller parked for the life of the process (#2543)");
    }

    /// <summary>
    /// The bound is a bound on the WAIT, never on the mirror's chatter: a mirror that talks
    /// constantly and eventually carries the node hands that node through, with no fault.
    /// </summary>
    [Fact]
    public void MirrorThatEventuallyCarriesTheNode_EmitsIt_WithNoFault()
    {
        var observed = Run(
            s => Observable.Interval(TimeSpan.FromSeconds(10), s)
                .Select(tick => tick == 1 ? Carrying(7) : Empty()),
            runFor: TimeSpan.FromSeconds(120));

        observed.Error.Should().BeNull(
            "a usable base arrived at 20 s, inside the 30 s wait — the bound is on the wait for a "
            + "base, and it was satisfied");
        observed.Values.Should().ContainSingle().Which.Version.Should().Be(7,
            "the write diffs against the state the mirror actually carried");
    }

    /// <summary>
    /// A SILENT mirror still faults on the bound — the behaviour the old composition did deliver,
    /// pinned so the reorder cannot regress it.
    /// </summary>
    [Fact]
    public void SilentMirror_StillFaultsOnTheBound()
    {
        var observed = Run(
            _ => Observable.Never<ChangeItem<MeshNode>>(),
            runFor: TimeSpan.FromSeconds(120));

        observed.Values.Should().BeEmpty();
        observed.Error.Should().BeOfType<TimeoutException>(
            "a mirror that says nothing at all was already bounded, and must stay bounded");
    }
}
