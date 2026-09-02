using System;
using System.Collections.Immutable;
using MeshWeaver.Compiler;
using MeshWeaver.Data;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// 🔴 <b>The write-amplification pins for issue #2895 — "a hydrate is not a compile".</b>
///
/// <para>A NodeType's MeshNode is the framework's highest-fan-out record: every reader of the type
/// databinds to it, every version lands a row in its partition's Postgres schema, and every write
/// fans a change notification out across the mesh. So a write that records NOTHING NEW is not free
/// — and on 2026-08-31 that fan-out, riding on a framework-identity rebake of every type at once,
/// took the portal's connection pool with it. <c>Doc/Architecture/RebakeWaves</c> has the full
/// accounting, including which half of the wave this file is about.</para>
///
/// <para><b>The framework already suppresses such a write</b> —
/// <c>MeshNodeStreamExtensions.UpdateOwn</c> compares the updated node against the current one with
/// <see cref="MeshNode.SerializedEquals"/> and returns without applying anything when they agree,
/// so an <c>Update</c> lambda that reproduces the persisted state mints no version at all. The
/// defect was never the write; it was a single field per write-back re-stamped with
/// <c>DateTimeOffset.UtcNow</c>, which can never equal what is persisted and therefore carried the
/// whole (otherwise identical) write past that gate.</para>
///
/// <para>🚨 The write saving is the SECOND reason these are wrong. The first is that each recorded
/// a fact that had not happened — and in both cases a gate downstream reads that fact as evidence:
/// a hydrate satisfying <c>IsFreshSuccess</c> proves a rebuild that never ran, and a start stamp
/// pushed past the activity-node create hides the torn source snapshot
/// <c>SourcesMovedDuringCompile</c> exists to surface.</para>
///
/// <para>These are unit pins on the two pure functions that decide it, so the property is checkable
/// without a mesh and a regression names itself. Both assert EQUALITY against the input — the shape
/// the no-op gate consumes — rather than field-by-field, so a field added later to either stamp is
/// covered by construction.</para>
/// </summary>
public class HydrateIsNotACompileTest
{
    private static readonly DateTimeOffset CompiledAt =
        new(2026, 8, 14, 9, 30, 0, TimeSpan.Zero);

    /// <summary>A healthy, settled NodeType — the state a
    /// <see cref="GetCompilationPathRequest"/> finds whenever it asks about a type that compiled
    /// long ago, which is the case this whole file is about.</summary>
    private static NodeTypeDefinition Healthy() => new()
    {
        Configuration = "config => config",
        Sources = ["namespace:Source scope:subtree"],
        CompilationStatus = CompilationStatus.Ok,
        CompilationError = null,
        LastCompileStartedAt = CompiledAt.AddSeconds(-4),
        LastCompileSucceededAt = CompiledAt,
        LastCompiledVersion = 11,
        LatestReleasePath = "P/T/Release/v11",
        LatestAssemblyCollection = "assemblies",
        LatestAssemblyPath = "P_T/v11.dll",
        LatestAssemblyMvid = "b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0",
        CompiledFrameworkVersion = NodeTypeCompilationHelpers.FrameworkVersion,
        CompiledSources = ImmutableDictionary<string, long>.Empty.Add("P/T/Source/File1", 42),
    };

    private static MeshNode NodeFor(NodeTypeDefinition def) =>
        new("T", "P") { Content = def, NodeType = MeshNode.NodeTypePath };

    /// <summary>
    /// The hydrate response: the handler resolved the bytes the record already names, out of the
    /// assembly store, under the key the record already carries. Nothing was compiled.
    /// <c>AssemblyLocation</c> is a store path that does not exist on this box — exactly as in a
    /// unit context, and <see cref="ServedBuildIdentity.OfFile"/> answers null for it, so the MVID
    /// falls back to the persisted one just as it does for an unreadable file in production.
    /// </summary>
    private static GetCompilationPathResponse Hydrated() =>
        new(Success: true,
            AssemblyLocation: "/cache/T_11/T.dll",
            Collection: "assemblies",
            Version: "11",
            Error: null,
            HubConfiguration: null)
        { ContentPath = "P_T/v11.dll" };

    [Fact]
    public void ResolvedSuccessIsANoOpOnHydrate()
    {
        var def = Healthy();

        var stamped = NodeTypeContractHandler.ApplyResolvedSuccess(
            def, Hydrated(), freshCompile: false, NodeFor(def));

        stamped.Should().Be(def,
            "a hydrate resolved the bytes the record already names — it observed no new fact, so "
            + "the write-back must reproduce the persisted definition exactly and let UpdateOwn's "
            + "no-op gate absorb the whole write. Any field that differs mints a node version, a "
            + "change-feed fan-out and a Postgres row for a fact nobody observed (#2895)");
    }

