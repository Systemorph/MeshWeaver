using System;
using System.Collections.Generic;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using MeshWeaver.Mesh;
using Microsoft.Reactive.Testing;
using Xunit;

namespace MeshWeaver.Layout.Test;

/// <summary>
/// 🚨 THE CALLER-SIDE READ BUDGET — the seam behind issues #1563, #1693 and #1748, pinned in
/// VIRTUAL TIME.
///
/// <para><b>The shape all three share.</b> A read whose target hub is unreachable, still starting,
/// or whose reply is dropped in transit has no terminal of its own, so it inherits the hub's
/// <c>RequestTimeout</c> — 60 s, the framework's last-resort ceiling — and then reports the hub's
/// own impatience instead of what was being read. <see cref="ReadBudget"/> is the nested bound that
/// fires first and therefore gets to say which read starved.</para>
///
/// <para><b>Why virtual time.</b> These are timeout tests; run against the wall clock they would
/// either take a minute each or hang CI exactly the way the bug does. A
/// <see cref="TestScheduler"/> drives the budget deterministically and instantly — the same seam
/// <c>MessageHubGrain.BuildActivationChain</c> exposes for the identical reason.</para>
///
/// <para><b>What each disposition must NOT do</b> is as load-bearing as what it must: the failing
/// one must ERROR rather than complete (an empty completion reads as "there is genuinely nothing
/// here", which is Rx's own <c>Timeout(..., Observable.Empty)</c> trap), and the degrading one must
/// stay SUBSCRIBED (an error would tear a live binding down, so a hub that is merely slow could
/// never populate the control).</para>
/// </summary>
public class ReadBudgetTest
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(10);
    private const string Target = "Posts/RobertHaircuts";
    private const string What = "field 'authorPath'";

    private sealed record Observed<T>(List<T> Values, List<Exception> Errors, int Completions);

    private static Observed<T> Record<T>(IObservable<T> source, TestScheduler scheduler, TimeSpan advance)
    {
        var values = new List<T>();
        var errors = new List<Exception>();
        var completions = 0;
        using var subscription = source.Subscribe(values.Add, errors.Add, () => completions++);
        scheduler.AdvanceBy(advance.Ticks);
        return new Observed<T>(values, errors, completions);
    }

    // ════════════════════════════════════════════════════════════════════════════════════════
    //  FailIfNoFirstEmission — the ONE-SHOT disposition (the content route, #1563 / #1693).
    // ════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 🚨 THE WHOLE POINT. A target that never answers must terminate the read at the CALLER'S
    /// budget, not at the hub's 60 s ceiling — and the failure must name the target and the budget,
    /// because "the read gave up" is only actionable if you know which read.
    /// </summary>
    [Fact(Timeout = 30000)]
    public void ATargetThatNeverAnswers_FailsAtTheCallersBudget_NotTheHubs()
    {
        var scheduler = new TestScheduler();

        var observed = Record(
            Observable.Never<string>().FailIfNoFirstEmission(
                reader: null, Target, What, Budget, scheduler),
            scheduler,
            Budget);

        var failure = Assert.Single(observed.Errors);
        var unreachable = Assert.IsType<HubUnreachableException>(failure);
        unreachable.Target.Should().Be(Target);
        unreachable.Budget.Should().Be(Budget);
        unreachable.Message.Should().Contain(Target).And.Contain(What,
            "a bare 'no response received in hub X' names neither the read nor the file — which is "
            + "exactly why #1563 and #1748 were each filed as an unactionable single-occurrence alert");
    }

    /// <summary>
    /// It is a <see cref="TimeoutException"/> BY INHERITANCE, and that is not cosmetic: every
    /// transient-failure classifier in the framework keys on that type
    /// (<see cref="AreaErrorClassifier.IsTransientHubFailure"/>,
    /// <c>MeshNodeStreamCache.IsTransientOwnerFailure</c>, <c>RoutingGrain.IsTransientFailure</c>).
    /// A brand-new exception type would have fallen out of all three at once, turning a retryable
    /// stall into a negative-cached "missing node" — a strictly worse failure than the 60 s wait
    /// this replaces.
    /// </summary>
    [Fact(Timeout = 30000)]
    public void TheFailure_StaysClassifiedAsATransientHubMiss()
    {
        var scheduler = new TestScheduler();

        var observed = Record(
            Observable.Never<string>().FailIfNoFirstEmission(
                reader: null, Target, What, Budget, scheduler),
            scheduler,
            Budget);

        var failure = Assert.Single(observed.Errors);
        failure.Should().BeAssignableTo<TimeoutException>();
        AreaErrorClassifier.IsTransientHubFailure(failure).Should().BeTrue(
            "the retry/negative-cache classifiers must keep recognising a lapsed read budget as the "
            + "transient owner miss it is");
    }

    /// <summary>
    /// 🚨 IT ERRORS — it never completes empty. Rx's <c>Timeout(..., Observable.Empty)</c> passes a
    /// COMPLETION downstream, which a reader cannot tell apart from "there is genuinely nothing
    /// here"; a route would answer 404 for a hub that never spoke. The distinction has to survive to
    /// the caller, so the degraded read is a terminal ERROR and nothing else.
    /// </summary>
    [Fact(Timeout = 30000)]
    public void TheFailedRead_NeverCompletesEmpty()
    {
        var scheduler = new TestScheduler();

        var observed = Record(
            Observable.Never<string>().FailIfNoFirstEmission(
                reader: null, Target, What, Budget, scheduler),
            scheduler,
            Budget);

        observed.Values.Should().BeEmpty();
        observed.Completions.Should().Be(0,
            "an empty completion is indistinguishable from 'no data' — the exact silent failure "
            + "this budget exists to remove");
        observed.Errors.Should().ContainSingle();
    }

    /// <summary>
    /// Nothing fires one tick early: the budget is a deadline, not a hint. Pinning the boundary is
    /// what stops a later "make the tests faster" edit from quietly turning a bound into a race.
    /// </summary>
    [Fact(Timeout = 30000)]
    public void TheBudget_DoesNotFireBeforeItElapses()
    {
        var scheduler = new TestScheduler();

        var observed = Record(
            Observable.Never<string>().FailIfNoFirstEmission(
                reader: null, Target, What, Budget, scheduler),
            scheduler,
            Budget - TimeSpan.FromTicks(1));

        observed.Errors.Should().BeEmpty();
        observed.Completions.Should().Be(0);
    }

    /// <summary>
    /// 🚨 ONLY THE FIRST EMISSION IS BOUNDED. Rx's plain <c>Timeout(TimeSpan)</c> applies BETWEEN
    /// consecutive elements, so wiring it here would fault a perfectly healthy binding that simply
    /// has nothing new to say — turning a fix for a stall into a new source of them. Once the source
    /// speaks, the budget timer is gone and the stream flows untouched, however long the gaps.
    /// </summary>
    [Fact(Timeout = 30000)]
    public void OnceTheSourceAnswers_TheBudgetIsGone_AndAnIdleStreamSurvives()
    {
        var scheduler = new TestScheduler();
        var source = new Subject<string>();
        var values = new List<string>();
        var errors = new List<Exception>();

        using var subscription = source
            .FailIfNoFirstEmission(reader: null, Target, What, Budget, scheduler)
            .Subscribe(values.Add, errors.Add);

        scheduler.AdvanceBy(TimeSpan.FromSeconds(1).Ticks);
        source.OnNext("first");
        // Ten budgets' worth of silence — an idle live binding.
        scheduler.AdvanceBy(TimeSpan.FromSeconds(100).Ticks);
        source.OnNext("late");

        values.Should().Equal("first", "late");
        errors.Should().BeEmpty();
    }

    /// <summary>
    /// The source's OWN terminal wins when it arrives first — a routing <c>NotFound</c>, a denial, a
    /// hub NACK. The budget exists for the case where nothing arrives; it must never overwrite a
    /// real answer with "the hub did not respond", which would erase the one classification the
    /// caller can act on.
    /// </summary>
    [Fact(Timeout = 30000)]
    public void ARealFailure_IsNotReplacedByTheBudgetsOne()
    {
        var scheduler = new TestScheduler();
        var source = new Subject<string>();
        var errors = new List<Exception>();

        using var subscription = source
            .FailIfNoFirstEmission(reader: null, Target, What, Budget, scheduler)
            .Subscribe(_ => { }, errors.Add);

        scheduler.AdvanceBy(TimeSpan.FromSeconds(1).Ticks);
        source.OnError(new UnauthorizedAccessException("Access denied"));
        scheduler.AdvanceBy(TimeSpan.FromSeconds(100).Ticks);

        Assert.Single(errors).Should().BeOfType<UnauthorizedAccessException>();
    }

    // ════════════════════════════════════════════════════════════════════════════════════════
    //  DegradeIfNoFirstEmission — the LIVE-BINDING disposition (the node picker, #1748).
    // ════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 🚨 THE BINDING DRAWS, AND KEEPS TRYING. A control bound to an unreachable node used to spin
    /// for the hub's full 60 s (and, when the node was simply ABSENT, for the life of the circuit —
    /// the <c>Where(node is not null)</c> filter means the stream never emits or errors at all).
    /// Now the budget hands it the same "no value" it renders for an absent field, and the
    /// subscription survives, so the late value still lands.
    /// </summary>
    [Fact(Timeout = 30000)]
    public void ABindingThatGetsNothingInTime_DrawsEmpty_AndStillTakesTheLateValue()
    {
        var scheduler = new TestScheduler();
        var source = new Subject<object?>();
        var values = new List<object?>();
        var errors = new List<Exception>();
        var degradations = new List<HubUnreachableException>();

        using var subscription = source
            .DegradeIfNoFirstEmission<object?>(
                fallback: null, degradations.Add, reader: null, Target, What, Budget, scheduler)
            .Subscribe(values.Add, errors.Add);

        scheduler.AdvanceBy(Budget.Ticks);
        values.Should().Equal(new object?[] { null }, "the control must draw instead of spinning");
        errors.Should().BeEmpty("an error would tear the binding down and the late value would never arrive");

        source.OnNext("arrived-late");
        values.Should().Equal(new object?[] { null, "arrived-late" },
            "a cold NodeType compile legitimately outruns any interactive budget — the value must "
            + "still replace the placeholder when it lands");
    }

    /// <summary>
    /// …and it is NOT SILENT. Emitting the fallback with no record would make "the hub never
    /// answered" indistinguishable from "this field is empty" — the same collapse the failing
    /// disposition refuses. The report carries the identical typed failure, so whoever logs it names
    /// the node and the budget.
    /// </summary>
    [Fact(Timeout = 30000)]
    public void TheDegradation_IsReportedOnce_WithTheSameAttributableFailure()
    {
        var scheduler = new TestScheduler();
        var degradations = new List<HubUnreachableException>();

        using var subscription = Observable.Never<object?>()
            .DegradeIfNoFirstEmission<object?>(
                fallback: null, degradations.Add, reader: null, Target, What, Budget, scheduler)
            .Subscribe(_ => { });

        scheduler.AdvanceBy(TimeSpan.FromSeconds(120).Ticks);

        var reported = Assert.Single(degradations);
        reported.Target.Should().Be(Target);
        reported.Budget.Should().Be(Budget);
        reported.Message.Should().Contain(What);
    }

    /// <summary>
    /// A binding that answers in time is untouched: no placeholder, no report. The degradation must
    /// never race a healthy read — otherwise every slow-but-fine control would log a warning and
    /// flash empty.
    /// </summary>
    [Fact(Timeout = 30000)]
    public void AValueInTime_CancelsTheDegradationEntirely()
    {
        var scheduler = new TestScheduler();
        var source = new Subject<object?>();
        var values = new List<object?>();
        var degradations = new List<HubUnreachableException>();

        using var subscription = source
            .DegradeIfNoFirstEmission<object?>(
                fallback: null, degradations.Add, reader: null, Target, What, Budget, scheduler)
            .Subscribe(values.Add);

        scheduler.AdvanceBy((Budget - TimeSpan.FromSeconds(1)).Ticks);
        source.OnNext("value");
        scheduler.AdvanceBy(TimeSpan.FromSeconds(120).Ticks);

        values.Should().Equal("value");
        degradations.Should().BeEmpty();
    }

    /// <summary>
    /// The degrading disposition never completes either — a completed binding tells the consumer
    /// there will be no more values, which is precisely the opposite of "we are still waiting".
    /// </summary>
    [Fact(Timeout = 30000)]
    public void TheDegradedBinding_StaysOpen()
    {
        var scheduler = new TestScheduler();

        var observed = Record(
            Observable.Never<object?>().DegradeIfNoFirstEmission<object?>(
                fallback: null, _ => { }, reader: null, Target, What, Budget, scheduler),
            scheduler,
            TimeSpan.FromSeconds(120));

        observed.Completions.Should().Be(0);
        observed.Errors.Should().BeEmpty();
    }

    /// <summary>
    /// A reporting callback that throws must not swallow the placeholder it was reporting: the
    /// consumer still needs a value to draw. Logging is a side effect of the degradation, never a
    /// precondition for it.
    /// </summary>
    [Fact(Timeout = 30000)]
    public void AThrowingReport_StillLetsTheControlDraw()
    {
        var scheduler = new TestScheduler();

        var observed = Record(
            Observable.Never<object?>().DegradeIfNoFirstEmission<object?>(
                fallback: null,
                _ => throw new InvalidOperationException("logger exploded"),
                reader: null, Target, What, Budget, scheduler),
            scheduler,
            Budget);

        observed.Values.Should().Equal(new object?[] { null });
        observed.Errors.Should().BeEmpty();
    }

    // ════════════════════════════════════════════════════════════════════════════════════════
    //  The budget itself.
    // ════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// The default is the framework's EXISTING interactive read budget, not a new number: 10 s is
    /// what <c>GetMeshNode</c> has always defaulted to, and it must stay far below the 60 s hub
    /// <c>RequestTimeout</c> — that inequality is what makes this the bound that fires, and
    /// therefore the one that can say which read starved.
    /// </summary>
    [Fact(Timeout = 30000)]
    public void TheDefaultBudget_SitsWellInsideTheHubsRequestTimeout()
    {
        ReadBudget.Default.Should().Be(TimeSpan.FromSeconds(10));
        ReadBudget.Default.Should().BeLessThan(TimeSpan.FromSeconds(60));
    }

    /// <summary>
    /// A non-positive budget expires before the read is even posted, so it would report
    /// "unreachable" about a hub nobody asked. Refused loudly rather than silently treated as
    /// "immediately" — the zero-budget shape that leaked a pending SubscribeRequest in #1613.
    /// </summary>
    [Theory(Timeout = 30000)]
    [InlineData(0)]
    [InlineData(-1)]
    public void ANonPositiveBudget_IsRefused(int seconds)
    {
        var budget = TimeSpan.FromSeconds(seconds);

        Assert.Throws<ArgumentOutOfRangeException>(() => Observable.Never<string>()
            .FailIfNoFirstEmission(reader: null, Target, What, budget));

        Assert.Throws<ArgumentOutOfRangeException>(() => Observable.Never<object?>()
            .DegradeIfNoFirstEmission<object?>(
                fallback: null, _ => { }, reader: null, Target, What, budget));
    }
}
