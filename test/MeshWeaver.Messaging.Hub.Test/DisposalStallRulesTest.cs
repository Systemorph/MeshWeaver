using MeshWeaver.Fixture;
using Xunit;

namespace MeshWeaver.Messaging.Hub.Test;

/// <summary>
/// The watchdog fired while a quiesce drain was still in flight and the hub then completed its
/// disposal normally (#6156). A drain that began since the last look is progress; the same drain stuck
/// across the whole budget is not, and must still be reported.
/// </summary>
public class DisposalStallRulesTest
{
    [Fact]
    public void ADrainInFlightThatBeganSinceTheLastLook_IsProgress()
        => DisposalStallRules.InFlightDrainIsProgress(
                drainsInFlight: 1, currentTurn: null, drainsStartedNow: 5, drainsStartedAtLastLook: 4)
            .Should().BeTrue("a drain that started in the window means the pump is cycling toward Dead");

    [Fact]
    public void TheSameDrainInFlightAcrossTheWholeBudget_IsNotProgress_AndStillFires()
        => DisposalStallRules.InFlightDrainIsProgress(
                drainsInFlight: 1, currentTurn: null, drainsStartedNow: 4, drainsStartedAtLastLook: 4)
            .Should().BeFalse("negative control: a drain that never advanced past the budget is a stuck pump");

    [Fact]
    public void NoDrainInFlight_IsNotProgress()
        => DisposalStallRules.InFlightDrainIsProgress(
                drainsInFlight: 0, currentTurn: null, drainsStartedNow: 9, drainsStartedAtLastLook: 4)
            .Should().BeFalse("with nothing running, a started drain that already ended proves nothing now");

    [Fact]
    public void ATurnOnTheBlock_IsJudgedByItsOwnAge_NotByTheDrain()
        => DisposalStallRules.InFlightDrainIsProgress(
                drainsInFlight: 1, currentTurn: "DataChangedEvent", drainsStartedNow: 9, drainsStartedAtLastLook: 4)
            .Should().BeFalse("a wedged turn must still reach the wedge verdicts");

    [Fact]
    public void WithoutABaseline_IsNotProgress()
        => DisposalStallRules.InFlightDrainIsProgress(
                drainsInFlight: 1, currentTurn: null, drainsStartedNow: 9, drainsStartedAtLastLook: -1)
            .Should().BeFalse("a pump the detector could not read must not be waved through");
}
