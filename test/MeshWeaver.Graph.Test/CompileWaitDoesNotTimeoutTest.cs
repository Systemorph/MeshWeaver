using System;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using Microsoft.Reactive.Testing;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// Deterministic (virtual-time) pin for the fresh-pod wedge fix
/// (<see cref="NodeTypeEnrichmentHelpers.WaitForCompileSettled"/>).
///
/// <para>The wedge: on a fresh pod every dynamic NodeType recompiles from source
/// at once. A user request that activates a per-instance hub while that NodeType is
/// still mid-compile used to hit a flat <c>SlowPathTimeout</c> wall-clock — it
/// expired mid-compile, cached the compilation-error overlay onto the instance hub
/// for its whole lifetime, and only a manual recycle could heal it.</para>
///
/// <para>The fix: a compile-in-progress is a WAIT, not a fault. Once a
/// <c>Pending</c>/<c>Compiling</c> state is observed the wall-clock is DISARMED and
/// the wait is bounded by the compile FINISHING (RunCompile always writes a terminal
/// Ok/Error). The wall-clock still bounds the genuine "no compile is coming" case so
/// a truly stuck/misconfigured type surfaces the diagnostic (the graceful sink).</para>
///
/// <para>Virtual time via <see cref="TestScheduler"/> — no <c>Task.Delay</c>, no real
/// Roslyn compile; the scheduler lets a compile "take" far longer than the budget in
/// zero wall-clock so the assertion is a pure state race, not a timing race.</para>
/// </summary>
public class CompileWaitDoesNotTimeoutTest
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(60);

    private static MeshNode Node(CompilationStatus? status, bool withAssembly = false,
        string? configuration = null)
        => new("DynamicType", "Test")
        {
            Content = new NodeTypeDefinition
            {
                CompilationStatus = status,
                Configuration = configuration,
                LatestAssemblyCollection = withAssembly ? "coll" : null,
                LatestAssemblyPath = withAssembly ? "path" : null,
            }
        };

    /// <summary>The BuildSlowPath terminal predicate, in miniature: Ok-with-assembly or Error.</summary>
    private static bool IsSettled(MeshNode n)
        => n.Content is NodeTypeDefinition d
            && ((d.CompilationStatus == CompilationStatus.Ok
                    && !string.IsNullOrEmpty(d.LatestAssemblyCollection)
                    && !string.IsNullOrEmpty(d.LatestAssemblyPath))
                || d.CompilationStatus == CompilationStatus.Error);

    private static IObservable<MeshNode> Settled(IObservable<MeshNode> typeStream)
        => typeStream.Where(IsSettled).Take(1);

    /// <summary>
    /// THE Part-2 contract: while a compile is in flight the wall-clock is disarmed —
    /// the wait outlasts many multiples of the no-progress budget and only resolves
    /// when the compile finally settles Ok. Before the fix the flat 60 s timeout fired
    /// and cached the overlay; here we advance 5× the budget with the type still
    /// Compiling and NOTHING resolves until the Ok write lands.
    /// </summary>
    [Fact]
    public void InFlightCompile_WaitsPastWallClock_ThenEmitsOnTerminalOk()
    {
        var scheduler = new TestScheduler();
        var typeStream = new Subject<MeshNode>();

        MeshNode? emitted = null;
        Exception? error = null;
        NodeTypeEnrichmentHelpers
            .WaitForCompileSettled(typeStream, Settled(typeStream), Budget,
                () => new TimeoutException("no compile in flight"), scheduler)
            .Subscribe(n => emitted = n, ex => error = ex);

        // A compile is genuinely in flight — this DISARMS the wall-clock.
        typeStream.OnNext(Node(CompilationStatus.Compiling));

        // Advance FIVE budgets of virtual time. A flat wall-clock would have fired at
        // one budget and cached the overlay; the disarmed timer must stay silent.
        scheduler.AdvanceBy(Budget.Ticks * 5);
        Assert.Null(error);
        Assert.Null(emitted);

        // The compile finally settles Ok — NOW the wait resolves.
        var ok = Node(CompilationStatus.Ok, withAssembly: true);
        typeStream.OnNext(ok);

        Assert.Null(error);
        Assert.Same(ok, emitted);
    }

    /// <summary>
    /// An in-flight compile that fails LATE still surfaces the Error deterministically
    /// (never a hang) so the caller can overlay the diagnostics — the
    /// GrainActivationFailureRegistry/error path stays intact.
    /// </summary>
    [Fact]
    public void InFlightCompile_ThatFailsLate_SurfacesError_NotHang()
    {
        var scheduler = new TestScheduler();
        var typeStream = new Subject<MeshNode>();

        MeshNode? emitted = null;
        Exception? error = null;
        NodeTypeEnrichmentHelpers
            .WaitForCompileSettled(typeStream, Settled(typeStream), Budget,
                () => new TimeoutException("no compile in flight"), scheduler)
            .Subscribe(n => emitted = n, ex => error = ex);

        typeStream.OnNext(Node(CompilationStatus.Pending));     // disarm
        scheduler.AdvanceBy(Budget.Ticks * 3);
        Assert.Null(error);
        Assert.Null(emitted);

        var failed = Node(CompilationStatus.Error);
        typeStream.OnNext(failed);

        Assert.Null(error);                     // the Error node is a VALUE, not a fault
        Assert.Same(failed, emitted);           // caller applies the compilation-error overlay
    }

    /// <summary>
    /// The graceful sink for the genuine "no compile is coming" case: a type stuck at
    /// a non-settled, non-in-flight state (source present but no compile ever kicked)
    /// still faults after the budget so the caller overlays a diagnostic — never an
    /// infinite silent hang.
    /// </summary>
    [Fact]
    public void NoCompileEverStarts_TimesOut_ForGracefulSink()
    {
        var scheduler = new TestScheduler();
        var typeStream = new Subject<MeshNode>();

        MeshNode? emitted = null;
        Exception? error = null;
        NodeTypeEnrichmentHelpers
            .WaitForCompileSettled(typeStream, Settled(typeStream), Budget,
                () => new TimeoutException("no compile in flight"), scheduler)
            .Subscribe(n => emitted = n, ex => error = ex);

        // Neither settled nor in-flight: sits waiting for a compile that never starts.
        typeStream.OnNext(Node(status: null, configuration: "config => config"));

        scheduler.AdvanceBy(Budget.Ticks + 1);

        Assert.Null(emitted);
        Assert.IsType<TimeoutException>(error);
    }

    /// <summary>
    /// A NodeType that is already settled Ok at first observation resolves immediately
    /// — the wall-clock never even starts to matter.
    /// </summary>
    [Fact]
    public void AlreadySettled_EmitsImmediately_NoTimeout()
    {
        var scheduler = new TestScheduler();
        var typeStream = new Subject<MeshNode>();

        MeshNode? emitted = null;
        Exception? error = null;
        NodeTypeEnrichmentHelpers
            .WaitForCompileSettled(typeStream, Settled(typeStream), Budget,
                () => new TimeoutException("no compile in flight"), scheduler)
            .Subscribe(n => emitted = n, ex => error = ex);

        var ok = Node(CompilationStatus.Ok, withAssembly: true);
        typeStream.OnNext(ok);

        Assert.Same(ok, emitted);
        Assert.Null(error);

        // Even long past the budget nothing else fires (the timer was unsubscribed).
        scheduler.AdvanceBy(Budget.Ticks * 5);
        Assert.Null(error);
    }
}
