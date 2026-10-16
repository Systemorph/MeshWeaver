#pragma warning disable CS1591

using System;
using System.Collections.Immutable;
using System.Reactive;
using System.Reactive.Linq;
using Memex.Portal.Shared.Api;
using Microsoft.AspNetCore.Http;
using Microsoft.Reactive.Testing;
using Xunit;

namespace Memex.Portal.Shared.Test;

/// <summary>
/// MeshWeaver#6790 — <c>POST /api/plugins/combo-verification</c> let its 30 s deadline escape as a
/// bare <see cref="TimeoutException"/>, which faulted the endpoint's task into an unhandled 500
/// naming neither the route nor which wait had stalled. Measured on the incident: the CI verifier's
/// POST answered 500 after 31 s and the node's version history shows the verdict was not written.
///
/// <para>These drive the endpoint's own composition (<see cref="ReleaseGateEndpoints.RecordAndConfirm"/>)
/// on VIRTUAL time, so the deadline expires deterministically and without a wall-clock wait: a
/// write that never completes answers 504 with phase <c>write</c>; a write that completes but is never
/// confirmed answers 504 with phase <c>confirm</c>. Controls: a confirmed write answers its own result
/// before the deadline; a fault that is NOT a deadline still propagates; and a
/// <see cref="TimeoutException"/> raised UPSTREAM (not this route's own timer) also still propagates —
/// the fix turns one designed outcome into a response, it does not swallow faults.</para>
/// </summary>
public class ComboVerificationDeadlineTest
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(30);

    private static (ImmutableList<IResult> Results, Exception? Error, bool Completed) Run(
        TestScheduler scheduler, IObservable<Unit> write, Func<IObservable<IResult>> confirmed, TimeSpan advance)
    {
        var results = ImmutableList<IResult>.Empty;
        Exception? error = null;
        var completed = false;
        using var _ = ReleaseGateEndpoints.RecordAndConfirm(
                write, confirmed, Deadline, scheduler,
                phase => Results.Json(new { phase },
                    statusCode: StatusCodes.Status504GatewayTimeout))
            .Subscribe(r => results = results.Add(r), ex => error = ex, () => completed = true);
        scheduler.AdvanceBy(advance.Ticks);
        return (results, error, completed);
    }

    private static string? PhaseOf(IResult result) =>
        result is IValueHttpResult { Value: { } value }
            ? value.GetType().GetProperty("phase")?.GetValue(value) as string
            : null;

    private static int? StatusOf(IResult result) =>
        (result as IStatusCodeHttpResult)?.StatusCode;

    [Fact]
    public void A_write_that_never_completes_answers_504_naming_the_write_phase()
    {
        var scheduler = new TestScheduler();
        var (results, error, completed) = Run(scheduler, Observable.Never<Unit>(),
            () => Observable.Never<IResult>(), Deadline + TimeSpan.FromSeconds(1));

        error.Should().BeNull("an expired deadline is a designed outcome, answered — never an unhandled fault (#6790)");
        completed.Should().BeTrue();
        results.Should().ContainSingle();
        StatusOf(results[0]).Should().Be(StatusCodes.Status504GatewayTimeout);
        PhaseOf(results[0]).Should().Be(ReleaseGateEndpoints.WritePhase,
            "the write had not completed when the deadline expired, so the answer names that phase "
            + "(the verdict's outcome is unknown — a queued write may still land)");
    }

    [Fact]
    public void A_write_that_completes_but_is_never_confirmed_answers_504_naming_the_confirm_phase()
    {
        var scheduler = new TestScheduler();
        var (results, error, _) = Run(scheduler, Observable.Return(Unit.Default),
            () => Observable.Never<IResult>(), Deadline + TimeSpan.FromSeconds(1));

        error.Should().BeNull();
        results.Should().ContainSingle();
        StatusOf(results[0]).Should().Be(StatusCodes.Status504GatewayTimeout);
        PhaseOf(results[0]).Should().Be(ReleaseGateEndpoints.ConfirmPhase);
    }

    [Fact]
    public void Control_a_confirmed_write_answers_its_own_result_before_the_deadline()
    {
        var scheduler = new TestScheduler();
        var (results, error, completed) = Run(scheduler, Observable.Return(Unit.Default),
            () => Observable.Return(Results.Ok(new { recorded = true })), TimeSpan.FromSeconds(1));

        error.Should().BeNull();
        completed.Should().BeTrue();
        results.Should().ContainSingle();
        StatusOf(results[0]).Should().Be(StatusCodes.Status200OK);
    }

    [Fact]
    public void Control_a_fault_that_is_not_a_deadline_still_propagates()
    {
        var scheduler = new TestScheduler();
        var (results, error, _) = Run(scheduler,
            Observable.Throw<Unit>(new InvalidOperationException("owner refused the write")),
            () => Observable.Never<IResult>(), TimeSpan.FromSeconds(1));

        results.Should().BeEmpty();
        error.Should().BeOfType<InvalidOperationException>(
            "only the deadline is answered; any other fault is a defect and reaches the exception handler");
    }

    [Fact]
    public void Control_an_upstream_TimeoutException_is_not_mistaken_for_this_routes_deadline()
    {
        var scheduler = new TestScheduler();
        var (results, error, _) = Run(scheduler,
            Observable.Throw<Unit>(new TimeoutException("the write's own initial-state bound expired")),
            () => Observable.Never<IResult>(), TimeSpan.FromSeconds(1));

        results.Should().BeEmpty(
            "only this route's own timer yields the 504; an upstream TimeoutException is a fault");
        error.Should().BeOfType<TimeoutException>();
    }
}
