using System.Text.Json;
using System.Text.Json.Nodes;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// Unit contract for <see cref="MeshDataSourceExtensions.HasLoadableBuild"/> and
/// <see cref="MeshDataSourceExtensions.IsCompilationSettled"/> — the two gates the package
/// installer orders its root recycle and its root warm against.
///
/// <para><b>The bug this pins.</b> Both predicates decided everything on
/// <c>node.Content is not NodeTypeDefinition</c>, a CLR type test whose "this is not a NodeType at
/// all" escape answers <c>true</c> — settled, loadable, go ahead. That escape also fires for a
/// NodeType node whose Content arrived UN-MATERIALIZED (a <see cref="JsonElement"/> or
/// <see cref="JsonNode"/> mirror snapshot — the normal shape for a node that just crossed a sync
/// stream or was just created, which is precisely a node the installer wrote seconds ago). In that
/// shape a type that was still COMPILING answered "loadable", and the caller acted on it.</para>
///
/// <para><b>Why it is user-visible.</b>
/// <c>PackageInstaller.MayPublishIntoRoot</c> reads the definition with <c>ContentAs</c> one line
/// above and <c>HasLoadableBuild</c> one line below — so the two halves of ONE fold disagreed about
/// ONE snapshot: <c>inFlight</c> said "still compiling", <c>loadable</c> said "loadable", and
/// <c>loadable</c> is the term that settles the wait. <c>SettleRetypedRoot</c> then recycles the
/// retyped package root BEFORE its in-package NodeType has a build, and the hub that comes back
/// binds the fallback configuration for its whole lifetime — "No renderer is registered for area
/// <c>Tests</c> on hub <c>Store</c>", the plugin-gate RED the recycle ordering exists to prevent.
/// The same escape lets <c>WarmInstalledRoots</c> warm a root against an unloadable type.</para>
///
/// <para>Same defect, same fix, same test shape as <see cref="CompileSettlePredicateTest"/> — one
/// predicate over. Both now read the definition the way their consumers read it
/// (<c>ContentAs</c>), so a gate and its consumer can no longer disagree about one node.</para>
/// </summary>
public class LoadableBuildPredicateTest
{
    private const string NodeTypePath = "type/LoadableProbe";

    private static readonly JsonSerializerOptions Options = new(JsonSerializerOptions.Default);

    private static NodeTypeDefinition Def(
        CompilationStatus? status,
        string? assemblyPath = null,
        string? assemblyCollection = null,
        string? frameworkVersion = null) => new()
    {
        Description = "loadable-build probe",
        Configuration = "config => config",
        CompilationStatus = status,
        LatestAssemblyPath = assemblyPath,
        LatestAssemblyCollection = assemblyCollection,
        CompiledFrameworkVersion = frameworkVersion,
    };

    private static MeshNode Typed(NodeTypeDefinition def)
        => new(NodeTypePath) { Version = 3, Content = def };

    /// <summary>The same node as it arrives on a sync-stream mirror / straight after creation:
    /// Content un-materialized, as raw JSON rather than the CLR type.</summary>
    private static MeshNode AsJsonElement(NodeTypeDefinition def)
        => new(NodeTypePath) { Version = 3, Content = JsonSerializer.SerializeToElement(def, Options) };

    /// <summary>The DOM twin of the above — node builders assign JsonObject content.</summary>
    private static MeshNode AsJsonObject(NodeTypeDefinition def)
        => new(NodeTypePath) { Version = 3, Content = JsonSerializer.SerializeToNode(def, Options)! };

