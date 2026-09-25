using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using MeshWeaver.Compiler;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// 🚨 <b>The platform/plugin compatibility LADDER, held on the pure rules</b> (policy
/// <c>platform-backwards-compatibility</c>): compiled bytes are keyed on the compatibility key
/// <c>c&lt;major&gt;e&lt;epoch&gt;</c>, never on a per-build identity, and ranged by the producing
/// platform build (floor) and an optional ceiling.
///
/// <para>Every positive case has its negative control beside it: same epoch, different build ⇒
/// adopted and not stale; an epoch bump ⇒ declined and stale; a producer NEWER than the running
/// build ⇒ declined loudly (both versions named) and never laundered by a store hit; an OLDER
/// producer ⇒ adopted; a running build above a declared ceiling ⇒ declined; a plugin compiled
/// against a lower platform assembly version ⇒ binds, a higher one ⇒ does not.</para>
/// </summary>
public class PlatformCompatibilityTest
{
    private static readonly string Key = PlatformCompatibility.KeyOf(3, 1);
    private static readonly string NextEpoch = PlatformCompatibility.KeyOf(3, 2);
    private const string Older = "3.0.0-ci.9193";
    private const string Running = "3.0.0-ci.9215";
    private const string Newer = "3.0.0-ci.9300";

    private static NodeTypeDefinition Built(string key, string? producer, string? ceiling = null) => new()
    {
        Configuration = "config => config",
        CompilationStatus = CompilationStatus.Ok,
        CompiledFrameworkVersion = key,
        CompiledPlatformVersion = producer,
        PlatformCeiling = ceiling,
        LastCompiledVersion = 12,
        LatestAssemblyCollection = "nodetype-cache",
        LatestAssemblyPath = $"Crm_Offer/v12-{key[..8]}-0f0e0d0c0b0a.dll",
        LastCompileSucceededAt = new DateTimeOffset(2026, 9, 25, 5, 0, 0, TimeSpan.Zero),
    };

    // ---- the key ---------------------------------------------------------------------------------

    [Fact]
    public void TheKeyIsFixedWidth_RoundTrips_AndIsTheWholeStoreTag()
    {
        Assert.Equal("c003e001", Key);
        Assert.Equal(AssemblyCacheFileName.FrameworkTagLength, Key.Length);
        Assert.True(PlatformCompatibility.TryParseKey(Key, out var major, out var epoch));
        Assert.Equal((3, 1), (major, epoch));
        Assert.Equal(Key, AssemblyCacheFileName.TagOf($"v7-{Key}-9f4455cd1122.dll"));
        // Legacy per-build identities are NOT keys.
        Assert.False(PlatformCompatibility.IsKey("sdc4cbaa0e03ddf01c781cabc070e5ef4"));
        Assert.False(PlatformCompatibility.IsKey("g140c5fb7"));
        Assert.False(PlatformCompatibility.IsKey(null));
    }

    [Fact]
    public void TheLiveKeyIsTheDeclaredEpoch_NotAPerBuildIdentity()
    {
        var live = FrameworkBuildIdentity.FrameworkVersion;
        Assert.True(PlatformCompatibility.TryParseKey(live, out _, out var epoch));
        Assert.Equal(PlatformCompatibility.Declaration.Epoch, epoch);
        Assert.NotEqual(FrameworkBuildIdentity.BuildProvenance, live);
        Assert.Equal(live, PrebuiltAssemblySeeder.LiveFrameworkMvid);
        Assert.Equal(live, NodeTypeCompilationHelpers.FrameworkVersion);
    }

    // ---- adoption: the ladder ----------------------------------------------------------------------

    [Fact]
    public void SameEpoch_DifferentBuild_IsAdopted_AndNotStale()
    {
        // platform2 + plugin1: the platform rolled, the plugin's bytes stay.
        Assert.Null(PlatformCompatibility.DeclineReason(Key, Older, Key, Running));
        Assert.Null(PrebuiltAssemblySeeder.DeclineReason(Key, Older, Key, Running));
        var record = Built(Key, Older);
        Assert.Null(NodeTypeBuildIdentity.RefusalReason(record, Key, Running));
        Assert.Equal(CompilationStatus.Ok, NodeTypeBuildIdentity.ReportedStatus(record, Key, Running));
        Assert.Equal(CompilationStatus.Foreign, NodeTypeBuildIdentity.ReportedStatus(Built(Key, Newer), Key, Running));
        Assert.Equal(BakeState.Baked,
            NodeTypeBakeStatus.ClassifyAgainst(record, storeHasBytes: true, Key, Running).State);
    }

