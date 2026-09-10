using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Mesh.Services;
using Xunit;

using MeshWeaver.Compiler;
namespace MeshWeaver.Graph.Test;

/// <summary>
/// 🚨 Issue #3903 — <b>a NodeType whose own <c>Source/</c> subtree is absent must report THAT, and
/// must not have the same doomed compile taken again on every boot.</b>
///
/// <para><b>The measurement this pins.</b> memex.meshweaver.cloud, 2026-09-10:
/// <c>rbuergi/OperationRequest</c> had been at <c>compilationStatus: Error</c> since 2026-09-06,
/// with <c>lastCompileStartedAt</c> moving on every pod boot, reporting</para>
///
/// <code>
/// CS0246 The type or namespace name 'OperationRequestContent' could not be found
/// CS1061 'MessageHubConfiguration' has no 'AddOperationRequestControlPlane'
/// CS1061 'LayoutDefinition'        has no 'AddOperationRequestLayoutAreas'
/// </code>
///
/// <para>All three names are its own three <c>Source/*</c> nodes, and
/// <c>search 'namespace:rbuergi/OperationRequest scope:subtree'</c> returned <b>0</b> against 21
/// under the package copy. Its snapshot nonetheless held <b>41</b> nodes, every one pulled in by a
/// <c>shared=@Store/…</c> entry — so <c>PreWarmStatus.NoSources</c>, which asks whether the snapshot
/// is EMPTY, could not fire, and nothing anywhere said "one of your declared source queries matched
/// nothing". The investigation went looking for the three symbols in module surfaces that never
/// carried them.</para>
///
/// <para><b>Both controls run here, on the same fixture.</b> The NEGATIVE control is that shape; the
/// POSITIVE control is the identical type with its own source node present, which must still
/// compile, still classify as an ordinary compile error, and still earn its automatic re-drive. A
/// fix that "worked" by declining every re-drive would pass the first and fail the second.</para>
///
/// <para>🚨 <b>The pre-#3903 behaviour is re-measured on every run, not asserted from memory.</b>
/// <c>HasStaleFailureVerdict</c> takes the type's path as an OPTIONAL argument, and without it the
/// convergence question is unanswerable and answered NO — which is exactly what the predicate did
/// before this change. Every test below calls both overloads on the same definition, so the
/// baseline arm fails the moment the fix stops making a difference.</para>
/// </summary>
public class ADoomedCompileIsClassifiedNotRepeatedTest
{
    private const string TypePath = "rbuergi/OperationRequest";
    private const string OwnSourceQuery = "namespace:Source scope:subtree";

    /// <summary>The live node's <c>sources</c>, as read off memex.meshweaver.cloud 2026-09-10.</summary>
    private static readonly ImmutableList<string> DeclaredSources = ImmutableList.Create(
        OwnSourceQuery,
        "shared=@Store/Core/Source",
        "shared=@Store/Licensing/Source");

    /// <summary>The <c>shared=</c> half of the live snapshot — real paths, shortened.</summary>
    private static readonly ImmutableList<string> SharedOnly = ImmutableList.Create(
        "Store/Core/Source/CoreContent",
        "Store/Core/Source/MeshQueries",
        "Store/Licensing/Source/LicenseTerms");

    /// <summary>The same, plus the type's own source node — the world before it went missing.</summary>
    private static readonly ImmutableList<string> SharedPlusOwn =
        SharedOnly.Add($"{TypePath}/Source/OperationRequestContent");

    private static ImmutableDictionary<string, long> Snapshot(IEnumerable<string> paths)
    {
        var map = ImmutableDictionary<string, long>.Empty;
        var ticks = 1L;
        foreach (var path in paths)
            map = map.SetItem(path, ticks++);
        return map;
    }

    /// <param name="sourcePaths">What the type's source discovery resolved.</param>
    /// <param name="everCompiled">The second witness — whether the sources were LOST or never present.</param>
    private static NodeTypeDefinition Failing(
        IEnumerable<string> sourcePaths, bool everCompiled = true) => new()
    {
        Configuration =
            "config => config.WithContentType<OperationRequestContent>().AddDefaultLayoutAreas()"
            + ".AddOperationRequestControlPlane()",
        Sources = DeclaredSources,
        CompilationStatus = CompilationStatus.Error,
        CompilationError = "CS0246 The type or namespace name 'OperationRequestContent' could not be found",
        CurrentSourceVersions = Snapshot(sourcePaths),
        // The verdict was formed on a DIFFERENT framework — i.e. this pod's boot is exactly the
        // event that re-opened the attempt before #3903, on every image, for ever.
        FailedBuildInputs = "fw=an0therfr4me;mod=mod-1;src=3:0000000000000000",
        LastCompileSucceededAt = everCompiled
            ? new DateTimeOffset(2026, 9, 6, 7, 8, 49, TimeSpan.Zero)
            : null,
    };

