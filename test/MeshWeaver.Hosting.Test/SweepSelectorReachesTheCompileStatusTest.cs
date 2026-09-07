using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Persistence.Query;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using Xunit;

namespace MeshWeaver.Hosting.Test;

/// <summary>
/// 🔴 <b>The pre-deploy sweep's SELECTOR, pinned — Systemorph/MeshWeaver#3472.</b>
///
/// <para>AGENTS.md names one instrument for "is this mesh safe to deploy":
/// <c>search 'nodeType:NodeType compilationStatus:Error'</c>, ONE non-truncated call. A sweep is
/// only worth anything if a broken type makes it answer — and a sweep that returns <c>count: 0</c>
/// because its selector resolves to <c>null</c> is indistinguishable from a clean mesh. That is the
/// same defect shape as a CI gate that skips on a missing input: "it never ran" and "it passed"
/// paint the same colour.</para>
///
/// <para>🚨 <b>The finding is NOT "the sweep is broken" — it is that the two query
/// providers disagree about which selectors exist, and nothing compares them</b>
/// (Systemorph/MeshWeaver#3511). Both halves are measured, and they disagree:</para>
///
/// <list type="bullet">
///   <item><b>In-memory / FileSystem</b> (<see cref="QueryEvaluator"/>, what a dev Monolith and
///     most test meshes run): the BARE selector reaches nothing.
///     <see cref="QueryEvaluator.GetPropertyValue"/> resolves by reflection against the object it
///     is handed — a <see cref="MeshNode"/> — which has no <c>compilationStatus</c> property and
///     no <c>Content</c> fallback, so the comparison is <c>null == "Error"</c>: false for every
///     node. The DOTTED selector splits on <c>.</c>, walks into <c>Content</c>, and does reach it.
///     That is what the three cases below pin.</item>
///   <item><b>Postgres</b> (<c>PostgreSqlSqlGenerator</c>, not in this repository): the bare
///     selector DISCRIMINATES. Measured on a live mesh by a peer, 2026-09-07 —
///     <c>compilationStatus:Error</c> returned 5, <c>compilationStatus:Ok</c> returned 195, and
///     none of the five were among the 195.</item>
/// </list>
///
/// <para><b>So the sweep means different things in different places</b>, and that is worse than a
/// uniformly broken one: on the portal a deploy is actually gated on it works, while a rehearsal
/// on a local or CI mesh is green by construction and proves nothing about it. Closing the gap
/// properly is one test exercising BOTH providers against the same records — #3511's scope, of
/// which this file is the first half. It deliberately claims nothing about the Postgres provider
/// it cannot execute.</para>
/// </summary>
public class SweepSelectorReachesTheCompileStatusTest
{
    private static MeshNode BrokenType() =>
        new("Offer", "Crm")
        {
            NodeType = MeshNode.NodeTypePath,
            Content = new NodeTypeDefinition
            {
                Configuration = "config => config",
                CompilationStatus = CompilationStatus.Error,
                CompilationError = "CS0103: the name 'x' does not exist in the current context",
            },
        };

    [Fact]
    public void TheDottedSelectorReachesTheCompileStatus()
        => new QueryEvaluator().GetPropertyValue(BrokenType(), "content.compilationStatus")
            ?.ToString()
            .Should().Be("Error",
                "the sweep has to be able to SEE a broken type. The evaluator walks a dotted "
                + "selector part by part, so 'content' resolves MeshNode.Content and "
                + "'compilationStatus' then resolves NodeTypeDefinition.CompilationStatus "
                + "case-insensitively — and the comparison is done on ToString(), so the enum name "
                + "matches the query's literal");

    [Fact]
    public void TheBareSelectorDoesNotReachIt_WhichIsWhyAGreenSweepHereIsNotEvidence()
        => new QueryEvaluator().GetPropertyValue(BrokenType(), "compilationStatus")
            .Should().BeNull(
                "🚨 MeshNode has no such property and this evaluator has no Content fallback, so "
                + "the bare selector compares null against 'Error' and matches NOTHING — a mesh "
                + "full of broken types answers count: 0, which reads exactly like a clean one. "
                + "On Postgres the same query DISCRIMINATES (5 Error / 195 Ok, disjoint, measured "
                + "on a live mesh), so this is a provider DISAGREEMENT, not a broken sweep — and "
                + "the disagreement is the dangerous part, because it makes a green rehearsal on "
                + "a local mesh look like a green sweep on the portal. Pinned rather than fixed: "
                + "giving the evaluator a Content fallback would change the meaning of every "
                + "selector in every query in the platform, and comparing the two providers needs "
                + "a test that can run both (#3511)");

    [Fact]
    public void AHealthyTypeIsNotReportedBroken()
        => new QueryEvaluator()
            .GetPropertyValue(
                new MeshNode("Client", "Crm")
                {
                    NodeType = MeshNode.NodeTypePath,
                    Content = new NodeTypeDefinition
                    {
                        Configuration = "config => config",
                        CompilationStatus = CompilationStatus.Ok,
                    },
                },
                "content.compilationStatus")
            ?.ToString()
            .Should().Be("Ok",
                "THE CONTROL — a selector that answered 'Error' for everything would satisfy the "
                + "first assertion and make the sweep useless in the other direction");
}
