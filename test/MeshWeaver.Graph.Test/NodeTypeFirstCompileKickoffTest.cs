using System;
using System.Reactive.Linq;
using System.Threading.Tasks;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// 🚨 "A compile that will never start" and "a compile that has not been KICKED OFF yet" are
/// different states — issue #3006, the <c>area-not-found</c> half of #2952.
///
/// <para><b>The branch.</b> <c>NodeTypeEnrichmentHelpers.ApplyStreamResult</c> treats a
/// <see cref="NodeTypeDefinition"/> with no <c>CompilationStatus</c> and no <c>HubConfiguration</c>
/// delegate as a test-seeded / framework type and hands the instance back with NO node
/// configuration, so the hub factory composes <c>MeshConfiguration.DefaultNodeHubConfiguration</c>
/// (Overview / Thumbnail / Settings / Search — nothing type-specific). An absent
/// <c>CompilationStatus</c> is all these two states have in common: a NodeType loaded from a repo
/// or from JSON carries none until <c>InstallCompileWatcher</c>'s first-build kickoff stamps
/// <c>Pending</c>, and an instance activating inside that window took this branch while one
/// activating a moment later took the healthy in-flight path.</para>
///
/// <para><b>Why the loser never recovers.</b> The binding is PERMANENT, by three independent
/// mechanisms: <c>EnrichWithNodeType</c> short-circuits at the top once <c>HubConfiguration</c> is
/// set; <c>NodeTypeRebindWatcher</c> recycles only on a change of the INSTANCE's own
/// <c>MeshNode.NodeType</c>, never on a compile transition, and bails outright when the config is
/// null; and this branch — alone among its siblings (in-flight, ABI-stale, bytes-missing,
/// execution-refused) — returned UNWRAPPED by <c>WithOverlaySelfHeal</c>, so nothing recycled the
/// instance when the build later settled.</para>
///
/// <para><b>Why re-typing the content (#2952) does not cover it.</b> A NodeType's
/// <c>configuration</c> is ONE expression — <c>PandasExplorer.json</c> declares
/// <c>config =&gt; config.WithContentType&lt;PandasExplorer&gt;().AddLayout(layout =&gt;
/// layout.AddPandasExplorerLayoutAreas().WithDefaultArea("Explorer"))</c> — so content typing and
/// area registration are two side effects of ONE configuration build, not cause and effect. An
/// instance that bound it has BOTH; one that did not has NEITHER. Re-typing the content afterwards
/// adds no renderer, and every deep link to a NodeType-declared area answers the TERMINAL
/// <c>area-not-found</c> verdict where the transient compile-progress promise is the true one.</para>
///
/// <para><b>Deterministic by construction.</b> The verdict is asserted on
/// <c>ApplyStreamResult</c> — the method that MAKES it — against a NodeType node built in memory.
/// Driving this through <c>EnrichWithNodeType</c> would mean running a real Roslyn compile, so the
/// assertion would measure the compiler's timing rather than this decision. Same reason
/// <c>IsCompileSettled</c> and <c>PreferAuthoritative</c> are exposed and tested directly.</para>
/// </summary>
public class NodeTypeFirstCompileKickoffTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string NodeTypePath = $"{TestPartition}/PandasExplorerProbe";

    private static readonly TimeSpan VerdictBudget = TimeSpan.FromSeconds(10);

    private static MeshConfiguration EmptyMeshConfiguration() => new(Array.Empty<MeshNode>());

    /// <summary>The mesh's real compiler. Registered by <c>AddGraph()</c>, so a Monolith mesh —
    /// like every portal — genuinely CAN run a compile.</summary>
    private IMeshNodeCompilationService Compiler =>
        Mesh.ServiceProvider.GetRequiredService<IMeshNodeCompilationService>();

    /// <summary>
    /// The shape of <c>Data/DataMesh/PythonPandasNode/PandasExplorer.json</c> as it exists between
    /// being loaded and the first-build kickoff stamping <c>Pending</c>: source to build, no
    /// compile state recorded, and no in-process delegate (it is not a static-provider type).
    /// </summary>
    private static MeshNode AwaitingKickoff() => new(NodeTypePath)
    {
        Version = 1,
        Content = new NodeTypeDefinition
        {
            Description = "declares a content type AND its layout areas in one expression",
            Configuration =
                "config => config.WithContentType<PandasExplorer>()"
                + ".AddLayout(layout => layout.AddPandasExplorerLayoutAreas().WithDefaultArea(\"Explorer\"))",
        },
    };

    /// <summary>A pure MARKER type: it names a shape and ships no code, so no compile is EVER
    /// coming for it. This is the state the branch was written for.</summary>
    private static MeshNode Marker() => new(NodeTypePath)
    {
        Version = 1,
        Content = new NodeTypeDefinition { Description = "a marker type — nothing to compile" },
    };

    private static MeshNode Instance() =>
        new("explorer1", TestPartition) { NodeType = NodeTypePath };

    private Task<MeshNode> Verdict(MeshNode typeNode, IMeshNodeCompilationService? compiler) =>
        NodeTypeEnrichmentHelpers
            .ApplyStreamResult(
                typeNode, Instance(), NodeTypePath, EmptyMeshConfiguration(),
                compiler, Mesh, logger: null)
            .Take(1)
            .Should().Within(VerdictBudget).Emit("ApplyStreamResult must always reach a verdict");

    /// <summary>
    /// 🚨 THE REGRESSION. A NodeType that HAS source to build, on a mesh that can actually build
    /// it, whose first-build kickoff has not stamped <c>Pending</c> yet.
    ///
    /// <para>RED before the fix: <c>HubConfiguration</c> came back <c>null</c>, i.e. the instance
    /// was handed to the factory to be bound to the mesh default chain for the grain's whole life,
    /// with no self-heal to un-pin it. GREEN after: it binds the compile-progress overlay, whose
    /// catch-all renderer answers every area and whose <c>WithOverlaySelfHeal</c> wrapper recycles
    /// the instance onto the real configuration the moment the build lands.</para>
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task ParticipatingTypeAwaitingItsFirstKickoff_IsNotPinnedToTheDefaultConfiguration()
    {
        var verdict = await Verdict(AwaitingKickoff(), Compiler);

        verdict.HubConfiguration.Should().NotBeNull(
            "this NodeType declares a Configuration expression and this mesh has a compilation "
            + "service, so a compile IS coming — the instance must serve the transient "
            + "compile-progress overlay (which answers every area and self-heals onto the real "
            + "configuration when the build lands), NOT be handed to the factory with no node "
            + "configuration. Binding the default chain here is permanent: enrichment binds once, "
            + "and NodeTypeRebindWatcher only fires on a change of the instance's own NodeType. "
            + "Its layout areas would never appear and every deep link to one would answer the "
            + "TERMINAL area-not-found verdict — the area-not-found half of #2952 that re-typing "
            + "the content cannot reach, because the areas and the content type are two side "
            + "effects of the SAME configuration build.");
    }

    /// <summary>
    /// 🚨 THE OTHER HALF, and the reason #3006 was held back from #2952's PR: a test-seeded
    /// NodeType that carries a <c>Configuration</c> string but runs on a mesh with NO
    /// <c>IMeshNodeCompilationService</c> must NOT be parked on a progress page for a build that
    /// will never run. There the original behaviour is correct and is kept unchanged.
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task ParticipatingTypeOnAMeshThatCannotCompile_StillBindsTheDefaultChain()
    {
        var verdict = await Verdict(AwaitingKickoff(), compiler: null);

        verdict.HubConfiguration.Should().BeNull(
            "with no compilation service registered no compile can ever run, so a compile-progress "
            + "overlay would be a page about a build that is not coming. The instance must still "
            + "activate on the factory's default chain — which is what makes seeded NodeTypes in "
            + "unit-test meshes work at all.");
    }

    /// <summary>
    /// A pure marker type — the state the branch was actually written for — is unaffected even on a
    /// mesh that CAN compile. Without this the fix would widen into "every statusless NodeType gets
    /// a progress page", which is the half-understood change #3006 warns against.
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task MarkerTypeWithNoSource_StillBindsTheDefaultChain()
    {
        var verdict = await Verdict(Marker(), Compiler);

        verdict.HubConfiguration.Should().BeNull(
            "a definition with no Configuration, no HubConfiguration source and no Sources has "
            + "nothing to compile, so no Pending will ever fire for it — waiting on, or painting a "
            + "page about, a build that cannot start is exactly the defect in the opposite "
            + "direction");
    }
}

