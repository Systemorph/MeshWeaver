using System;
using System.Reactive;
using System.Reactive.Linq;
using MeshWeaver.Fixture;
using MeshWeaver.Messaging;
using Xunit;

namespace MeshWeaver.Messaging.Hub.Test;

/// <summary>
/// Pins that a hub whose initialization HANGS names the BuildupAction it was waiting on — by
/// position and by the method behind the delegate — instead of the sentence that used to stand
/// there: <i>"a BuildupAction did not complete within 120s (a hung dependency or stuck compile)"</i>,
/// two candidates and no way to tell which of the hub's actions was the one still pending
/// (issue #2886, the same defect class #1122 fixed one layer down in <c>DataContext</c>).
///
/// <para><b>Why this line has to carry it.</b> A hub's BuildupAction <c>Concat</c> is the ONE thing
/// this layer can see: the enclosing waits know only that initialization ran out of time, and the
/// waits nested below it are other hubs' business. Which level of a nested initialization gets to
/// report at all is a separate property, held by the contracting rungs in
/// <c>HubInitializationBudget</c> (<c>Doc/Architecture/InitializationBudgetLadder</c>) — this test
/// pins what the line SAYS once it fires, not which line fires.</para>
///
/// <para><b>Both sides.</b> The action that hangs is the SECOND of two, and the first is a method
/// group with a name, so the assertion distinguishes "named the right one" from "named one":
/// <c>2 of 2 (…HangsForever)</c> and NOT <c>CompletesAtOnce</c>. RED on the pre-fix code: the
/// sentence carries neither the position nor the name.</para>
/// </summary>
public class InitializationTimeoutNamesThePendingActionTest(ITestOutputHelper output) : HubTestBase(output)
{
    private record ProbeRequest : IRequest<ProbeResponse>;
    private record ProbeResponse;

    /// <summary>The first action — signals immediately, so the Concat moves past it.</summary>
    private static IObservable<Unit> CompletesAtOnce(IMessageHub _) => Observable.Return(Unit.Default);

    /// <summary>The second action — never emits and never completes: the hang under test.</summary>
    private static IObservable<Unit> HangsForever(IMessageHub _) => Observable.Never<Unit>();

    /// <summary>The startup bound the hub under test is given — short, from the shared budgets.</summary>
    private static TimeSpan StartupBound => TestTimeouts.Quick / 2;

    protected override MessageHubConfiguration ConfigureHost(MessageHubConfiguration configuration)
        => configuration
            .WithStartupTimeout(StartupBound)
            .WithHandler<ProbeRequest>((hub, request) =>
            {
                hub.Post(new ProbeResponse(), o => o.ResponseFor(request));
                return request.Processed();
            })
            .WithInitialization(CompletesAtOnce)
            .WithInitialization(HangsForever);

    [Fact]
    public async Task AHungInitialization_NamesThePendingAction_ByPositionAndByMethod()
    {
        var host = GetHost();
        var client = GetClient();

        // The FAILED hub answers with a typed DeliveryFailure carrying the init error; the outer
        // wait is the shared budget, comfortably past the hub's own StartupBound.
        var act = () => client
            .Observe(new ProbeRequest(), o => o.WithTarget(host.Address))
            .FirstAsync().Timeout(TestTimeouts.Quick).Await(TestContext.Current.CancellationToken);

        var ex = (await act.Should().ThrowAsync<Exception>(
            "a hub whose init hung must answer requests with its init error, not hang")).Which;
        ex.Should().NotBeOfType<TimeoutException>(
            "the FAILED hub must answer FAST — a TimeoutException here means the gate never opened");

        var error = ((MessageHub)host).InitializationError;
        error.Should().NotBeNull("the hub must record the init failure");
        var reason = error!.Message;

        reason.Should().Contain("2 of 2",
            "the line names WHICH action was pending, by position in the Concat");
        reason.Should().Contain(nameof(HangsForever),
            "the line names the method behind the pending delegate");
        reason.Should().NotContain(nameof(CompletesAtOnce),
            "the action that signalled is not the one the hub was waiting on");
        reason.Should().Contain("did not complete within",
            "it is still reported as a hang, not as a fault");
        reason.Should().NotContain("a hung dependency or stuck compile",
            "the two-candidates guess is gone; the line says what it measured");
    }
}
