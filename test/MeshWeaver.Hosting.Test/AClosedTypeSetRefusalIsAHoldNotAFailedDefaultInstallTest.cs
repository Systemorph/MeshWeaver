using System;
using MeshWeaver.PluginCatalog;
using Xunit;

namespace MeshWeaver.Hosting.Test;

/// <summary>
/// 🚨 On a host serving a CLOSED type set, a package whose root type the image does not register
/// (<c>Store/Plugin</c> on the control instance) can never install there. That is a HOLD, never a
/// failure: measured on the control instance, Explorer and Spaces logged an Error each plus the
/// "reconciled with FAILURES" roll-up on every pass, none of it actionable. The same message on an
/// OPEN mesh is a real defect and stays a failure.
/// </summary>
public class AClosedTypeSetRefusalIsAHoldNotAFailedDefaultInstallTest
{
    private static InvalidOperationException Refusal(string type = "Store/Plugin") =>
        new($"Install of 'Explorer' failed: NodeType '{type}' is not registered");

    [Fact]
    public void AnUnregisteredRootType_OnAClosedTypeSet_IsAHold_NamingTheType()
    {
        var reason = InstanceAutoRegistrationService.StandingTypeSetHold(Refusal(), closedTypeSet: true);
        Assert.NotNull(reason);
        Assert.Contains("'Store/Plugin'", reason);
        Assert.Contains("closed type set", reason);
    }

    [Fact]
    public void TheSameRefusal_OnAnOpenMesh_IsARealFailure()
    {
        // Negative control: a missing dependency on an open mesh is a defect, still an Error.
        Assert.Null(InstanceAutoRegistrationService.StandingTypeSetHold(Refusal(), closedTypeSet: false));
    }

    [Fact]
    public void AnyOtherFailure_OnAClosedTypeSet_IsStillAFailure()
    {
        Assert.Null(InstanceAutoRegistrationService.StandingTypeSetHold(
            new InvalidOperationException("Install of 'Explorer' failed: the registry answered 500"), closedTypeSet: true));
        Assert.Null(InstanceAutoRegistrationService.StandingTypeSetHold(new TimeoutException(), closedTypeSet: true));
    }

    [Fact]
    public void AWrappedRefusal_IsAHold_ButARealFailureBesideOneIsNot()
    {
        Assert.NotNull(InstanceAutoRegistrationService.StandingTypeSetHold(
            new InvalidOperationException("install failed", Refusal()), closedTypeSet: true));
        Assert.NotNull(InstanceAutoRegistrationService.StandingTypeSetHold(
            new AggregateException(Refusal(), Refusal("Store/Core")), closedTypeSet: true));
        Assert.Null(InstanceAutoRegistrationService.StandingTypeSetHold(
            new AggregateException(Refusal(), new InvalidOperationException("boom")), closedTypeSet: true));
    }

    [Fact]
    public void AHeldPackage_IsNotAFailure_SoThePassReadsClean()
    {
        var summary = DefaultInstallSummary.Empty with { Held = [new DefaultInstallHold("Explorer", "closed type set")] };
        Assert.Equal(0, summary.Failed);
        Assert.Null(InstanceAutoRegistrationService.LeftUndone(summary));
    }

    [Fact]
    public void ARealFailure_IsStillCountedAndLeavesRepairOwed()
    {
        var summary = new DefaultInstallSummary(0, 0, 1, ["Explorer"]) { Failures = ["Explorer"] };
        Assert.Equal(1, summary.Failed);
        Assert.NotNull(InstanceAutoRegistrationService.LeftUndone(summary));
    }
}
