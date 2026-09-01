using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// The PURE half of the adopted-build source stamp (Systemorph/MeshWeaver#1834) —
/// <see cref="NodeTypeCompilationHelpers.ApplyAdoptedSourceStamp"/> and the one-shot contract of
/// <see cref="NodeTypeDefinition.RequestedSourceStampAt"/>.
///
/// <para>The defect it closes: <c>PrebuiltAssemblySeeder.Seed</c> writes CROSS-HUB, so its lambda
/// diffs against the MIRROR's snapshot of the NodeType node — which predates the first-activation
/// write of <c>CurrentSourceVersions</c> that the seeder's own subscribe triggers. Reading that
/// field there stamped <c>CompiledSources = null</c> under a non-empty snapshot, i.e.
/// <see cref="NodeTypeDefinition.IsDirty"/>, and the release request an install issues one step
/// later then recompiled the type that had just been adopted. The adopter now REQUESTS the stamp
/// and the owner — whose copy of both fields is authoritative — applies it.</para>
///
/// <para>These are pins on the DECISION, testable with no hub, no mesh and no timing; the
/// end-to-end consequence is pinned by <c>AdoptedBuildSourceStampTest</c> in
/// MeshWeaver.PluginCatalog.Test.</para>
/// </summary>
public class AdoptedSourceStampTest
{
    private static ImmutableDictionary<string, long> Snapshot(params (string Path, long Version)[] entries)
    {
        var snap = ImmutableDictionary<string, long>.Empty;
        foreach (var (path, version) in entries)
            snap = snap.SetItem(path, version);
        return snap;
    }

    private static NodeTypeDefinition JustAdopted() => new()
    {
        Configuration = "config => config",
        CompilationStatus = CompilationStatus.Ok,
        LastCompileSucceededAt = DateTimeOffset.UtcNow,
        LastCompiledVersion = 12,
        LatestAssemblyCollection = "assemblies",
        LatestAssemblyPath = "P_T/v12-abc.dll",
        CompiledFrameworkVersion = NodeTypeCompilationHelpers.FrameworkVersion,
        // The state the adoption commits: a build, a standing stamp request, and NO compiled-source
        // snapshot — the adopter could not know one.
        RequestedSourceStampAt = DateTimeOffset.UtcNow,
        CompiledSources = null,
    };

    [Fact]
    public void Stamp_MakesTheAdoptedBuildCurrent_AndConsumesTheRequest()
    {
        var live = Snapshot(("P/T/Source/Model", 42), ("P/T/Test/ModelTest", 43));
        var adopted = JustAdopted() with { CurrentSourceVersions = live };

        // 🚨 The pre-condition IS the defect: a build with sources and no stamp reads dirty, and
        // InstallReleaseRequestWatcher's "satisfied by the existing current build" branch requires
        // !IsDirty — so without the stamp the next release request recompiles it.
        adopted.IsDirty.Should().BeTrue("this is the state the adoption used to commit");

        var stamped = NodeTypeCompilationHelpers.ApplyAdoptedSourceStamp(
            adopted, live, canCompileLocally: true);

        stamped.CompiledSources.Should().Equal(live,
            "the stamp IS the owner's live snapshot — equal by construction, never by timing");
        stamped.IsDirty.Should().BeFalse(
            "an adopted build that reads dirty is thrown away by the next release request");
        stamped.RequestedSourceStampAt.Should().BeNull(
            "the request is ONE-SHOT: a standing request could later re-stamp CompiledSources from "
            + "a NEWER CurrentSourceVersions and suppress a rebuild that is genuinely needed");
    }

    [Fact]
    public void Stamp_TouchesNothingElseOnTheRecord()
    {
        var live = Snapshot(("P/T/Source/Model", 7));
        var adopted = JustAdopted() with { CurrentSourceVersions = live };
        var stamped = NodeTypeCompilationHelpers.ApplyAdoptedSourceStamp(
            adopted, live, canCompileLocally: true);

        // Everything the adoption itself decided must survive verbatim — the stamp answers ONE
        // question and must not become a second, competing terminal write.
        stamped.CompilationStatus.Should().Be(adopted.CompilationStatus);
        stamped.LastCompiledVersion.Should().Be(adopted.LastCompiledVersion);
        stamped.LastCompileSucceededAt.Should().Be(adopted.LastCompileSucceededAt);
        stamped.LatestAssemblyCollection.Should().Be(adopted.LatestAssemblyCollection);
        stamped.LatestAssemblyPath.Should().Be(adopted.LatestAssemblyPath);
        stamped.CompiledFrameworkVersion.Should().Be(adopted.CompiledFrameworkVersion);
        stamped.CurrentSourceVersions.Should().Equal(adopted.CurrentSourceVersions!);
    }