    // ── The detector ────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 🚨 NEGATIVE CONTROL — the live shape. The union is non-empty, so every existing emptiness
    /// test reads healthy; the DECLARED entry for the type's own sources matched nothing.
    /// </summary>
    [Fact]
    public void OwnSourceSubtreeAbsent_IsNamed_WhileTheUnionLooksHealthy()
    {
        var snapshot = Snapshot(SharedOnly);

        snapshot.Should().NotBeEmpty(
            "the whole point: PreWarmStatus.NoSources asks whether the SNAPSHOT is empty, and for a "
            + "type that also draws on a shared library it never is — which is why the emptiness "
            + "that matters was invisible for four days");

        SourceCoverage.UnmatchedSourceQueries(DeclaredSources, TypePath, snapshot.Keys.ToList())
            .Should().ContainSingle().Which.Should().Be(OwnSourceQuery,
                "the type's own Source subtree resolved nothing while the shared= entries resolved, "
                + "so the compile ran against a set SHORT of what the type declares");
    }

    /// <summary>
    /// 🚨 POSITIVE CONTROL — the same type with its own source node present. Nothing is reported,
    /// so the detector cannot pass by accusing everything.
    /// </summary>
    [Fact]
    public void OwnSourcePresent_ReportsNothing()
    {
        SourceCoverage.UnmatchedSourceQueries(
                DeclaredSources, TypePath, Snapshot(SharedPlusOwn).Keys.ToList())
            .Should().BeEmpty(
                "every declared entry matched — an EMPTY list is the 'checked, all present' answer "
                + "and must stay distinguishable from the null one below");
    }

    /// <summary>
    /// 🚨 The <c>@</c> shorthand's FALSE-ALARM control. <c>CodeQueryResolver.Expand</c> turns one
    /// <c>shared=@Store/Core/Source</c> entry into TWO queries — a <c>path:</c> exact match and a
    /// <c>namespace:… scope:subtree</c> folder match — and the exact one matches nothing whenever
    /// the folder node is not itself a Code node, which is the normal case. Measured per QUERY, every
    /// healthy shared entry in the fleet would be reported as half missing; the unit is therefore the
    /// authored ENTRY.
    /// </summary>
    [Fact]
    public void ASharedEntryWhoseFolderNodeIsNotCode_IsNotAccused()
    {
        // Only the subtree half of "@Store/Core/Source" matches: there is no Code node AT the
        // folder path itself.
        var snapshot = Snapshot(["Store/Core/Source/CoreContent", $"{TypePath}/Source/Own"]);

        SourceCoverage.UnmatchedSourceQueries(DeclaredSources, TypePath, snapshot.Keys.ToList())
            .Should().ContainSingle().Which.Should().Be("shared=@Store/Licensing/Source",
                "only the entry that genuinely resolved nothing is named — the Store/Core entry "
                + "matched through its subtree half and must not be accused because its path: half "
                + "did not");
    }

    /// <summary>
    /// 🚨 NOT DETERMINED is its own answer. An empty list means "checked, every declared query
    /// matched"; null means "nothing was established". Collapsing them is the
    /// <c>NodeDiagnosticsOutcome</c> defect (#3912) in a different costume.
    /// </summary>
    [Fact]
    public void WithoutEvidence_TheAnswerIsNull_NeverEmpty()
    {
        SourceCoverage.UnmatchedSourceQueries(DeclaredSources, TypePath, matchedPaths: null)
            .Should().BeNull("no source snapshot was established, so nothing was checked");

        SourceCoverage.UnmatchedSourceQueries(DeclaredSources, nodeTypePath: null, [])
            .Should().BeNull("without the type's path $self cannot be expanded, so no query is evaluable");

        SourceCoverage.UnmatchedSourceQueries(["laptop pricing"], TypePath, [])
            .Should().BeNull(
                "a free-text query routes to the vector index, which a pure function cannot "
                + "reproduce — an entry the offline evaluator refuses contributes to NEITHER side");

        // …and one inevaluable entry does not silence an evaluable one.
        SourceCoverage.UnmatchedSourceQueries(["laptop pricing", OwnSourceQuery], TypePath, [])
            .Should().ContainSingle().Which.Should().Be(OwnSourceQuery);
    }