    [Theory]
    [InlineData(CompilationStatus.Pending)]
    [InlineData(CompilationStatus.Compiling)]
    public void InFlightCompile_IsNeitherSettledNorLoadable_WhateverShapeContentArrivesIn(
        CompilationStatus status)
    {
        var inFlight = Def(status);

        // Baseline: with typed content both predicates have always been right.
        Typed(inFlight).HasLoadableBuild(Options).Should().BeFalse(
            "a type that is mid-compile has no build an instance could bind to");
        Typed(inFlight).IsCompilationSettled(Options).Should().BeFalse(
            "Pending/Compiling is the definition of not settled");

        // 🚨 The defect: the identical node, un-materialized, must reach the identical verdict.
        // Before the fix the CLR type test fell through to the "not a NodeType at all" escape and
        // answered TRUE — the installer recycled the root while this very compile was running.
        AsJsonElement(inFlight).HasLoadableBuild(Options).Should().BeFalse(
            "an un-materialized JsonElement mirror snapshot is the SAME state — a CLR type test "
            + "must not turn an in-flight compile into a loadable build");
        AsJsonElement(inFlight).IsCompilationSettled(Options).Should().BeFalse(
            "…and the settle gate must not admit it either");

        AsJsonObject(inFlight).HasLoadableBuild(Options).Should().BeFalse(
            "the JsonObject shape a node builder assigns is the same state again");
        AsJsonObject(inFlight).IsCompilationSettled(Options).Should().BeFalse(
            "…and likewise for the settle gate");
    }

    /// <summary>
    /// The state a node repo COMMITS: <c>compilationStatus: Ok</c> with assembly coordinates from
    /// some other machine's framework. It is not loadable here, and saying otherwise is what pins
    /// the fallback configuration onto the instance for its hub's lifetime.
    /// </summary>
    [Fact]
    public void CommittedStaleStamp_IsNotLoadable_WhateverShapeContentArrivesIn()
    {
        var stale = Def(
            CompilationStatus.Ok,
            assemblyPath: "/cache/type_LoadableProbe_deadbeef/type_LoadableProbe.dll",
            assemblyCollection: "assemblies",
            frameworkVersion: "a-framework-this-process-has-never-run");

        Typed(stale).HasLoadableBuild(Options).Should().BeFalse(
            "a settled Ok whose CompiledFrameworkVersion is not this framework's advertises a "
            + "build this process cannot load");
        AsJsonElement(stale).HasLoadableBuild(Options).Should().BeFalse(
            "and the un-materialized snapshot of that very node is the same claim");
        AsJsonObject(stale).HasLoadableBuild(Options).Should().BeFalse(
            "…in the DOM shape too");
    }

    /// <summary>
    /// The pass-throughs the gates are documented to have, which the ContentAs read must preserve:
    /// a node that genuinely is not a NodeType, and a terminal state with nothing built.
    /// </summary>
    [Fact]
    public void NonNodeTypeContent_AndSettledStatesWithNothingBuilt_StillPassThrough()
    {
        var notATypeNode = new MeshNode("some/markdown")
        {
            Version = 1,
            Content = new MarkdownProbeContent("hello"),
        };
        notATypeNode.HasLoadableBuild(Options).Should().BeTrue(
            "asking a non-NodeType node is safe and answers yes — that pass-through is the whole "
            + "reason the CLR type test was there, and it has to survive the fix");
        notATypeNode.IsCompilationSettled(Options).Should().BeTrue(
            "…same for the settle gate");

        ((MeshNode?)null).HasLoadableBuild(Options).Should().BeTrue("nothing to judge");

        var neverCompiled = Def(status: null);
        Typed(neverCompiled).HasLoadableBuild(Options).Should().BeTrue(
            "no assembly coordinates at all is a settled answer — 'nothing built', not a stale build");
        AsJsonElement(neverCompiled).HasLoadableBuild(Options).Should().BeTrue(
            "…and the shape it arrives in does not change that");

        var failed = Def(CompilationStatus.Error);
        Typed(failed).HasLoadableBuild(Options).Should().BeTrue(
            "a genuinely failed compile wrote no assembly fields, so it too is settled");
        AsJsonElement(failed).HasLoadableBuild(Options).Should().BeTrue(
            "…in either shape");
        AsJsonElement(failed).IsCompilationSettled(Options).Should().BeTrue(
            "Error is a terminal state — the settle gate must not hold for a compile that is over");
    }

    /// <summary>Content that is a record of some other shape entirely — the "not a NodeType"
    /// pass-through, exercised with something that really does serialize.</summary>
    private sealed record MarkdownProbeContent(string Text);
}