    [Fact]
    public void Stamp_OnASourcelessType_IsTheEmptySnapshot_NotNull()
    {
        // The fixture shape the older bundle tests use: both sides empty. It was already
        // !IsDirty by the null/null rule, but the stamp must leave an EMPTY snapshot rather than
        // null — that is what NodeTypeDefinition documents a sourceless success as recording.
        var empty = ImmutableDictionary<string, long>.Empty;
        var stamped = NodeTypeCompilationHelpers.ApplyAdoptedSourceStamp(
            JustAdopted() with { CurrentSourceVersions = empty }, empty, canCompileLocally: true);

        stamped.CompiledSources.Should().NotBeNull();
        stamped.CompiledSources!.Count.Should().Be(0);
        stamped.IsDirty.Should().BeFalse();
        stamped.RequestedSourceStampAt.Should().BeNull();
    }

    [Fact]
    public void Stamp_AcceptsANonImmutableSnapshot_WithoutAliasingIt()
    {
        // CurrentSourceVersions arrives as a plain Dictionary after a JSON round-trip. The stamp
        // must materialize its own immutable copy, or a later mutation of the caller's dictionary
        // would silently rewrite what "was compiled".
        var live = new Dictionary<string, long> { ["P/T/Source/Model"] = 5 };
        var stamped = NodeTypeCompilationHelpers.ApplyAdoptedSourceStamp(
            JustAdopted() with { CurrentSourceVersions = live }, live, canCompileLocally: true);

        stamped.CompiledSources.Should().BeAssignableTo<ImmutableDictionary<string, long>>();
        live["P/T/Source/Model"] = 6;
        stamped.CompiledSources!["P/T/Source/Model"].Should().Be(5L,
            "the recorded snapshot must not alias a mutable caller dictionary");
    }

    // ── The one-shot contract, from the other side ────────────────────────────────────────────

    [Fact]
    public void ASuccessfulCompile_ConsumesAStandingStampRequest()
    {
        // 🚨 The dangerous residue. A compile answers the same question from the set it ACTUALLY
        // consumed; a request left standing beside it would let the next CurrentSourceVersions
        // publication re-stamp CompiledSources over the compile's own snapshot — which is how a
        // needed rebuild gets suppressed (the failure mode with the opposite sign).
        var compiled = NodeTypeCompilationHelpers.ApplyCompileSuccess(
            JustAdopted() with { CurrentSourceVersions = Snapshot(("P/T/Source/Model", 42)) },
            new NodeCompilationResult(
                AssemblyLocation: "/cache/T/T.dll",
                NodeTypeConfigurations: [],
                CompiledSources: Snapshot(("P/T/Source/Model", 42)),
                Collection: "assemblies",
                ContentPath: "P_T/v13.dll",
                Version: 13),
            currentNodeVersion: 13, activityPath: null, releasePath: null);

        compiled.RequestedSourceStampAt.Should().BeNull();
        compiled.IsDirty.Should().BeFalse();
    }

    [Fact]
    public void AFailedCompile_ConsumesAStandingStampRequest()
    {
        var failed = NodeTypeCompilationHelpers.ApplyCompileFailure(
            JustAdopted() with { CurrentSourceVersions = Snapshot(("P/T/Source/Model", 42)) },
            result: null, error: new InvalidOperationException("boom"), activityPath: null);

        failed.CompiledSources.Should().BeNull("a failed compile records no consumed source set");
        failed.RequestedSourceStampAt.Should().BeNull(
            "the adopted build the request belonged to is gone with it");
    }

    // ── Ownership: the request is MESH state, never authored ───────────────────────────────────

    [Fact]
    public void TheStampRequest_IsAnOperationalMember()
    {
        // The set matches CASE-INSENSITIVELY (stored content is camelCased; a hub with no naming
        // policy emits PascalCase), so ask the set, not a default-comparer collection assertion.
        NodeTypeOperationalContent.MemberNames
            .Contains(nameof(NodeTypeDefinition.RequestedSourceStampAt))
            .Should().BeTrue(
                "an authored value would ask the owner to re-stamp a compile's source snapshot "
                + "from the live set — export must strip it and import must take the live node's "
                + "value");
    }
}