/// <summary>
/// The predicate the enrichment decision above turns on, asserted on its own — no mesh, no hub.
///
/// <para>It is single-sourced deliberately (#3006). The rule already existed inline in
/// <c>NodeTypeLayoutAreas.AppendSweepSummary</c> ("Only types that participate in compilation"),
/// and a NodeType the sweep summary counts as compiling while enrichment treats it as inert is
/// precisely the disagreement that pinned an instance to the default configuration.</para>
/// </summary>
public class ParticipatesInCompilationTest
{
    [Fact]
    public void AConfigurationExpression_Participates() =>
        NodeTypeDefinition.ParticipatesInCompilation(
                new NodeTypeDefinition { Configuration = "config => config" })
            .Should().BeTrue("a Configuration string is source that will be Roslyn-compiled");

    [Fact]
    public void AHubConfigurationExpression_Participates() =>
        NodeTypeDefinition.ParticipatesInCompilation(
                new NodeTypeDefinition { HubConfiguration = "config => config" })
            .Should().BeTrue("the HubConfiguration STRING is source too — unlike MeshNode's "
                + "HubConfiguration delegate, which is an already-built configuration");

    [Fact]
    public void SourceNodes_Participate() =>
        NodeTypeDefinition.ParticipatesInCompilation(
                new NodeTypeDefinition { Sources = ["Source/Area.cs"] })
            .Should().BeTrue();

    [Fact]
    public void ARecordedCompileState_Participates() =>
        NodeTypeDefinition.ParticipatesInCompilation(
                new NodeTypeDefinition { CompilationStatus = CompilationStatus.Error })
            .Should().BeTrue("a type that has already been through a compile is a participant "
                + "whatever the verdict was");

    [Fact]
    public void AMarkerTypeWithNoSourceAndNoState_DoesNot() =>
        NodeTypeDefinition.ParticipatesInCompilation(
                new NodeTypeDefinition { Description = "just a marker" })
            .Should().BeFalse();

    [Fact]
    public void WhitespaceIsNotSource() =>
        NodeTypeDefinition.ParticipatesInCompilation(
                new NodeTypeDefinition { Configuration = "   " })
            .Should().BeFalse("blank is not a lambda — IsNullOrWhiteSpace, not IsNullOrEmpty");

    [Fact]
    public void AnEmptySourceList_IsNotSource() =>
        NodeTypeDefinition.ParticipatesInCompilation(new NodeTypeDefinition { Sources = [] })
            .Should().BeFalse();

    [Fact]
    public void NullIsNotAParticipant() =>
        NodeTypeDefinition.ParticipatesInCompilation(null).Should().BeFalse(
            "content that could not be read as a NodeTypeDefinition at all is not a NodeType "
            + "awaiting a compile");
}
