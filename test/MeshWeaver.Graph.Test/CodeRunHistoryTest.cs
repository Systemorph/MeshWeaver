using System;
using System.Linq;
using System.Reactive.Linq;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Mesh;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// #3301 — the pure half of "a cell can find its own runs without the last-execution stamp".
///
/// <para><b>What is being pinned.</b> <see cref="CodeRunHistory"/> follows the cell → run edge that
/// the DISPATCHER writes (<c>ActivityLog.HubPath</c> = the Code node's path) instead of the
/// denormalised pointer the STAMP writes. Two things have to hold for that to work at all, and
/// neither is visible to a compiler: the query has to name the vocabulary the dispatcher actually
/// used, and the <c>{viewer}</c> sentinel has to expand to the same place on both sides. Both are
/// cross-ASSEMBLY agreements — <c>MeshWeaver.Mesh.Contract</c> sits below
/// <c>MeshWeaver.Graph</c>/<c>MeshWeaver.Graph.Contract</c>, so the lookup cannot reference the
/// dispatcher's own constants and would otherwise agree with it only by luck.</para>
///
/// <para>The through-the-mesh half — that a real run really is found after its stamp is wiped — is
/// <see cref="CodeCellCurrencyThroughTheMeshTest.ARunWhoseStampNeverLanded_IsStillFoundByTheActivityItWrote"/>.</para>
/// </summary>
public class CodeRunHistoryTest
{
    private const string Cell = "acme/notebook/cell-7";

    /// <summary>
    /// 🚨 The lookup filters on <c>nodeType:Activity</c>, and that string is DECLARED in
    /// <c>MeshWeaver.Graph.Contract</c> — an assembly the lookup cannot reference. A rename there
    /// would leave <see cref="CodeRunHistory.RunsQuery"/> querying a node type nothing writes any
    /// more: zero rows, every recovered cell silently back to <see cref="CodeOutputCurrency.NeverRun"/>,
    /// and no compiler error anywhere. This test is the only thing that would notice.
    /// </summary>
    [Fact]
    public void TheActivityNodeTypeNameMatchesTheGraphVocabulary() =>
        CodeRunHistory.ActivityNodeTypeName.Should().Be(GraphNodeTypeNames.Activity,
            "the lookup's nodeType filter must name the type the dispatcher actually creates — "
            + "MeshWeaver.Mesh.Contract cannot reference GraphNodeTypeNames, so this equality is "
            + "the only thing holding the two spellings together");

    /// <summary>
    /// 🚨 The other cross-assembly agreement, pinned BEHAVIOURALLY rather than by comparing two
    /// literals: the sentinel <see cref="CodeRunHistory.ActivityNamespaces"/> expands must be the
    /// one <c>CodeNodeType.ResolveActivityParent</c> expands, and to the same target. Drive the
    /// dispatcher's own resolver and require the answer the lookup assumes.
    /// </summary>
    [Fact]
    public async Task TheViewerSentinelExpandsTheSameWayOnBothSides()
    {
        var resolved = await CodeNodeType.ResolveActivityParent(
                Observable.Return<PartitionDefinition?>(null),
                CodeRunHistory.ViewerHomeSentinel,
                viewerHome: "rbuergi",
                partitionRoot: "acme",
                lookupBudget: TimeSpan.FromSeconds(5))
            .FirstAsync()
            .Timeout(TimeSpan.FromSeconds(10))
            .Await();

        resolved.Should().Be("rbuergi",
            "the dispatcher writes the run into the VIEWER's home when the sentinel is configured, "
            + "so a lookup that expanded it to the cell's own partition would search the one place "
            + "the activity is not");

        CodeRunHistory.ActivityNamespaces(
                Cell,
                new CodeConfiguration { ActivityParentPath = CodeRunHistory.ViewerHomeSentinel },
                viewerHome: "rbuergi")
            .Should().Contain($"{resolved}/_Activity",
                "the lookup must search exactly where the resolver said the run was written");
    }

    /// <summary>
    /// The default: no override, so the dispatcher writes to the cell's own partition root and the
    /// lookup searches exactly one namespace. The common case must not fan out.
    /// </summary>
    [Fact]
    public void WithNoOverrideTheLookupSearchesOnlyTheCellsOwnPartition() =>
        CodeRunHistory.ActivityNamespaces(Cell, new CodeConfiguration(), viewerHome: null)
            .Should().Equal("acme/_Activity");

    /// <summary>
    /// A viewer reading someone else's cell adds their own home — the place a <c>{viewer}</c>-routed
    /// run would have landed — and nothing else. Two namespaces, one union query.
    /// </summary>
    [Fact]
    public void AForeignViewerAlsoSearchesTheirOwnHome() =>
        CodeRunHistory.ActivityNamespaces(Cell, new CodeConfiguration(), viewerHome: "rbuergi")
            .Should().Equal("acme/_Activity", "rbuergi/_Activity");

    /// <summary>
    /// The viewer reading a cell in their OWN partition must not produce the same namespace twice —
    /// a duplicated query is a duplicated read for an answer that cannot differ.
    /// </summary>
    [Fact]
    public void TheViewersOwnPartitionIsNotSearchedTwice() =>
        CodeRunHistory.ActivityNamespaces(Cell, new CodeConfiguration(), viewerHome: "acme")
            .Should().Equal("acme/_Activity");

    /// <summary>
    /// An explicit <see cref="CodeConfiguration.ActivityParentPath"/> is where the dispatcher writes,
    /// so it is searched — and the partition root stays in the set, because runs recorded BEFORE the
    /// override was configured are still there and still this cell's.
    /// </summary>
    [Fact]
    public void AnExplicitActivityParentIsSearchedAlongsideThePartitionRoot() =>
        CodeRunHistory.ActivityNamespaces(
                Cell,
                new CodeConfiguration { ActivityParentPath = "acme/runs" },
                viewerHome: null)
            .Should().Equal("acme/runs/_Activity", "acme/_Activity");

    /// <summary>A path that names no partition cannot be looked up — and must not produce a query
    /// with an empty namespace, which is the unanchored cross-schema shape a provider refuses.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("/")]
    public void APathThatNamesNoPartitionYieldsNoQuery(string? cellPath) =>
        CodeRunHistory.ActivityNamespaces(cellPath, new CodeConfiguration(), viewerHome: null)
            .Should().BeEmpty();

    /// <summary>
    /// The query's shape, spelled out once: the run is found by the edge the DISPATCHER wrote, it is
    /// anchored to a namespace (never an unanchored cross-partition scan), and it asks for existence
    /// — newest first, one row — not for the cell's whole run history.
    /// </summary>
    [Fact]
    public void TheQueryFindsRunsByTheDispatchersEdgeAndAsksOnlyForExistence()
    {
        var query = CodeRunHistory.RunsQuery(Cell, "acme/_Activity");

        query.Should().Contain($"content.hubPath:{Cell}",
            "HubPath is the cell → run edge the dispatcher stamps onto the Activity node BEFORE it "
            + "dispatches — the whole point is that it survives a failed last-execution stamp");
        query.Should().Contain("namespace:acme/_Activity",
            "an unanchored query names no partition and is refused rather than fanned out");
        query.Should().Contain("limit:1").And.Contain("sort:LastModified-desc",
            "the question is existence, so a cell with a thousand runs must still cost one row");
    }
}
