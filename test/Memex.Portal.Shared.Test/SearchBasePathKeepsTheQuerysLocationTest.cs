using System.Reactive.Linq;
using System.Text.Json;
using MeshWeaver.AI;   // MeshOperations — its namespace is a frozen binary contract (#2370)
using MeshWeaver.Fixture;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using Xunit;

namespace Memex.Portal.Shared.Test;

/// <summary>
/// 🚨 <c>basePath</c> never rewrites the query's OWN location (#6828). Measured on a portal: a
/// query anchored at one space (<c>namespace:&lt;space B&gt; …</c>) sent with <c>basePath</c> at
/// another reported <c>coverage.partitions</c> of the base's partition only — because
/// <c>MeshOperations.Search</c> deleted every <c>namespace:</c> from the text before prefixing the
/// base, turning the query's location into a FREE-TEXT term read inside the wrong partition, and
/// an exclusion (<c>-namespace:X</c>) into a search FOR X.
///
/// <para>Now a query that names its own location keeps it when it lies under the base, is refused
/// naming both when it does not, and only a query that names none is scoped by the base.</para>
/// </summary>
public class SearchBasePathKeepsTheQuerysLocationTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string Alpha = "BaseAlpha";
    private const string Beta = "BaseBeta";
    private const string Person = "Jane Q Example";

    private static TimeSpan Budget => TestTimeouts.Convergence;

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder).AddMeshNodes(
            new MeshNode(Alpha) { Name = "Alpha", NodeType = "Markdown" },
            new MeshNode(Beta) { Name = "Beta", NodeType = "Markdown" },
            new MeshNode("Deal", Beta) { Name = "Deal", NodeType = "Markdown" },
            new MeshNode("Jane", $"{Beta}/Deal") { Name = Person, NodeType = "Markdown" },
            new MeshNode("Archive", Beta) { Name = "Archive", NodeType = "Markdown" },
            new MeshNode("Jane", $"{Beta}/Archive") { Name = Person, NodeType = "Markdown" });

    private Task<string> Search(string query, string? basePath, CancellationToken cancellationToken)
        => new MeshOperations(Mesh).Search(query, basePath, 50)
            .FirstAsync().Timeout(Budget).Await(cancellationToken);

    private static string[] Paths(string answer)
        => JsonDocument.Parse(answer).RootElement.GetProperty("results").EnumerateArray()
            .Select(r => r.GetProperty("path").GetString()!).OrderBy(p => p, StringComparer.Ordinal).ToArray();

    private static string[] Partitions(string answer)
        => JsonDocument.Parse(answer).RootElement.GetProperty("coverage").GetProperty("partitions")
            .EnumerateArray().Select(p => p.GetString()!).ToArray();

    /// <summary>
    /// The measured shape: the query names space B, the base is space A. It used to be answered
    /// inside A, with A's coverage. No answer can honour both, so it is refused, naming both.
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task AQueryAnchoredOutsideTheBaseIsRefused_NotReadInTheBasesPartition()
    {
        var answer = await Search($"namespace:{Beta} scope:descendants name:\"{Person}\"", $"@{Alpha}",
            TestContext.Current.CancellationToken);

        Output.WriteLine(answer);
        answer.Should().StartWith("Error:", "a query read against a partition it did not name is the defect");
        answer.Should().Contain(Alpha).And.Contain(Beta, "the refusal names both locations");
        answer.Should().NotContain("\"count\"");
    }

    /// <summary>A query anchored INSIDE the base keeps its own, narrower anchor.</summary>
    [Fact(Timeout = 60_000)]
    public async Task AQueryAnchoredUnderTheBaseKeepsItsOwnAnchor()
    {
        var answer = await Search($"namespace:{Beta}/Deal name:\"{Person}\"", $"@{Beta}",
            TestContext.Current.CancellationToken);

        Output.WriteLine(answer);
        Paths(answer).Should().Equal(new[] { $"{Beta}/Deal/Jane" }, "the query's own namespace, not the whole base");
        Partitions(answer).Should().Equal(Beta);
    }

    /// <summary>An exclusion stays an exclusion; it used to become a free-text search FOR the namespace.</summary>
    [Fact(Timeout = 60_000)]
    public async Task AnExclusionUnderABaseStillExcludes()
    {
        var answer = await Search($"-namespace:{Beta}/Archive name:\"{Person}\"", $"@{Beta}",
            TestContext.Current.CancellationToken);

        Output.WriteLine(answer);
        Paths(answer).Should().Equal(new[] { $"{Beta}/Deal/Jane" }, "the archived twin is excluded, the live one is not");
    }

    /// <summary>
    /// The controls on the base itself: a query with no location of its own is scoped to the whole
    /// subtree, and an empty <c>namespace:</c> (callers' "the base's children" idiom) is no location.
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task AQueryWithNoLocationIsScopedByTheBase()
    {
        var ct = TestContext.Current.CancellationToken;

        var both = await Search($"name:\"{Person}\"", $"@{Beta}", ct);
        Output.WriteLine(both);
        Paths(both).Should().Equal($"{Beta}/Archive/Jane", $"{Beta}/Deal/Jane");
        Partitions(both).Should().Equal(Beta);

        var elsewhere = await Search($"name:\"{Person}\"", $"@{Alpha}", ct);
        JsonDocument.Parse(elsewhere).RootElement.GetProperty("count").GetInt32().Should().Be(0, "nobody of that name lives in Alpha");

        var children = await Search("namespace:", $"@{Beta}", ct);
        Output.WriteLine(children);
        Paths(children).Should().Contain(new[] { $"{Beta}/Deal", $"{Beta}/Archive" });
        Paths(children).Should().OnlyContain(p => p.StartsWith(Beta + "/", StringComparison.Ordinal));
    }
}
