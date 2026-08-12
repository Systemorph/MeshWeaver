using System;
using System.Collections.Immutable;
using MeshWeaver.Data;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Mesh.Services;
using Microsoft.Extensions.Logging;
using Xunit;
using Lsp = MeshWeaver.Mesh.Services.LanguageServer;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// Unit tests for the SHARED terminal compile stamp
/// (<see cref="NodeTypeCompilationHelpers.ApplyCompileSuccess"/> /
/// <see cref="NodeTypeCompilationHelpers.ApplyCompileFailure"/> /
/// <see cref="NodeTypeCompilationHelpers.SummarizeCompileError"/>) — the ONE field-set both
/// write-back paths apply: the per-NodeType hub's compile watcher (<c>RunCompile</c>) and the
/// initial-bake batch driver (<c>NodeTypeBatchBake</c>, issue #1207). These pins are what keeps
/// "batch-baked" and "activation-compiled" records indistinguishable: a field one path stamps
/// and the other forgets is exactly how a batch-baked type would later re-compile (or worse,
/// serve a stale build) on its first lazy activation.
/// </summary>
public class NodeTypeCompileStampTest
{
    private static NodeTypeDefinition PreviouslyBroken() => new()
    {
        Configuration = "config => config",
        Sources = ["namespace:Source scope:subtree"],
        CompilationStatus = CompilationStatus.Compiling,
        CompilationError = "old error",
        CompilationDiagnostics = ImmutableList.Create(
            new Lsp.DiagnosticInfo("CS0103", Lsp.DiagnosticSeverity.Error, "old diagnostic", null)),
        ReleaseNotes = "notes for the pending release",
        RequestedReleaseBy = "some-user",
        LatestReleasePath = "P/T/Release/old",
        LatestAssemblyCollection = "old-collection",
        LatestAssemblyPath = "old/path.dll",
        CompiledFrameworkVersion = "0ldfr4me",
        CompiledSources = ImmutableDictionary<string, long>.Empty.Add("P/T/Source/Old", 1),
    };

    private static NodeCompilationResult SuccessResult(long? storeVersion = 7) => new(
        AssemblyLocation: "/cache/T_1/T.dll",
        NodeTypeConfigurations: [],
        CompiledSources: ImmutableDictionary<string, long>.Empty.Add("P/T/Source/File1", 42),
        Collection: "assemblies",
        ContentPath: "P_T/v7.dll",
        Version: storeVersion);

    // ---- ApplyCompileSuccess ------------------------------------------------------------------

    [Fact]
    public void Success_StampsTheFullUsableBuildFieldSet()
    {
        var before = DateTimeOffset.UtcNow;
        var stamped = NodeTypeCompilationHelpers.ApplyCompileSuccess(
            PreviouslyBroken(), SuccessResult(), currentNodeVersion: 3,
            activityPath: "P/T/_Activity/compile-1", releasePath: "P/T/Release/new");

        stamped.CompilationStatus.Should().Be(CompilationStatus.Ok);
        stamped.CompilationError.Should().BeNull();
        stamped.CompilationDiagnostics.Should().BeNull();
        (stamped.LastCompileSucceededAt >= before
                && stamped.LastCompileSucceededAt <= DateTimeOffset.UtcNow)
            .Should().BeTrue("a fresh success timestamp is what regression baselines compare against");
        // Must MATCH the version the IAssemblyStore upload used — a different version points
        // activation at a store key with no bytes.
        stamped.LastCompiledVersion.Should().Be(7L);
        stamped.LastCompilationActivityPath.Should().Be("P/T/_Activity/compile-1");
        stamped.LatestReleasePath.Should().Be("P/T/Release/new");
        stamped.ReleaseNotes.Should().BeNull("the notes were consumed by the release that carried them");
        stamped.CompiledSources!.ContainsKey("P/T/Source/File1").Should().BeTrue(
            "CompiledSources must record exactly the snapshot the compile consumed");
        stamped.LatestAssemblyCollection.Should().Be("assemblies");
        stamped.LatestAssemblyPath.Should().Be("P_T/v7.dll");
        stamped.CompiledFrameworkVersion.Should().Be(NodeTypeCompilationHelpers.FrameworkVersion,
            "HasUsableBuild compares this against the live framework — anything else re-compiles on every activation");
        stamped.RequestedReleaseBy.Should().BeNull("the consumed release-requester must not leak into later System compiles");

        NodeTypeCompilationHelpers.HasUsableBuild(
                new MeshWeaver.Mesh.MeshNode("T", "P") { Content = stamped }, stamped)
            .Should().BeTrue("the whole point of the stamp is that lazy activation finds a usable build");
    }