    [Fact]
    public void ResolvedSuccessOnHydrateKeepsTheRecordedSuccessTime()
    {
        var def = Healthy();

        var stamped = NodeTypeContractHandler.ApplyResolvedSuccess(
            def, Hydrated(), freshCompile: false, NodeFor(def));

        stamped.LastCompileSucceededAt.Should().Be(CompiledAt,
            "LastCompileSucceededAt names when a compile SUCCEEDED. DynamicTypePreWarmer's "
            + "RebuildMissingBytes and WatchForRecovery both prove a rebuild happened by requiring "
            + "it to be strictly newer than a baseline they took — a hydrate advancing it satisfies "
            + "that proof without compiling anything, which is the one outcome those gates exist to "
            + "withhold");
    }

    [Fact]
    public void ResolvedSuccessOnAFreshCompileStampsTheNewFacts()
    {
        var def = Healthy();
        var before = DateTimeOffset.UtcNow;

        var stamped = NodeTypeContractHandler.ApplyResolvedSuccess(
            def,
            Hydrated() with { Version = "12", ContentPath = "P_T/v12.dll" },
            freshCompile: true,
            NodeFor(def));

        stamped.Should().NotBe(def, "a real Roslyn run produced new bytes — this write MUST land");
        (stamped.LastCompileSucceededAt >= before
                && stamped.LastCompileSucceededAt <= DateTimeOffset.UtcNow)
            .Should().BeTrue("only a fresh compile may stamp a fresh success time");
        stamped.LastCompiledVersion.Should().Be(12L, "the bytes moved to a new store key");
        stamped.LatestAssemblyPath.Should().Be("P_T/v12.dll");
        stamped.CompiledFrameworkVersion.Should().Be(NodeTypeCompilationHelpers.FrameworkVersion);
    }

    /// <summary>
    /// The on-disk compatibility half: nothing about the record's SHAPE changed, so a node written
    /// by any previous build still reads — including one whose success time predates this fix by
    /// weeks, and one that carries no assembly MVID at all (every record written before #2471).
    /// The hydrate must carry both through untouched rather than "repairing" them, which is what
    /// keeps the write a no-op for the population that has been churning.
    /// </summary>
    [Fact]
    public void ResolvedSuccessOnHydrateCarriesALegacyRecordThroughUnchanged()
    {
        var legacy = Healthy() with
        {
            LastCompileSucceededAt = new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero),
            LatestAssemblyMvid = null,
        };

        var stamped = NodeTypeContractHandler.ApplyResolvedSuccess(
            legacy, Hydrated(), freshCompile: false, NodeFor(legacy));

        stamped.Should().Be(legacy,
            "a record written before this fix must hydrate to itself — otherwise the first "
            + "activation after the upgrade mints exactly the version this change removes");
        stamped.LatestAssemblyMvid.Should().BeNull(
            "an unreadable/absent assembly file must never erase or forge the served-build identity");
    }

    // ---- The Compiling flip: one claim, one start stamp ----------------------------------------

    [Fact]
    public void AStandingCompilingClaimKeepsItsOwnStartStamp()
    {
        var claim = Healthy() with
        {
            CompilationStatus = CompilationStatus.Compiling,
            LastCompileStartedAt = CompiledAt,
        };

        NodeTypeCompilationHelpers.StartOfThisCompileClaim(claim).Should().Be(CompiledAt,
            "HandleDispatchCompile's Pending → Compiling compare-and-swap IS the transition this "
            + "field names, and it stamped this value moments ago. Re-minting it in RunCompile's "
            + "activity-path flip moved the recorded start past the activity-node create — a step "
            + "bounded at ten seconds — so every source written inside that window read as older "
            + "than the compile and SourcesMovedDuringCompile lost the torn-snapshot evidence it "
            + "exists to surface");
    }

    [Fact]
    public void AFlipWithNoStandingClaimStillGetsAStartStamp()
    {
        var before = DateTimeOffset.UtcNow;

        // Both shapes a caller reaching the flip outside the CAS can present: a settled record,
        // and a Compiling one left without a stamp by an older write (the shape
        // DynamicTypePreWarmer.IsLiveCompileClaim explicitly treats as stranded).
        foreach (var def in new[]
                 {
                     Healthy(),
                     Healthy() with
                     {
                         CompilationStatus = CompilationStatus.Compiling,
                         LastCompileStartedAt = null,
                     },
                 })
        {
            var stamped = NodeTypeCompilationHelpers.StartOfThisCompileClaim(def);
            (stamped >= before && stamped <= DateTimeOffset.UtcNow).Should().BeTrue(
                "a Compiling status must never be left without the timestamp IsLiveCompileClaim "
                + "ages it by — an unstamped claim is honoured forever");
        }
    }
}