    // ── The report ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 🚨 NEGATIVE CONTROL — the durable record leads with the diagnosis instead of leaving the
    /// reader with three symbols to hunt through module surfaces.
    /// </summary>
    [Fact]
    public void AFailureOnAShortSourceSet_RecordsWhichQueryMatchedNothing()
    {
        var stamped = NodeTypeCompilationHelpers.ApplyCompileFailure(
            Failing(SharedOnly), result: null,
            new CompilationException(TypePath, "CS0246 'OperationRequestContent' could not be found"),
            activityPath: null, modulesHash: "mod-1", nodeTypePath: TypePath);

        stamped.FailedSourceQueries.Should().ContainSingle(
                "the standing verdict must carry the finding that explains it — a reader who is "
                + "not told the source set was short spends the investigation hunting the named "
                + "symbols through module surfaces that never carried them")
            .Which.Should().Be(OwnSourceQuery);
        stamped.CompilationError.Should()
            .Contain("MISSING SOURCES", "the diagnosis leads")
            .And.Contain(OwnSourceQuery, "…and names the query the author can act on")
            .And.Contain("CS0246", "…without discarding the evidence it explains");
    }

    /// <summary>🚨 POSITIVE CONTROL — an ordinary compile error is recorded exactly as before.</summary>
    [Fact]
    public void AnOrdinaryCompileError_IsRecordedUnchanged()
    {
        var stamped = NodeTypeCompilationHelpers.ApplyCompileFailure(
            Failing(SharedPlusOwn), result: null,
            new CompilationException(TypePath, "CS0103 The name 'Nope' does not exist"),
            activityPath: null, modulesHash: "mod-1", nodeTypePath: TypePath);

        stamped.FailedSourceQueries.Should().BeEmpty(
            "determined, and every declared query matched — the failure is about the CODE");
        stamped.CompilationError.Should().NotContain("MISSING SOURCES",
            "a diagnosis that fires on a healthy source set is noise, and noise is how a real one "
            + "stops being read");
    }

    /// <summary>
    /// A stamp with no path could not measure the coverage, and must say so rather than record the
    /// "checked, all present" shape.
    /// </summary>
    [Fact]
    public void AFailureStampedWithoutThePath_LeavesTheFindingUndetermined()
    {
        NodeTypeCompilationHelpers.ApplyCompileFailure(
                Failing(SharedOnly), result: null, new CompilationException(TypePath, "CS0246"),
                activityPath: null, modulesHash: "mod-1")
            .FailedSourceQueries.Should().BeNull(
                "not determined — never an empty list, which reads as 'checked and clean'");
    }

    /// <summary>The finding describes a STANDING failure, so a success must take it with it.</summary>
    [Fact]
    public void ASuccess_ClearsTheFinding()
    {
        var failed = NodeTypeCompilationHelpers.ApplyCompileFailure(
            Failing(SharedOnly), result: null, new CompilationException(TypePath, "CS0246"),
            activityPath: null, modulesHash: "mod-1", nodeTypePath: TypePath);
        failed.FailedSourceQueries.Should().NotBeNull("the fixture must start from the state it clears");

        NodeTypeCompilationHelpers.ApplyCompileSuccess(
                failed, new NodeCompilationResult("/cache/T.dll", []),
                currentNodeVersion: 2, activityPath: null, releasePath: null, modulesHash: "mod-1")
            .FailedSourceQueries.Should().BeNull(
                "a standing 'these queries matched nothing' describes a failure that no longer "
                + "exists — left behind it would tell the next reader a green type is broken");
    }

    // ── The non-repetition ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// 🚨 NEGATIVE CONTROL, and the behaviour change itself: a new framework no longer re-opens an
    /// attempt at a compile whose missing symbols live in nodes this mesh does not hold. The
    /// baseline arm re-measures the pre-#3903 answer on the SAME definition.
    /// </summary>
    [Fact]
    public void ANewFramework_DoesNotReDriveACompileThatCannotConverge()
    {
        var doomed = Failing(SharedOnly);

        NodeTypeCompilationHelpers.HasStaleFailureVerdict(doomed, "mod-1")
            .Should().BeTrue(
                "BASELINE — without the type's path the convergence question cannot be asked, which "
                + "is exactly what this predicate did before #3903: the stamped token names another "
                + "framework, so every boot bought another identical compile");

        NodeTypeCompilationHelpers.IsUnconvergableSourceFailure(doomed, TypePath)
            .Should().BeTrue();

        NodeTypeCompilationHelpers.HasStaleFailureVerdict(doomed, "mod-1", TypePath)
            .Should().BeFalse(
                "no framework and no module set can supply a symbol that lives in a source node "
                + "this mesh does not hold, so re-driving on either is not a retry — it is the same "
                + "measurement taken again");
    }