    [Fact]
    public void Success_WithoutStoreCoordinates_KeepsThePreviousAssemblyReference_AndFallsBackToTheNodeVersion()
    {
        var result = SuccessResult(storeVersion: null) with { Collection = null, ContentPath = null };
        var stamped = NodeTypeCompilationHelpers.ApplyCompileSuccess(
            PreviouslyBroken(), result, currentNodeVersion: 3, activityPath: null, releasePath: null);

        stamped.LastCompiledVersion.Should().Be(3L);
        stamped.LatestAssemblyCollection.Should().Be("old-collection",
            "a producer without a store must not erase the previous durable reference");
        stamped.LatestAssemblyPath.Should().Be("old/path.dll");
        stamped.LatestReleasePath.Should().Be("P/T/Release/old",
            "no new release landed, so the previous one stays");
        stamped.ReleaseNotes.Should().Be("notes for the pending release",
            "un-consumed notes must survive until a release actually carries them");
    }

    // ---- ApplyCompileFailure ------------------------------------------------------------------

    [Fact]
    public void Failure_StampsErrorAndDiagnostics_AndPreservesThePriorUsableBuild()
    {
        var diagnostics = new[]
        {
            new Lsp.DiagnosticInfo("CS1002", Lsp.DiagnosticSeverity.Error, "; expected", null),
        };
        var result = new NodeCompilationResult(null, [], Diagnostics: diagnostics);

        var stamped = NodeTypeCompilationHelpers.ApplyCompileFailure(
            PreviouslyBroken(), result,
            new CompilationException("P/T", "Compilation failed for 'P/T':\nCS1002 Error: ; expected"),
            activityPath: null);

        stamped.CompilationStatus.Should().Be(CompilationStatus.Error);
        stamped.CompilationError.Should().Contain("CS1002");
        stamped.CompilationDiagnostics.Should().ContainSingle(d => d.Id == "CS1002");
        stamped.CompiledSources.Should().BeNull("a failed compile recorded no consumed source set");
        stamped.RequestedReleaseBy.Should().BeNull("the failed request is done; a fresh request must re-stamp it");
        // 🚨 The prior successful build's fields survive a later failure — HasUsableBuild's
        // self-healing-across-Status=Error contract depends on it.
        stamped.LatestAssemblyCollection.Should().Be("old-collection");
        stamped.LatestAssemblyPath.Should().Be("old/path.dll");
        stamped.CompiledFrameworkVersion.Should().Be("0ldfr4me");
    }

    // ---- SummarizeCompileError ----------------------------------------------------------------

    [Fact]
    public void Summarize_CompilationException_UsesItsMessageVerbatim() =>
        NodeTypeCompilationHelpers.SummarizeCompileError(
                null, new CompilationException("P/T", "Compilation failed for 'P/T': CS0103 …"))
            .Should().Be("Compilation failed for 'P/T': CS0103 …");

    [Fact]
    public void Summarize_InfrastructureException_NamesTheExceptionType() =>
        NodeTypeCompilationHelpers.SummarizeCompileError(null, new NullReferenceException("boom"))
            .Should().Be("NullReferenceException: boom",
                "a bare 'Object reference not set…' told CI triage nothing about what died (#612)");

    [Fact]
    public void Summarize_NoException_JoinsTheActivityLogErrors()
    {
        var log = new ActivityLog(ActivityCategory.Compilation);
        log = log with
        {
            Messages = log.Messages
                .Add(new LogMessage("first error", LogLevel.Error))
                .Add(new LogMessage("some info", LogLevel.Information))
                .Add(new LogMessage("second error", LogLevel.Error)),
        };
        NodeTypeCompilationHelpers.SummarizeCompileError(
                new NodeCompilationResult(null, [], log), null)
            .Should().Be("first error; second error");
    }

    [Fact]
    public void Summarize_NothingAtAll_SaysNoAssemblyWasProduced() =>
        NodeTypeCompilationHelpers.SummarizeCompileError(null, null)
            .Should().Be("Compilation produced no assembly");
}