    [Fact]
    public void AnEpochBump_IsADeclaredBreak_DeclinedAndStale()
    {
        var reason = PlatformCompatibility.DeclineReason(Key, Older, NextEpoch, Running);
        Assert.NotNull(reason);
        Assert.Contains(Key, reason);
        Assert.Contains(NextEpoch, reason);
        var record = Built(Key, Older);
        Assert.NotNull(NodeTypeBuildIdentity.RefusalReason(record, NextEpoch, Running));
        Assert.Equal(BakeState.FrameworkStale,
            NodeTypeBakeStatus.ClassifyAgainst(record, storeHasBytes: false, NextEpoch, Running).State);
    }

    [Fact]
    public void AProducerNewerThanTheRunningBuild_IsDeclinedLoudly_NamingBoth()
    {
        var reason = PlatformCompatibility.DeclineReason(Key, Newer, Key, Running);
        Assert.NotNull(reason);
        Assert.Contains(Newer, reason);
        Assert.Contains(Running, reason);

        var record = Built(Key, Newer);
        Assert.NotNull(NodeTypeBuildIdentity.RefusalReason(record, Key, Running));
        // 🚨 A store hit must not launder bytes from a newer build: the store key cannot tell them
        // from ours.
        Assert.Equal(BakeState.FrameworkStale,
            NodeTypeBakeStatus.ClassifyAgainst(record, storeHasBytes: true, Key, Running).State);
        // Negative control: the SAME record on the producing build and on a later one is fine.
        Assert.Equal(BakeState.Baked,
            NodeTypeBakeStatus.ClassifyAgainst(record, storeHasBytes: true, Key, Newer).State);
        Assert.Null(NodeTypeBuildIdentity.RefusalReason(record, Key, "3.0.0-ci.9400"));
    }

    [Fact]
    public void AnUnknownProducer_IsOlder_AndAccepted()
    {
        Assert.Null(PlatformCompatibility.DeclineReason(Key, null, Key, Running));
        Assert.Null(NodeTypeBuildIdentity.RefusalReason(Built(Key, producer: null), Key, Running));
        // An unordered pair (a release against a continuous build) is not a "newer" reading either.
        Assert.False(PlatformCompatibility.ProducerIsNewer("3.0.0", Running));
        Assert.False(PlatformCompatibility.ProducerIsNewer(Newer, null));
        // A LOCAL build (-ci.0, no run number) is never "older" than a CI bake.
        Assert.False(PlatformCompatibility.ProducerIsNewer(Newer, "3.0.0-ci.0"));
    }

    [Fact]
    public void ARunningBuildAboveTheCeiling_IsDeclined_AtTheCeilingItIsNot()
    {
        var reason = PlatformCompatibility.DeclineReason(Key, Older, Older, Key, Running);
        Assert.NotNull(reason);
        Assert.Contains("ceiling", reason);
        Assert.Contains(Older, reason);
        Assert.Contains(Running, reason);
        Assert.Null(PlatformCompatibility.DeclineReason(Key, Older, Running, Key, Running));
        Assert.Null(PlatformCompatibility.DeclineReason(Key, Older, null, Key, Newer));
        Assert.NotNull(NodeTypeBuildIdentity.RefusalReason(Built(Key, Older, ceiling: Older), Key, Running));
    }

    [Fact]
    public void ARetiredPerBuildIdentity_StatesNoEpoch_AndIsDeclined()
    {
        var reason = PlatformCompatibility.DeclineReason("s944d7fd0000000000000000000000000", Older, Key, Running);
        Assert.NotNull(reason);
        Assert.Contains("retired", reason);
        Assert.NotNull(PlatformCompatibility.DeclineReason(null, Older, Key, Running));
    }

    // ---- the mixed roll: no re-key war -------------------------------------------------------------

