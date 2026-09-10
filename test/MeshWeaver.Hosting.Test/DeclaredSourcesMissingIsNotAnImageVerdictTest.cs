using System;
using System.Collections.Immutable;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Mesh.Services;
using Xunit;

namespace MeshWeaver.Hosting.Test;

/// <summary>
/// 🚨 Issue #3903 — <b>a NodeType whose own declared source query matches NOTHING is broken by
/// CONTENT, not by the image</b>, and the sweep must say which.
///
/// <para><c>PreWarmStatus.NoSources</c> already carries that rule for the degenerate case: "which
/// nodes a mesh query matches is a property of the mesh, not of the framework being rolled out, so
/// no image caused it and no rollout can fix it" — written after four <c>KmuBasics/*</c> types
/// stalled memex-cloud's self-update for a day across two images (2026-08-10). But it asks whether
/// the SNAPSHOT is empty, and for a type that also draws on a shared library the snapshot never is:
/// <c>rbuergi/OperationRequest</c> held 41 sources from five <c>shared=@Store/…</c> entries while
/// the entry for its own <c>Source/</c> subtree matched zero, and its three <c>CS0246</c>/
/// <c>CS1061</c> — its own three absent source nodes — were filed as an image verdict on every pod
/// boot from 2026-09-06.</para>
///
/// <para>🚨 <b>What must NOT change is that compile errors gate.</b> Both controls are here: the
/// missing-sources shape does not gate and is NAMED, and an ordinary compile error on a
/// previously-healthy type still refuses readiness. The second witness —
/// <see cref="NodeTypeDefinition.LastCompileSucceededAt"/> — is what separates them, exactly as it
/// does for <see cref="PreWarmStatus.NoSources"/>: a type that has NEVER built cannot have LOST
/// anything, so its failure may be its own configuration and keeps gating.</para>
/// </summary>
public class DeclaredSourcesMissingIsNotAnImageVerdictTest
{
    private const string TypePath = "rbuergi/OperationRequest";
    private const string OwnSourceQuery = "namespace:Source scope:subtree";

    private static ImmutableDictionary<string, long> Snapshot(params string[] paths)
    {
        var map = ImmutableDictionary<string, long>.Empty;
        var ticks = 1L;
        foreach (var path in paths)
            map = map.SetItem(path, ticks++);
        return map;
    }

    /// <param name="ownSourcePresent">Whether the type's own <c>Source/</c> node resolved.</param>
    /// <param name="everCompiled">The second witness: were the sources LOST, or never present?</param>
    private static NodeTypeDefinition Failing(bool ownSourcePresent, bool everCompiled = true) => new()
    {
        Configuration = "config => config.WithContentType<OperationRequestContent>()",
        Sources = ImmutableList.Create(OwnSourceQuery, "shared=@Store/Core/Source"),
        CompilationStatus = CompilationStatus.Error,
        CompilationError = "CS0246 The type or namespace name 'OperationRequestContent' could not be found",
        CurrentSourceVersions = ownSourcePresent
            ? Snapshot("Store/Core/Source/CoreContent", $"{TypePath}/Source/OperationRequestContent")
            : Snapshot("Store/Core/Source/CoreContent"),
        LastCompileSucceededAt = everCompiled
            ? new DateTimeOffset(2026, 9, 6, 7, 8, 49, TimeSpan.Zero)
            : null,
    };

    // ── The classification ──────────────────────────────────────────────────────────────────

    /// <summary>🚨 NEGATIVE CONTROL — the live shape, classified as the content fact it is.</summary>
    [Fact(Timeout = 60000)]
    public void OwnSourceQueryMatchedNothing_IsAContentVerdict()
    {
        var def = Failing(ownSourcePresent: false);

        def.CurrentSourceVersions.Should().NotBeEmpty(
            "the union is populated by the shared= entries, which is exactly why NoSources cannot "
            + "see this and why the emptiness that matters went unnamed for four days");

        DynamicTypePreWarmer.ClassifyCompileFailure(def, TypePath)
            .Should().Be(PreWarmStatus.DeclaredSourcesMissing);
    }

