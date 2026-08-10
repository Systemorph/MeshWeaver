using System.Text.Json;
using System.Text.Json.Nodes;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// Unit contract for <see cref="NodeTypeEnrichmentHelpers.IsCompileSettled"/> — the predicate that
/// decides which NodeType-stream emission a per-instance activation may bind its
/// <c>HubConfiguration</c> to.
///
/// <para><b>The bug this pins.</b> Every clause of that predicate used to pattern-match
/// <c>typeNode.Content is NodeTypeDefinition</c> — a CLR type test — while
/// <c>ApplyStreamResult</c>, which acts on whatever the predicate admits, reads the SAME node with
/// <c>ContentAs&lt;NodeTypeDefinition&gt;</c>, which also recovers a <see cref="JsonElement"/> /
/// <see cref="JsonNode"/> / same-short-named foreign type. The two disagree exactly when the mirror
/// holds Content in an UN-MATERIALIZED JSON shape — the normal shape for a node that just crossed a
/// sync stream or was just created. In that state every guard was skipped and the trailing
/// "content that isn't a NodeTypeDefinition at all" escape admitted the emission as SETTLED;
/// <c>ApplyStreamResult</c> then deserialized the very same node successfully and acted on a state
/// the gate exists to reject.</para>
///
/// <para>Consequence, and why it is user-visible rather than merely untidy: a PRE-COMPILE snapshot
/// (<c>CompilationStatus</c> null/Unknown) routes to <c>ApplyDefaultConfig</c>, so the instance hub
/// binds the mesh DEFAULT configuration — and enrichment binds ONCE, never re-observing, so it is
/// bound for the grain's whole lifetime. The NodeType's own layout areas never appear on the
/// instance's page, and for a NON-COMPILING type the compilation-error overlay can never surface:
/// the page renders (data flows, the area emits) and simply never contains the error. An in-flight
/// (Pending/Compiling) snapshot is frozen onto the same one-way ticket.</para>
/// </summary>
public class CompileSettlePredicateTest
{
    private const string NodeTypePath = "type/SettleProbe";

    private static readonly JsonSerializerOptions Options = new(JsonSerializerOptions.Default);

    /// <summary>A dynamic NodeType: it HAS source (a Configuration lambda), so a compile is coming
    /// and no pre-terminal state may be bound.</summary>
    private static NodeTypeDefinition Dynamic(CompilationStatus? status) => new()
    {
        Description = "settle-predicate probe",
        Configuration = "config => config",
        CompilationStatus = status,
    };

    private static MeshNode Typed(NodeTypeDefinition def)
        => new(NodeTypePath) { Version = 3, Content = def };

    /// <summary>The same node as it arrives on a sync-stream mirror / straight after creation:
    /// Content un-materialized, as raw JSON rather than the CLR type.</summary>
    private static MeshNode AsJsonElement(NodeTypeDefinition def)
        => new(NodeTypePath) { Version = 3, Content = JsonSerializer.SerializeToElement(def, Options) };

    /// <summary>The DOM twin of the above — node builders assign JsonObject content.</summary>
    private static MeshNode AsJsonObject(NodeTypeDefinition def)
        => new(NodeTypePath)
        {
            Version = 3,
            Content = JsonSerializer.SerializeToNode(def, Options)!
        };

    [Fact]
    public void PreCompileSnapshot_IsNotSettled_WhateverShapeContentArrivesIn()
    {
        var preCompile = Dynamic(status: null);

        // Baseline: with typed content the predicate has always been right.
        NodeTypeEnrichmentHelpers.IsCompileSettled(Typed(preCompile), Options)
            .Should().BeFalse(
                "the type has source to compile and has not reported a terminal state — binding "
                + "here gives the instance the DEFAULT config for the grain's whole lifetime");

        // 🚨 The defect: the identical node, un-materialized, must reach the identical verdict.
        NodeTypeEnrichmentHelpers.IsCompileSettled(AsJsonElement(preCompile), Options)
            .Should().BeFalse(
                "an un-materialized JsonElement mirror snapshot is the SAME state — a CLR type "
                + "test that cannot see it admitted every one of them as settled");

        NodeTypeEnrichmentHelpers.IsCompileSettled(AsJsonObject(preCompile), Options)
            .Should().BeFalse("the JsonNode/JsonObject shape is the DOM twin of the same state");
    }