    [Fact]
    public void TheOlderReplicaYieldsToANewerBuildsRecord_LeavingOrNot()
    {
        var newerRecord = Built(Key, Newer);
        Assert.True(NodeTypeBuildIdentity.OwnedByANewerPlatformBuild(newerRecord, Key, Running));
        Assert.Equal(NodeTypeEnrichmentHelpers.FrameworkStaleAction.Yield,
            NodeTypeEnrichmentHelpers.DecideFrameworkStale(
                newerRecord, Key, Running, bootedAt: null, leaving: false, recompileAttempts: 0));
        // Negative controls: the newer replica does not yield on its own record, nor on an older one.
        Assert.False(NodeTypeBuildIdentity.OwnedByANewerPlatformBuild(newerRecord, Key, Newer));
        Assert.False(NodeTypeBuildIdentity.OwnedByANewerPlatformBuild(Built(Key, Older), Key, Running));
        Assert.Equal(NodeTypeEnrichmentHelpers.FrameworkStaleAction.Recompile,
            NodeTypeEnrichmentHelpers.DecideFrameworkStale(
                Built(NextEpoch, Newer), Key, Running, bootedAt: null, leaving: false, recompileAttempts: 0));
    }

    // ---- the dependency record ----------------------------------------------------------------------

    [Fact]
    public void ADependencyRecord_SurvivesAPlatformBuild_AndMovesOnAnEpochBump()
    {
        var modules = ImmutableDictionary<string, string>.Empty;
        Func<string, string?> noVersion = _ => null;
        var produced = CompiledDependencies.Compute(
            ["MeshWeaver.Graph", "MeshWeaver.Layout", "System.Runtime"],
            CompiledDependencies.CreateCompatibilityIdResolver(Key, modules, noVersion),
            CompiledDependencies.ToolchainIdOf(Key));
        Assert.Equal("compat:" + Key, produced["MeshWeaver.Graph"]);
        Assert.False(produced.ContainsKey("System.Runtime"));

        // A later build of the same epoch validates it unchanged.
        Assert.Null(CompiledDependencies.FindMismatch(
            produced,
            CompiledDependencies.CreateCompatibilityIdResolver(Key, modules, noVersion),
            CompiledDependencies.ToolchainIdOf(Key)));
        // Negative control: the declared break invalidates it.
        Assert.NotNull(CompiledDependencies.FindMismatch(
            produced,
            CompiledDependencies.CreateCompatibilityIdResolver(NextEpoch, modules, noVersion),
            CompiledDependencies.ToolchainIdOf(NextEpoch)));
    }

    // ---- binding -------------------------------------------------------------------------------------

    [Fact]
    public void APluginCompiledAgainstALowerPlatformAssemblyVersion_Binds_AHigherOneDoesNot()
    {
        Assert.True(PlatformCompatibility.MayBind(new Version(3, 0, 0, 0), new Version(3, 1, 0, 0)));
        Assert.True(PlatformBinding.MayBind(new Version(3, 0, 0, 0), new Version(3, 0, 0, 0)));
        Assert.True(PlatformBinding.MayBind(null, new Version(3, 0, 0, 0)));
        Assert.True(PlatformBinding.MayBind(new Version(0, 0, 0, 0), new Version(3, 0, 0, 0)));
        Assert.False(PlatformBinding.MayBind(new Version(3, 1, 0, 0), new Version(3, 0, 0, 0)));
    }

    // ---- the declaration -----------------------------------------------------------------------------

    [Fact]
    public void TheDeclaration_ParsesBreaks_AndCapsThePreviousEpoch()
    {
        var declaration = CompatibilityDeclaration.Parse("""
            { "epoch": 2, "breaks": [ { "epoch": 2, "previousEpochCeiling": "3.0.0-ci.9400",
              "reason": "removed IFoo.Bar", "declaredIn": "#9999", "members": [] } ] }
            """);
        Assert.Equal(2, declaration.Epoch);
        Assert.Equal("3.0.0-ci.9400", declaration.CeilingForEpoch(1));
        Assert.Null(declaration.CeilingForEpoch(2));
        Assert.Null(CompatibilityDeclaration.Empty.CeilingForEpoch(0));
        // The shipped declaration is readable and states the epoch this build stamps.
        Assert.True(PlatformCompatibility.Declaration.Epoch >= 1);
    }
}