    /// <summary>
    /// 🚨 POSITIVE CONTROL — sources all present. Roslyn's verdict is about the code and stays an
    /// IMAGE verdict, so the classification cannot pass by reclassifying everything.
    /// </summary>
    [Fact(Timeout = 60000)]
    public void SourcesAllPresent_StaysACompileError()
    {
        DynamicTypePreWarmer.ClassifyCompileFailure(Failing(ownSourcePresent: true), TypePath)
            .Should().Be(PreWarmStatus.CompileError,
                "a type whose sources are still there and do not compile is this image's problem");
    }

    /// <summary>The second witness — a type that never built keeps gating.</summary>
    [Fact(Timeout = 60000)]
    public void ATypeThatNeverBuilt_StaysACompileError()
    {
        DynamicTypePreWarmer.ClassifyCompileFailure(
                Failing(ownSourcePresent: false, everCompiled: false), TypePath)
            .Should().Be(PreWarmStatus.CompileError,
                "nothing was LOST — the failure may be the type's own Configuration, and that must "
                + "keep refusing readiness and keep cascading UpstreamFailed to its dependents");
    }

    /// <summary>
    /// 🚨 Not being able to ASK must never buy the leniency the answer would have bought. Without
    /// the type's path the query cannot be expanded, and the classification falls back to the
    /// gating verdict.
    /// </summary>
    [Fact(Timeout = 60000)]
    public void WithoutThePath_TheClassificationStaysGating()
    {
        DynamicTypePreWarmer.ClassifyCompileFailure(Failing(ownSourcePresent: false))
            .Should().Be(PreWarmStatus.CompileError,
                "an unanswerable question is answered in the direction that keeps the gate intact");
    }

    // ── The gate ────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 🚨 NEGATIVE CONTROL — a rollout is never held on a failure no image caused and no image can
    /// fix. Non-blocking must not mean invisible: the health payload names the type.
    /// </summary>
    [Fact(Timeout = 60000)]
    public void ADeclaredSourcesMissingOutcome_DoesNotGate_AndIsNamed()
    {
        var gate = new NodeTypeBakeGateState { GatesReadiness = true };
        gate.MarkRunning("enumerating dynamic NodeTypes");

        var watch = gate.MarkOutcome(new PreWarmOutcome(
            TypePath, PreWarmStatus.DeclaredSourcesMissing,
            $"MISSING SOURCES: 1 of 2 declared source queries matched NO nodes ('{OwnSourceQuery}')")
        {
            WasHealthyBeforeBake = true,
        });

        watch.Should().BeFalse("no image can bring the missing source nodes back, so there is no "
            + "recovery for this pod to watch for");
        gate.Regressions.Should().BeEmpty("this is a content fact, not an image verdict");
        gate.ContentBroken.Keys.Should().Contain(TypePath);

        gate.MarkComplete("baked in 00:01:00 — compiled=3 alreadyBaked=0");

        gate.ReadinessGranted.Should().BeTrue();
        gate.Detail.Should().Contain(TypePath,
            "the pod serves, and says which type it is serving without");
    }

    /// <summary>🚨 POSITIVE CONTROL — the gate itself is untouched.</summary>
    [Fact(Timeout = 60000)]
    public void ACompileErrorOnAHealthyType_StillRefusesReadiness()
    {
        var gate = new NodeTypeBakeGateState { GatesReadiness = true };
        gate.MarkRunning("enumerating dynamic NodeTypes");

        gate.MarkOutcome(new PreWarmOutcome(TypePath, PreWarmStatus.CompileError, "CS0246")
        {
            WasHealthyBeforeBake = true,
        }).Should().BeTrue();

        gate.Phase.Should().Be(BakePhase.Regressed);
        gate.ContentBroken.Should().BeEmpty();
        gate.ReadinessGranted.Should().BeFalse(
            "the classification changed; the gate did not");
    }
}
