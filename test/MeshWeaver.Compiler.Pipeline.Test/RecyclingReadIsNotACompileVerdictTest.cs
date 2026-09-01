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
/// 🚨 Issue #1701 — <b>a read that never got an answer is not a verdict about the code.</b>
///
/// <para><b>The failure.</b> A package-root hub recycles (an install, a rebind, a manual Recycle).
/// While it is tearing down, every dependent NodeType's compile reads that root and the read is
/// NACKed <c>ShuttingDown</c>. <c>GetMeshNode</c> deliberately re-probes and, if the address is
/// still recycling when the caller's budget runs out, surfaces the typed
/// <see cref="AddressRecyclingException"/> whose own message ends "Retry the read once the address
/// has reactivated". Nothing followed that advice. All 33 types of a package settled
/// <c>compile=FAILED(Error)</c> and the gate went red.</para>
///
/// <para><b>Why, in three steps — each pinned below.</b> (1) The availability fact reached the park
/// registry as a plain string plus <c>deterministic:false</c>, so three recycling reads parked the
/// type. (2) A parked type's next Pending flip is short-circuited and re-settled as
/// <see cref="CompilationStatus.Error"/>, discarding the <see cref="CompilationStatus.Unavailable"/>
/// classification the write-back had correctly applied. (3) The only automatic un-park is "the
/// SOURCES changed" — and a recycle changes no source — so the park was permanent until a human
/// pressed Compile.</para>
///
/// <para>The end-to-end half (a caller surviving the recycle it ordered and getting a fresh answer)
/// is pinned by <c>RecycleSurvivesItsOwnDisposeTest</c> in MeshWeaver.Hosting.Monolith.Test; the
/// read half by <c>GetMeshNodeShuttingDownIsNotAbsentTest</c>.</para>
/// </summary>
public class RecyclingReadIsNotACompileVerdictTest
{
    private const string TypePath = "P/T";

    private static ImmutableDictionary<string, long> Sources(params (string Path, long Ticks)[] entries)
    {
        var map = ImmutableDictionary<string, long>.Empty;
        foreach (var (path, ticks) in entries)
            map = map.SetItem(path, ticks);
        return map;
    }

    private static AddressRecyclingException Recycling() => new(
        $"GetMeshNode('{TypePath}'): the owning hub was still recycling (ShuttingDown) after 110 "
        + "probe(s) over 60.0s (budget 60s) — the address is recycling, NOT absent. "
        + "Retry the read once the address has reactivated.",
        inner: null);

    // ── 1. The classification: one predicate, so the two consumers cannot drift ───────────────

    /// <summary>
    /// Both shapes that mean "the compile never reached a verdict" are recognised, and nothing else
    /// is. A Roslyn verdict — or an ordinary infra fault, which IS a compile attempt that failed —
    /// must stay a compile failure with all its bounding.
    /// </summary>
    [Theory]
    [MemberData(nameof(ClassificationCases))]
    public void AvailabilityFailures_AreNotVerdicts(Exception? error, bool expected)
        => NodeTypeCompilationHelpers.IsAvailabilityNonVerdict(error).Should().Be(expected);

    public static TheoryData<Exception?, bool> ClassificationCases() => new()
    {
        { Recycling(), true },
        { new SourceDiscoveryUnavailableException("the source query did not answer"), true },
        { new CompilationException(TypePath, "CS0103: The name 'Nope' does not exist"), false },
        { new InvalidOperationException("something else broke"), false },
        { null, false },
    };

    // ── 2. The stamp: Unavailable, never Error ───────────────────────────────────────────────

    /// <summary>
    /// A recycling read stamps <see cref="CompilationStatus.Unavailable"/> — "the compile state
    /// could not be determined; nothing is known to be wrong with the source" — so the instance
    /// overlay drops "please correct the code" and the bake gate files it as unevaluated rather
    /// than as an image regression.
    /// </summary>
    [Fact]
    public void ARecyclingRead_StampsUnavailable_NotError()
    {
        var def = Definition(CompilationStatus.Compiling);

        NodeTypeCompilationHelpers.ApplyCompileFailure(def, result: null, Recycling(), activityPath: null)
            .CompilationStatus.Should().Be(CompilationStatus.Unavailable);

        NodeTypeCompilationHelpers
            .ApplyCompileFailure(def, result: null, new CompilationException(TypePath, "CS0103"), activityPath: null)
            .CompilationStatus.Should().Be(CompilationStatus.Error,
                "a Roslyn verdict is a statement about the code and must stay one");
    }

    // ── 3. The retry: Unavailable is stale ON ITS OWN ────────────────────────────────────────

    /// <summary>
    /// 🚨 The line that makes "Retry the read" something the framework actually does.
    ///
    /// <para>The automatic re-drive normally waits for the compile INPUTS to change — framework,
    /// installed modules, or sources. A package-root recycle moves none of the three, so an
    /// <see cref="CompilationStatus.Unavailable"/> verdict formed under exactly the live inputs was
    /// unreachable: every automatic path declined and only a human pressing Compile got the type
    /// out. Unavailable records "we never found out", and re-asking is the only way to find out —
    /// so it is stale regardless of the token.</para>
    /// </summary>
    [Fact]
    public void AnUnavailableVerdict_IsReDrivable_EvenWithTheVeryInputsItWasFormedUnder()
    {
        var sources = Sources(($"{TypePath}/Source/a", 1));
        var stampedUnderLiveInputs = NodeTypeCompilationHelpers.BuildInputsToken("mod-1", sources);

        var unavailable = Definition(CompilationStatus.Unavailable) with
        {
            CurrentSourceVersions = sources,
            FailedBuildInputs = stampedUnderLiveInputs,
        };

        NodeTypeCompilationHelpers.HasStaleFailureVerdict(unavailable, "mod-1")
            .Should().BeTrue(
                "a recycle changes no framework, no module and no source, so the inputs token can "
                + "never express the thing that actually changed — the address came back");
    }

    /// <summary>
    /// …and the bound that keeps it from becoming a poll is untouched: a real <c>Error</c> verdict
    /// formed under the live inputs still declines. Only "we never found out" is re-asked; "we
    /// found out, and it does not compile" is not.
    /// </summary>
    [Fact]
    public void AnErrorVerdict_UnderTheSameInputs_StillDeclines()
    {
        var sources = Sources(($"{TypePath}/Source/a", 1));
        var error = Definition(CompilationStatus.Error) with
        {
            CurrentSourceVersions = sources,
            FailedBuildInputs = NodeTypeCompilationHelpers.BuildInputsToken("mod-1", sources),
        };

        NodeTypeCompilationHelpers.HasStaleFailureVerdict(error, "mod-1")
            .Should().BeFalse("a source error would reproduce identically — there is nothing to retry");
    }

    /// <summary>
    /// The establishment gate still wins: before the sources watcher has written a snapshot the
    /// re-drive is WAITING, not declining, and firing there would compile from a set nobody
    /// established (#1216). Unavailable does not get to skip that.
    /// </summary>
    [Fact]
    public void AnUnavailableVerdict_WithAnUnestablishedSourceSet_IsNotReDrivable()
        => NodeTypeCompilationHelpers
            .HasStaleFailureVerdict(Definition(CompilationStatus.Unavailable), "mod-1")
            .Should().BeFalse();

    private static NodeTypeDefinition Definition(CompilationStatus? status) => new()
    {
        Configuration = "config => config",
        Sources = ["namespace:Source scope:subtree"],
        CompilationStatus = status,
    };
}
