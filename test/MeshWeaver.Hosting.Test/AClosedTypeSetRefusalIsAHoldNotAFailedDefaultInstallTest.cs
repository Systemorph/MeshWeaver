using System;
using System.Collections.Immutable;
using System.Reactive.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using MeshWeaver.Messaging;
using MeshWeaver.PluginCatalog;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace MeshWeaver.Hosting.Test;

/// <summary>
/// 🚨 On a host serving a CLOSED type set, a package whose root type the image does not register
/// (<c>Store/Plugin</c> on the control instance) can never install there. That is a HOLD, never a
/// failure: measured on the control instance, Explorer and Spaces logged an Error each plus the
/// "reconciled with FAILURES" roll-up on every pass, none of it actionable. The same refusal on an
/// OPEN mesh is a real defect and stays a failure.
/// </summary>
public class AClosedTypeSetRefusalIsAHoldNotAFailedDefaultInstallTest
{
    private static NodeTypeNotRegisteredException Refusal(string type = "Store/Plugin") =>
        NodeTypeNotRegisteredException.TryFrom("Explorer", $"NodeType '{type}' is not registered")!;

    // --- the refusal is TYPED at the write boundary, and only a typed one is a hold ---

    [Fact]
    public void TheWriteBoundary_TypesTheHubsRefusal_AndOnlyThat()
    {
        var typed = NodeTypeNotRegisteredException.TryFrom(
            "Explorer", "NodeType 'Store/Plugin' is not registered (referenced by 'Explorer')");
        Assert.NotNull(typed);
        Assert.Equal(["Store/Plugin"], typed!.TypeNames);
        Assert.Null(NodeTypeNotRegisteredException.TryFrom("Explorer", "the registry answered 500"));
        Assert.Null(NodeTypeNotRegisteredException.TryFrom("Explorer", null));
    }

    [Fact]
    public void AnUnrelatedException_WhoseMessageEmbedsThePhrase_IsNotAHold()
    {
        Assert.Null(InstanceAutoRegistrationService.StandingTypeSetHold(
            new InvalidOperationException("log said: NodeType 'X' is not registered"), closedTypeSet: true));
    }

    // --- the pure classifier ---

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
        Assert.Null(InstanceAutoRegistrationService.StandingTypeSetHold(
            new AggregateException(Refusal(), new InvalidOperationException("boom")), closedTypeSet: true));
    }

    [Fact]
    public void AnAggregate_NamesEveryUnregisteredType_NotJustTheFirst()
    {
        var reason = InstanceAutoRegistrationService.StandingTypeSetHold(
            new AggregateException(Refusal("Store/Plugin"), Refusal("Store/Core"), Refusal("Store/Plugin")),
            closedTypeSet: true);
        Assert.NotNull(reason);
        Assert.Contains("'Store/Plugin'", reason);
        Assert.Contains("'Store/Core'", reason);
        Assert.Equal(1, System.Text.RegularExpressions.Regex.Matches(reason!, "'Store/Plugin'").Count);
    }

    // --- the REAL per-package catch chain ---

    private static async Task<DefaultInstallSummary> Classify(Exception failure, bool closedTypeSet) =>
        await InstanceAutoRegistrationService.ClassifyInstallFailures(
                Observable.Throw<DefaultInstallSummary>(failure), "Explorer", closedTypeSet, NullLogger.Instance)
            .FirstAsync().Await();

    [Fact]
    public async Task TheChain_HoldsAnUnregisteredRootOnAClosedSet_AndReadsClean()
    {
        var summary = await Classify(Refusal(), closedTypeSet: true);
        Assert.Equal(0, summary.Failed);
        Assert.Empty(summary.Failures);
        var hold = Assert.Single(summary.Held);
        Assert.Equal("Explorer", hold.Package);
        Assert.Contains("'Store/Plugin'", hold.Reason);
        Assert.Null(InstanceAutoRegistrationService.LeftUndone(summary));
    }

    [Fact]
    public async Task TheChain_StillFailsTheSameRefusalOnAnOpenMesh()
    {
        var summary = await Classify(Refusal(), closedTypeSet: false);
        Assert.Equal(1, summary.Failed);
        Assert.Equal(["Explorer"], summary.Failures);
        Assert.Empty(summary.Held);
        Assert.NotNull(InstanceAutoRegistrationService.LeftUndone(summary));
    }

    [Fact]
    public async Task TheChain_StillFailsARealFailureOnAClosedSet()
    {
        var summary = await Classify(new InvalidOperationException("boom"), closedTypeSet: true);
        Assert.Equal(1, summary.Failed);
        Assert.Empty(summary.Held);
    }

    [Fact]
    public async Task TheChain_KeepsThePlatformHoldAndTheAuthorizationSkip_BeforeTheTypeSetHold()
    {
        var floor = await Classify(new PackagePlatformFloorException("needs platform >= 4.0.14289"), closedTypeSet: true);
        Assert.Equal("needs platform >= 4.0.14289", Assert.Single(floor.Held).Reason);
        var skipped = await Classify(new PackageAuthorizationException("not authorised"), closedTypeSet: true);
        Assert.Single(skipped.Skipped);
        Assert.Equal(0, skipped.Failed);
    }

    // --- the ledger's once-per-change comparison relies on value equality surviving the round trip ---

    [Fact]
    public void DefaultInstallHold_KeepsValueEquality_ThroughTheLedgerRoundTrip()
    {
        var hold = new DefaultInstallHold("Explorer", "its NodeType(s) 'Store/Plugin' are not registered");
        var ledger = new DefaultInstallLedger { Held = ImmutableList.Create(hold) };
        var json = JsonSerializer.Serialize(ledger, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var back = JsonSerializer.Deserialize<DefaultInstallLedger>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;

        Assert.NotSame(hold, back.Held[0]);
        Assert.True(back.Held.Contains(new DefaultInstallHold("Explorer", hold.Reason)));
        Assert.False(back.Held.Contains(new DefaultInstallHold("Explorer", "a different reason")));
    }

    [Fact]
    public void ARealFailure_IsStillCountedAndLeavesRepairOwed()
    {
        var summary = new DefaultInstallSummary(0, 0, 1, ["Explorer"]) { Failures = ["Explorer"] };
        Assert.Equal(1, summary.Failed);
        Assert.NotNull(InstanceAutoRegistrationService.LeftUndone(summary));
    }
}
