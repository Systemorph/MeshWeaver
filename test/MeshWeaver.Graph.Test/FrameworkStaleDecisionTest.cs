using System;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Mesh.Services;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// The bind path's framework-stale decision, held without a hub — the
/// <see cref="StaleAssemblySelfHealWatcherTest"/> shape: <see cref="NodeTypeEnrichmentHelpers.DecideFrameworkStale"/>
/// is the ONE switch <c>ApplyStreamResult</c> takes when a record's build is for another framework,
/// and its three outcomes map one-to-one onto the branch's three actions (yield: overlay and leave
/// the record; overlay: the recompile prompt; recompile: flip Pending and rebuild).
///
/// <para>The case that mattered on 2026-09-22: a LEAVING process must never recompile a record
/// another generation stamped after it started — each attempt would re-key it backwards
/// and the newer replica would heal it forward again, 34 records deep on memex's control instance.
/// The controls on the other side are the ordinary post-roll heal (a foreign stamp from BEFORE
/// boot still recompiles) and a mesh with no registered clock, which must never yield.</para>
/// </summary>
public class FrameworkStaleDecisionTest
{
    private const string Live = "sdc4cbaa0e03ddf01c781cabc070e5ef4";
    private const string Newer = "s6f66941d6aba8e4dd141454d38c9258d";
    private static readonly DateTimeOffset BootedAt = new(2026, 9, 22, 8, 55, 57, TimeSpan.Zero);

    private static NodeTypeDefinition Foreign(DateTimeOffset? stampedAt) => new()
    {
        Configuration = "config => config",
        CompilationStatus = CompilationStatus.Ok,
        CompiledFrameworkVersion = Newer,
        LatestAssemblyCollection = "nodetype-cache",
        LatestAssemblyPath = $"Hosting_InstanceAction/v12-{Newer[..8]}-0f0e0d0c0b0a.dll",
        LastCompiledVersion = 12,
        LastCompileSucceededAt = stampedAt,
    };

    [Fact]
    public void ALeavingProcess_YieldsOnAStampAnotherGenerationMadeAfterBoot_OnEveryAttempt()
    {
        var owned = Foreign(BootedAt.AddMinutes(7));
        Assert.Equal(NodeTypeEnrichmentHelpers.FrameworkStaleAction.Yield,
            NodeTypeEnrichmentHelpers.DecideFrameworkStale(owned, Live, BootedAt, leaving: true, recompileAttempts: 0));
        Assert.Equal(NodeTypeEnrichmentHelpers.FrameworkStaleAction.Yield,
            NodeTypeEnrichmentHelpers.DecideFrameworkStale(owned, Live, BootedAt, leaving: true, recompileAttempts: 5));
    }

    [Fact]
    public void TheSurvivor_HealsAStampAnotherGenerationMadeAfterBoot()
    {
        // The other end of the same mid-roll pair: a draining replica re-keyed the record backwards
        // after THIS process booted. The stamp reads exactly as it does on the draining side (a
        // framework identity has no order), so only "who is leaving" separates them — and the
        // process that stays must heal, or it overlays the type as framework-stale for its whole
        // life. OrleansCompileActivityAccessTest.FrameworkStaleAssembly_SelfHealsOnInstanceActivation
        // drives this end through a real activation.
        var reKeyedBackwards = Foreign(BootedAt.AddMinutes(7));
        Assert.Equal(NodeTypeEnrichmentHelpers.FrameworkStaleAction.Recompile,
            NodeTypeEnrichmentHelpers.DecideFrameworkStale(reKeyedBackwards, Live, BootedAt, leaving: false, recompileAttempts: 0));
    }

    [Fact]
    public void ThePreviousImagesStamp_BeforeBoot_IsStillHealed_ThenOverlaidWhenTheBudgetIsSpent()
    {
        var previous = Foreign(BootedAt.AddHours(-3));
        Assert.Equal(NodeTypeEnrichmentHelpers.FrameworkStaleAction.Recompile,
            NodeTypeEnrichmentHelpers.DecideFrameworkStale(previous, Live, BootedAt, leaving: true, recompileAttempts: 0));
        Assert.Equal(NodeTypeEnrichmentHelpers.FrameworkStaleAction.Overlay,
            NodeTypeEnrichmentHelpers.DecideFrameworkStale(previous, Live, BootedAt, leaving: true, recompileAttempts: 1));
    }

    [Fact]
    public void WithoutARegisteredClock_NothingIsEverYielded()
    {
        // The benign side: a mesh that registered no ProcessBootClock cannot tell "after boot" from
        // "before", so it heals exactly as it always did — never leaves a type un-healed on a
        // boundary nobody measured.
        var owned = Foreign(BootedAt.AddMinutes(7));
        Assert.Equal(NodeTypeEnrichmentHelpers.FrameworkStaleAction.Recompile,
            NodeTypeEnrichmentHelpers.DecideFrameworkStale(owned, Live, bootedAt: null, leaving: true, recompileAttempts: 0));
    }

    [Fact]
    public void AStampWithNoTime_IsNeverYieldedOn()
    {
        var undated = Foreign(stampedAt: null);
        Assert.Equal(NodeTypeEnrichmentHelpers.FrameworkStaleAction.Recompile,
            NodeTypeEnrichmentHelpers.DecideFrameworkStale(undated, Live, BootedAt, leaving: true, recompileAttempts: 0));
    }
}
