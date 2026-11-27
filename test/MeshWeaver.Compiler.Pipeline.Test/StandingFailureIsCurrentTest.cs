using System.Collections.Immutable;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Mesh.Services;
using Xunit;

using MeshWeaver.Compiler;
namespace MeshWeaver.Graph.Test;

/// <summary>
/// Pure pins of <see cref="NodeTypeCompilationHelpers.IsStandingFailureCurrent"/> — the predicate the
/// batch bake reads before re-compiling a <c>CompilationStatus.Error</c> type (MeshWeaver#8529: six
/// broken user types re-compiled by every pod of every roll). True only when NEITHER re-drive trigger
/// would fire; every uncertainty answers false (compile).
/// </summary>
public class StandingFailureIsCurrentTest
{
    private const string ModulesHash = "mod-1";
    private const string OldBuild = "4.0.14067";
    private const string NewBuild = "4.0.14099";

    private static readonly ImmutableDictionary<string, long> Sources =
        ImmutableDictionary<string, long>.Empty.Add("U/T/Source/A", 5).Add("U/T/Source/B", 6);

    private static NodeTypeDefinition Failed(string? platform = NewBuild) => new()
    {
        CompilationStatus = CompilationStatus.Error,
        CompilationError = "CS0246: The type or namespace name 'BehaviorSubject<>' could not be found",
        FailedBuildInputs = NodeTypeCompilationHelpers.BuildInputsToken(ModulesHash, Sources),
        FailedPlatformVersion = platform,
    };

    [Fact]
    public void AFailureFormedUnderTheLiveInputs_OnThisBuild_IsCurrent() =>
        NodeTypeCompilationHelpers.IsStandingFailureCurrent(Failed(), ModulesHash, Sources, NewBuild)
            .Should().BeTrue();

    [Fact]
    public void AFailureFormedOnANewerBuild_IsCurrent_ForAnOlderReplica() =>
        NodeTypeCompilationHelpers.IsStandingFailureCurrent(Failed(NewBuild), ModulesHash, Sources, OldBuild)
            .Should().BeTrue("an older replica never retries a newer build's verdict");

    [Fact]
    public void AnEditedSource_IsNotCurrent() =>
        NodeTypeCompilationHelpers.IsStandingFailureCurrent(
                Failed(), ModulesHash, Sources.SetItem("U/T/Source/A", 7), NewBuild)
            .Should().BeFalse("a source edit is a new input");

    [Fact]
    public void AnAddedOrRemovedSource_IsNotCurrent()
    {
        NodeTypeCompilationHelpers.IsStandingFailureCurrent(
                Failed(), ModulesHash, Sources.Add("U/T/Source/C", 1), NewBuild)
            .Should().BeFalse();
        NodeTypeCompilationHelpers.IsStandingFailureCurrent(
                Failed(), ModulesHash, Sources.Remove("U/T/Source/B"), NewBuild)
            .Should().BeFalse();
    }

    [Fact]
    public void AChangedModuleSet_IsNotCurrent() =>
        NodeTypeCompilationHelpers.IsStandingFailureCurrent(Failed(), "mod-2", Sources, NewBuild)
            .Should().BeFalse("a module update may ship the symbol the source needs");

    [Fact]
    public void AFailureFromAnOlderOrderedBuild_IsNotCurrent_OnTheNewerBuild()
    {
        NodeTypeCompilationHelpers.IsStandingFailureCurrent(Failed(OldBuild), ModulesHash, Sources, NewBuild)
            .Should().BeFalse("the newer build gets exactly one attempt");
        NodeTypeCompilationHelpers.IsStandingFailureCurrent(Failed(null), ModulesHash, Sources, NewBuild)
            .Should().BeFalse("a failure stamped before the field existed gets its one-off recovery");
    }

    [Fact]
    public void AnUnorderedLiveBuild_CannotClaimToBeNewer() =>
        NodeTypeCompilationHelpers.IsStandingFailureCurrent(Failed(OldBuild), ModulesHash, Sources, "4.0.0-dev")
            .Should().BeTrue("an unordered pair answers 'not newer', exactly as HasFailureFromOlderPlatform does");

    // 🚨 #8529 follow-up — what a batch bake STAMPS must be comparable with what the next bake DERIVES.
    // The record's CurrentSourceVersions is kept by the type's own hub watcher only, so it can lag the
    // set a bake just resolved fresh and compiled against.
    private const string TypePath = "U/T";
    private static readonly ImmutableDictionary<string, long> LaggingSnapshot =
        Sources.SetItem("U/T/Source/A", 1);

    private static NodeTypeDefinition StampedByABake(IReadOnlyDictionary<string, long>? consumed) =>
        NodeTypeCompilationHelpers.ApplyCompileFailure(
            new NodeTypeDefinition { CurrentSourceVersions = LaggingSnapshot },
            result: null,
            new CompilationException(TypePath, "CS0246"),
            activityPath: null,
            ModulesHash,
            TypePath,
            importRefusals: null,
            consumedSources: consumed);

    [Fact]
    public void AFailureStampedFromTheConsumedSources_IsCurrent_WhateverTheRecordSnapshotSays() =>
        NodeTypeCompilationHelpers.IsStandingFailureCurrent(
                StampedByABake(Sources), ModulesHash, Sources, NodeTypeCompilationHelpers.LivePlatformVersion)
            .Should().BeTrue("the next bake resolves exactly the sources this one compiled against");

    [Fact]
    public void NegativeControl_AFailureStampedFromALaggingSnapshot_IsNotCurrent() =>
        NodeTypeCompilationHelpers.IsStandingFailureCurrent(
                StampedByABake(consumed: null), ModulesHash, Sources, NodeTypeCompilationHelpers.LivePlatformVersion)
            .Should().BeFalse("without the consumed set the stamp describes sources nobody compiled — "
                              + "the pre-fix shape that made every pod a stranger");

    [Fact]
    public void AFailureStampedFromTheConsumedSources_StillEarnsAnAttemptWhenTheSourcesMove() =>
        NodeTypeCompilationHelpers.IsStandingFailureCurrent(
                StampedByABake(Sources), ModulesHash, Sources.SetItem("U/T/Source/B", 99),
                NodeTypeCompilationHelpers.LivePlatformVersion)
            .Should().BeFalse("memoisation of a deterministic function, never a latch");

    [Fact]
    public void AnUnstampedOrNonErrorRecord_IsNeverCurrent()
    {
        NodeTypeCompilationHelpers.IsStandingFailureCurrent(
                Failed() with { FailedBuildInputs = null }, ModulesHash, Sources, NewBuild)
            .Should().BeFalse("nothing says what the verdict was formed from");
        NodeTypeCompilationHelpers.IsStandingFailureCurrent(
                Failed() with { CompilationStatus = CompilationStatus.Unavailable }, ModulesHash, Sources, NewBuild)
            .Should().BeFalse("Unavailable formed no verdict — re-asking is the only way to find out");
        NodeTypeCompilationHelpers.IsStandingFailureCurrent(
                Failed() with { CompilationStatus = CompilationStatus.Ok }, ModulesHash, Sources, NewBuild)
            .Should().BeFalse();
    }
}