    [Theory]
    [InlineData(CompilationStatus.Pending)]
    [InlineData(CompilationStatus.Compiling)]
    public void InFlightCompile_IsNeverSettled_WhateverShapeContentArrivesIn(CompilationStatus status)
    {
        var inFlight = Dynamic(status);

        NodeTypeEnrichmentHelpers.IsCompileSettled(Typed(inFlight), Options)
            .Should().BeFalse("a compile in flight is transitional, never a terminal to bind");

        NodeTypeEnrichmentHelpers.IsCompileSettled(AsJsonElement(inFlight), Options)
            .Should().BeFalse(
                "the in-flight guard must survive the un-materialized shape — otherwise the "
                + "instance freezes onto a mid-recompile configuration");
    }

    [Fact]
    public void TerminalStates_AreSettled_WhateverShapeContentArrivesIn()
    {
        // A genuine compile failure IS terminal — this is what lets the compilation-error overlay
        // reach the instance page instead of the default config.
        foreach (var node in new[]
                 {
                     Typed(Dynamic(CompilationStatus.Error)),
                     AsJsonElement(Dynamic(CompilationStatus.Error)),
                     AsJsonObject(Dynamic(CompilationStatus.Error)),
                 })
            NodeTypeEnrichmentHelpers.IsCompileSettled(node, Options).Should().BeTrue();

        // Unavailable is terminal too: a driver already gave up determining the state.
        NodeTypeEnrichmentHelpers
            .IsCompileSettled(AsJsonElement(Dynamic(CompilationStatus.Unavailable)), Options)
            .Should().BeTrue();

        // Ok is terminal ONLY with assembly coordinates — a bare Ok is the stale shape the
        // self-heal exists for, and must keep waiting.
        var okNoAssembly = Dynamic(CompilationStatus.Ok);
        NodeTypeEnrichmentHelpers.IsCompileSettled(AsJsonElement(okNoAssembly), Options)
            .Should().BeFalse();

        var okWithAssembly = Dynamic(CompilationStatus.Ok) with
        {
            LatestAssemblyCollection = "local",
            LatestAssemblyPath = "type_SettleProbe/v1-abc.dll",
        };
        NodeTypeEnrichmentHelpers.IsCompileSettled(AsJsonElement(okWithAssembly), Options)
            .Should().BeTrue();
    }

    [Fact]
    public void NoCompileComing_IsSettled_SoASourcelessTypeDoesNotBurnTheBudget()
    {
        // No Configuration, no HubConfiguration, no Sources and no settled state: no Pending will
        // ever fire, so waiting would only burn the full no-progress budget.
        var sourceless = new NodeTypeDefinition { Description = "seeded, nothing to compile" };

        NodeTypeEnrichmentHelpers.IsCompileSettled(Typed(sourceless), Options).Should().BeTrue();
        NodeTypeEnrichmentHelpers.IsCompileSettled(AsJsonElement(sourceless), Options).Should().BeTrue();
    }

    [Fact]
    public void ContentThatIsNotANodeTypeDefinitionAtAll_IsSettled()
    {
        // The escape now means what it says: ContentAs could not produce a definition. A plain node
        // used as a type path has nothing to wait for — ApplyStreamResult applies the default
        // config for it deliberately.
        var plain = new MeshNode(NodeTypePath) { Version = 3, Content = "not a node type" };
        NodeTypeEnrichmentHelpers.IsCompileSettled(plain, Options).Should().BeTrue();

        var contentless = new MeshNode(NodeTypePath) { Version = 3 };
        NodeTypeEnrichmentHelpers.IsCompileSettled(contentless, Options).Should().BeTrue();
    }

    /// <summary>
    /// The sibling predicate driving the self-heal recompile wait carries the identical blindness:
    /// an un-materialized in-flight snapshot answered "settled", burning the single retry attempt
    /// before the recompile it had just kicked off could start.
    /// </summary>
    [Fact]
    public void RecompileSettle_InFlight_IsNotSettled_WhateverShapeContentArrivesIn()
    {
        var inFlight = Dynamic(CompilationStatus.Compiling);

        NodeTypeEnrichmentHelpers
            .IsRecompileSettled(Typed(inFlight), staleVersion: null, requireUsableBuild: false, Options)
            .Should().BeFalse();

        NodeTypeEnrichmentHelpers
            .IsRecompileSettled(AsJsonElement(inFlight), staleVersion: null, requireUsableBuild: false, Options)
            .Should().BeFalse(
                "the un-materialized shape is the same in-flight state — answering 'settled' here "
                + "burns the single MaxRecompileAttempts retry before the recompile even starts");
    }
}
