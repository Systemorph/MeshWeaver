using System;
using MeshWeaver.PluginCatalog;
using Xunit;

namespace MeshWeaver.Hosting.Test;

/// <summary>
/// 🚨 A default-install refusal that is a fact about the running PLATFORM — the version the source
/// serves needs a newer one, or the module's bytes do not link against it — is a HOLD, never a
/// failure. Measured on a local instance running 4.0.14179: 9 + 11 packages reported as
/// "installing package failed" Errors plus the aggregate "reconciled with FAILURES" Error on every
/// boot, none of it actionable but "update the platform".
/// </summary>
public class APlatformRefusalIsAHoldNotAFailedDefaultInstallTest
{
    [Fact]
    public void APackageFloorRefusal_IsAHold_CarryingTheRefusalsOwnWords()
    {
        var reason = InstanceAutoRegistrationService.StandingPlatformHold(
            new PackagePlatformFloorException("Package 'X' was not installed: needs platform >= 4.0.14289"));
        Assert.Equal("Package 'X' was not installed: needs platform >= 4.0.14289", reason);
    }

    [Fact]
    public void AModuleLandingRefusal_IsAHold()
    {
        var reason = InstanceAutoRegistrationService.StandingPlatformHold(
            new ModuleLandingRefusedException("Module 'M' refused: references a surface this platform lacks"));
        Assert.Contains("Module 'M' refused", reason);
    }

    [Fact]
    public void ARefusalWrappedByAnInnerOrAggregateException_IsStillAHold()
    {
        var inner = new PackagePlatformFloorException("floor");
        Assert.Equal("floor", InstanceAutoRegistrationService.StandingPlatformHold(
            new InvalidOperationException("install failed", inner)));
        Assert.Equal("floor", InstanceAutoRegistrationService.StandingPlatformHold(new AggregateException(inner)));
    }

    [Fact]
    public void ARealFailure_IsNotAHold()
    {
        Assert.Null(InstanceAutoRegistrationService.StandingPlatformHold(new InvalidOperationException("boom")));
        Assert.Null(InstanceAutoRegistrationService.StandingPlatformHold(new TimeoutException()));
        // A real failure beside a hold stays a failure; all-holds is a hold.
        Assert.Null(InstanceAutoRegistrationService.StandingPlatformHold(
            new AggregateException(new PackagePlatformFloorException("b"), new InvalidOperationException("a"))));
        Assert.Equal("b", InstanceAutoRegistrationService.StandingPlatformHold(
            new AggregateException(new PackagePlatformFloorException("b"), new ModuleLandingRefusedException("c"))));
    }

    [Fact]
    public void AHeldPackage_IsNeitherFailedNorDelivered_SoTheNextBootRetriesIt()
    {
        var summary = DefaultInstallSummary.Empty with { Held = [new DefaultInstallHold("X", "floor")] };
        Assert.Equal(0, summary.Failed);
        Assert.Empty(summary.Delivered);
        Assert.Null(InstanceAutoRegistrationService.LeftUndone(summary));
    }
}