    /// <summary>
    /// 🚨 POSITIVE CONTROL — the re-drive is intact. A type whose sources are all present and whose
    /// verdict was formed on another framework still earns its one automatic attempt; #1793's whole
    /// purpose survives.
    /// </summary>
    [Fact]
    public void ANewFramework_StillReDrivesAnOrdinaryFailure()
    {
        var ordinary = Failing(SharedPlusOwn);

        NodeTypeCompilationHelpers.IsUnconvergableSourceFailure(ordinary, TypePath)
            .Should().BeFalse("every declared query matched — this failure is about the code");
        NodeTypeCompilationHelpers.HasStaleFailureVerdict(ordinary, "mod-1", TypePath)
            .Should().BeTrue(
                "a shipped fix must still reach the nodes it was written for (#1793) — a change "
                + "that declined this one would be a mute, not a classification");
    }

    /// <summary>
    /// 🚨 The SECOND WITNESS. A type that has NEVER built cannot have LOST its sources: its failure
    /// may be its own Configuration, and that one must keep earning an attempt on a new framework —
    /// the same discriminator, for the same reason, as <c>PreWarmStatus.NoSources</c>.
    /// </summary>
    [Fact]
    public void ATypeThatNeverBuilt_IsStillReDriven()
    {
        var neverBuilt = Failing(SharedOnly, everCompiled: false);

        SourceCoverage.UnmatchedSourceQueries(
                DeclaredSources, TypePath, neverBuilt.CurrentSourceVersions!.Keys.ToList())
            .Should().ContainSingle("the coverage says the same thing — only the witness differs");
        NodeTypeCompilationHelpers.IsUnconvergableSourceFailure(neverBuilt, TypePath)
            .Should().BeFalse();
        NodeTypeCompilationHelpers.HasStaleFailureVerdict(neverBuilt, "mod-1", TypePath)
            .Should().BeTrue("nothing was lost, so nothing rules out that a new image is the answer");
    }

    /// <summary>
    /// 🚨 IT CONVERGES. The decision is recomputed from the LIVE snapshot on every emission, so the
    /// instant the missing nodes land the type is re-driven — with no field to clear and no budget
    /// to refund. That is what makes this a classification rather than a cap.
    /// </summary>
    [Fact]
    public void WhenTheSourcesComeBack_TheReDriveResumesImmediately()
    {
        var doomed = Failing(SharedOnly);
        NodeTypeCompilationHelpers.HasStaleFailureVerdict(doomed, "mod-1", TypePath).Should().BeFalse();

        var restored = doomed with { CurrentSourceVersions = Snapshot(SharedPlusOwn) };

        NodeTypeCompilationHelpers.HasStaleFailureVerdict(restored, "mod-1", TypePath)
            .Should().BeTrue(
                "the same node, one source publication later — nothing had to notice that the "
                + "framework changed, and nothing had to reset the standing finding");
    }

    /// <summary>
    /// <see cref="CompilationStatus.Unavailable"/> means the compile reached NO verdict, so there is
    /// nothing measured to decline repeating (#1701). It must keep its input-independent re-drive.
    /// </summary>
    [Fact]
    public void AnUnavailableVerdict_IsNeverDeclined()
    {
        var unavailable = Failing(SharedOnly) with
        {
            CompilationStatus = CompilationStatus.Unavailable,
            FailedBuildInputs = NodeTypeCompilationHelpers.BuildInputsToken(
                "mod-1", Snapshot(SharedOnly)),
        };

        NodeTypeCompilationHelpers.IsUnconvergableSourceFailure(unavailable, TypePath)
            .Should().BeFalse("nothing was established, so nothing is being repeated");
        NodeTypeCompilationHelpers.HasStaleFailureVerdict(unavailable, "mod-1", TypePath)
            .Should().BeTrue("'we never found out' is even less of a reason to stop asking");
    }
}
