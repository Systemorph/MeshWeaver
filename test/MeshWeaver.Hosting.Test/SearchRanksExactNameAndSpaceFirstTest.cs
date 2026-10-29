using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using MeshWeaver.Hosting.Persistence;
using MeshWeaver.Hosting.Persistence.Query;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using Xunit;

namespace MeshWeaver.Hosting.Test;

/// <summary>
/// Systemorph/MeshWeaver#7381 — free-text search ranks exact-name and Space matches first, on the
/// in-memory executor. The cases are <see cref="SearchRankingCorpus"/>, which the Postgres executor
/// in MeshWeaver.Plugins runs too. Runs through <see cref="MeshQuery"/> over the in-memory store, so
/// the provider's clip AND the aggregator's merge are both on the path under test.
/// </summary>
public class SearchRanksExactNameAndSpaceFirstTest
{
    private static readonly JsonSerializerOptions Options = new();
    private static readonly QueryEvaluator Evaluator = new();

    private static MeshNode Node(SearchRankingNode n) => new(n.Id, SearchRankingCorpus.Namespace)
    {
        Name = n.Name,
        NodeType = n.NodeType,
        State = MeshNodeState.Active,
        LastModified = SearchRankingCorpus.Epoch.AddMinutes(n.MinutesAfterEpoch),
        Content = n.Text is null
            ? null
            : JsonSerializer.Deserialize<object>(JsonSerializer.Serialize(new { text = n.Text }), Options),
    };

    private static async Task<IMeshQueryCore> MeshAsync(CancellationToken ct)
    {
        var store = new InMemoryStorageAdapter();
        foreach (var n in SearchRankingCorpus.Nodes)
            await store.Write(Node(n), Options).Await(ct);
        return new MeshQuery([new StorageAdapterMeshQueryProvider(persistence: store)], hub: null!);
    }

    private static async Task<IReadOnlyList<string>> Ids(IMeshQueryCore query, string text, CancellationToken ct)
    {
        var change = await query
            .Query<MeshNode>(new MeshQueryRequest { Query = $"namespace:{SearchRankingCorpus.Namespace} {text}" }, Options)
            .FirstAsync()
            .Timeout(TestTimeouts.Convergence)
            .Await(ct);
        return change.Items.Select(n => n.Id).ToList();
    }

    public static TheoryData<string> Texts()
    {
        var data = new TheoryData<string>();
        foreach (var c in SearchRankingCorpus.Cases)
            data.Add(c.Text);
        return data;
    }

    [Theory]
    [MemberData(nameof(Texts))]
    public async Task EveryCorpusCaseLeadsWithItsExpectedRows(string text)
    {
        var ct = TestContext.Current.CancellationToken;
        var expected = SearchRankingCorpus.Cases.Single(c => c.Text == text);

        var ids = await Ids(await MeshAsync(ct), text, ct);

        ids.Take(expected.Leading.Length).Should().Equal(expected.Leading, $"`{text}`: {expected.Note}");
        ids.Skip(expected.Leading.Length).Should().Contain(expected.Below,
            $"`{text}`: the lower-tier matches are still found, below the leading rows");
    }

    /// <summary>
    /// The cases are only evidence if the executor's score ALONE would have put a different row
    /// first. Without this, a corpus that the old ranking already satisfied would pass with the tier
    /// removed and prove nothing.
    /// </summary>
    [Theory]
    [MemberData(nameof(Texts))]
    public void TheFuzzyScoreAloneWouldNotLeadWithTheExpectedRow(string text)
    {
        var expected = SearchRankingCorpus.Cases.Single(c => c.Text == text);
        var fuzzyFirst = SearchRankingCorpus.Nodes
            .Select(Node)
            .Where(n => Evaluator.Matches(n, new QueryParser().Parse(text)))
            .OrderByDescending(n => Evaluator.GetFuzzyScore(n, text))
            .ThenBy(n => n.Path, StringComparer.Ordinal)
            .First().Id;

        fuzzyFirst.Should().NotBe(expected.Leading[0],
            $"`{text}` must discriminate: the tier, not the fuzzy score, has to be what puts `{expected.Leading[0]}` first");
    }

    /// <summary>
    /// Negative control: a query none of whose matches is a Space or an exact name keeps exactly the
    /// order it had before the rule — the fuzzy score descending, then path.
    /// </summary>
    [Fact]
    public async Task AnUnrelatedQuery_KeepsItsTierFreeOrder()
    {
        var ct = TestContext.Current.CancellationToken;
        var text = SearchRankingCorpus.UnrelatedText;

        var ids = await Ids(await MeshAsync(ct), text, ct);

        var nodes = SearchRankingCorpus.Nodes.Select(Node).ToList();
        var tierFree = nodes
            .Where(n => Evaluator.Matches(n, new QueryParser().Parse(text)))
            .OrderByDescending(n => Evaluator.GetFuzzyScore(n, text))
            .ThenBy(n => n.Path, StringComparer.Ordinal)
            .Select(n => n.Id)
            .ToList();

        nodes.Where(n => ids.Contains(n.Id)).Select(n => SearchRanking.Tier(n, text))
            .Should().AllSatisfy(t => t.Should().Be(0), "the control must exercise no tier at all");
        ids.Order().Should().Equal(SearchRankingCorpus.UnrelatedMatches.Order());
        ids.Should().Equal(tierFree, "with every tier 0 the rule must leave the order untouched");
    }

    [Theory]
    [InlineData("Compliance", "sp-7", "Compliance", "Space", 3)]
    [InlineData("  compliance ", "x", "Compliance", "Markdown", 2)]
    [InlineData("compliance", "compliance", "Something else", "Markdown", 2)]
    [InlineData("Compliance", "x", "Compliance team", "Space", 1)]
    [InlineData("Compliance", "x", "Compliance team", "Markdown", 0)]
    [InlineData("", "x", "Compliance", "Space", 0)]
    [InlineData(null, "x", "Compliance", "Space", 0)]
    public void TheTier_IsExactNameOrIdPlusSpace(string? text, string id, string name, string nodeType, int tier)
        => SearchRanking.Tier(name, id, nodeType, text).Should().Be(tier);
}
