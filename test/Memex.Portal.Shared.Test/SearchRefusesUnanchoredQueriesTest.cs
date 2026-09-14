using System.Reactive.Linq;
using System.Text.Json;
using MeshWeaver.AI;   // MeshOperations — its namespace is a frozen binary contract (#2370)
using MeshWeaver.Fixture;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using Xunit;

namespace Memex.Portal.Shared.Test;

/// <summary>
/// 🚨 THE MCP <c>search</c> TOOL REFUSES AN UNANCHORED QUERY AND STATES ITS COVERAGE. MeshWeaver #4274.
///
/// <para><b>What production showed</b> (memex.systemorph.com, 2026-09-14, one credential, calls
/// seconds apart): <c>nodeType:LogIncident</c> → <c>count: 0, truncated: false</c>;
/// <c>namespace:Admin scope:descendants nodeType:LogIncident</c> → truncated at any limit. Same
/// portal, same nodes, and <c>get @Admin/_LogIncident/…</c> returned them stamped with that exact
/// type. The bare form was served by the Postgres planner's <c>ServeAndReport</c> policy: a
/// cross-schema fan-out over <c>public.searchable_schemas</c>, which by construction never holds the
/// system schemas (<c>admin</c>, <c>auth</c>, …) and is narrowed to the partitions the caller can
/// read — and reported at Error in the portal's OWN log, where the caller never sees it. The
/// envelope the caller got was indistinguishable from a genuine empty result. AGENTS.md's
/// pre-deploy NodeType sweep was written in that form.</para>
///
/// <para><b>The fix is at the entry point, before any backend.</b> <see cref="MeshOperations.Search"/>
/// judges the query with the planner's own CI verdict — <see cref="ParsedQuery.IsSufficientlySpecified"/>
/// or a routing rule names the partition, else refused with the remedy — and the envelope carries
/// <c>coverage</c>: the scope of the read and the partitions it covered, so a zero is read against
/// a denominator. Both halves have a negative control here: the SAME query is answered the moment
/// it anchors or declares, so the refusal cannot be "search is broken", and the deep node is found
/// under <c>basePath</c> only because the scope widened, so the coverage cannot be decorative.</para>
/// </summary>
public class SearchRefusesUnanchoredQueriesTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string Partition = "SweepPartition";
    private const string Marker = "UnanchoredSearchMarker";

    private static TimeSpan Budget => TestTimeouts.Convergence;

    // A node two levels below the partition root: `namespace:{Partition}` alone (immediate
    // children) cannot reach it; `scope:descendants` can. That gap is what basePath used to fall
    // into (#4274, property 1).
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder).AddMeshNodes(
            new MeshNode(Partition) { Name = "Sweep", NodeType = "Markdown" },
            new MeshNode("Deep", Partition) { Name = "Deep", NodeType = "Markdown" },
            new MeshNode("Leaf", $"{Partition}/Deep") { Name = Marker, NodeType = "Markdown" });

    private Task<string> Search(string query, string? basePath = null, int limit = 50)
        => new MeshOperations(Mesh).Search(query, basePath, limit)
            .FirstAsync().Timeout(Budget).Await(TestContext.Current.CancellationToken);

    // ————————————————————————— refusal

    /// <summary>
    /// The shape #4274 measured: a bare <c>nodeType:</c> filter. Refused, naming both remedies —
    /// never a <c>count: 0</c> envelope that reads as "none exist".
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task AnUnanchoredQueryIsRefused_NotAnsweredWithAZero()
    {
        var answer = await Search($"nodeType:Markdown name:{Marker}");

        Output.WriteLine(answer);
        answer.Should().StartWith("Error:", "a partial answer presented as a total is the defect; a refusal is an answer");
        answer.Should().Contain("not sufficiently specified");
        answer.Should().Contain("namespace:", "the remedy names the anchor");
        answer.Should().Contain(ParsedQuery.CrossPartitionQualifier, "and the declaration");
        answer.Should().NotContain("\"count\"", "no envelope, so nothing that could be read as a count");
    }

    /// <summary>
    /// The positive control on the refusal: the SAME filter, anchored, is answered and finds the
    /// node. Without this the refusal test would pass over a search that refuses everything.
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task TheSameQueryAnchoredIsAnswered()
    {
        var answer = await Search($"namespace:{Partition} scope:descendants nodeType:Markdown name:{Marker}");

        Output.WriteLine(answer);
        var envelope = JsonDocument.Parse(answer).RootElement;
        envelope.GetProperty("count").GetInt32().Should().Be(1, "the anchored read reaches the leaf");
        envelope.GetProperty("results")[0].GetProperty("path").GetString().Should().Be($"{Partition}/Deep/Leaf");
        var coverage = envelope.GetProperty("coverage");
        coverage.GetProperty("scope").GetString().Should().Be("partition");
        coverage.GetProperty("partitions").EnumerateArray().Select(p => p.GetString())
            .Should().Equal(new[] { Partition }, "an anchored search states the partition it read");
    }

    /// <summary>
    /// The other remedy: declaring the fan-out is served. On a backend whose provider does not
    /// report the partitions it read (the in-memory store here; a Postgres image before its
    /// reporting half), the coverage says so EXPLICITLY — <c>partitions: null</c> — rather than
    /// implying "all" (the same reasoning as <c>search_chunks</c>' missing <c>count</c> under
    /// <c>"searched": false</c>, #2741).
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task ADeclaredFanOutIsAnswered_AndAnUnreportedDenominatorIsNull()
    {
        var answer = await Search($"nodeType:Markdown name:{Marker} {ParsedQuery.CrossPartitionQualifier}");

        Output.WriteLine(answer);
        var envelope = JsonDocument.Parse(answer).RootElement;
        envelope.GetProperty("count").GetInt32().Should().Be(1);
        var coverage = envelope.GetProperty("coverage");
        coverage.GetProperty("scope").GetString().Should().Be("declared");
        coverage.GetProperty("partitions").ValueKind.Should().Be(JsonValueKind.Null,
            "no provider reported what it read, and an unknown denominator must never read as 'all'");
    }

    /// <summary>
    /// A query a routing rule pins (<c>nodeType:User</c> → the Auth mirror, registered by
    /// <c>UserNodeType</c>) is served, exactly as the planner serves it, and the envelope names the
    /// partition the rule chose — which is how a reader learns that a zero there was read against
    /// the mirror and not against the mesh.
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task AQueryARoutingRulePinsIsServed_AndTheEnvelopeNamesThatPartition()
    {
        var answer = await Search("nodeType:User");

        Output.WriteLine(answer);
        answer.Should().NotStartWith("Error:", "the planner itself serves a rule-routed query");
        var coverage = JsonDocument.Parse(answer).RootElement.GetProperty("coverage");
        coverage.GetProperty("scope").GetString().Should().Be("routed");
        coverage.GetProperty("partitions").EnumerateArray().Select(p => p.GetString())
            .Should().Equal(new[] { "Auth" }, "UserNodeType's rule routes a path-less nodeType:User to the auth mirror");
    }

    // ————————————————————————— basePath

    /// <summary>
    /// <c>basePath</c> means "search FROM here" — the whole subtree. It used to become a bare
    /// <c>namespace:</c>, i.e. immediate children, so <c>basePath: @Admin</c> +
    /// <c>nodeType:LogIncident</c> answered 0 for nodes at <c>Admin/_LogIncident/…</c>: the
    /// parameter whose documented purpose is scoping did not reach what it scoped to.
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task BasePathReachesTheWholeSubtree()
    {
        var answer = await Search($"nodeType:Markdown name:{Marker}", $"@{Partition}");

        Output.WriteLine(answer);
        var envelope = JsonDocument.Parse(answer).RootElement;
        envelope.GetProperty("count").GetInt32().Should().Be(1, "the leaf is two levels below the base path");
        envelope.GetProperty("coverage").GetProperty("partitions").EnumerateArray().Select(p => p.GetString())
            .Should().Equal(Partition);
    }

    /// <summary>
    /// <c>truncated</c> is a measurement — "a further match exists" — not "the page was full". The
    /// subtree under the base path holds exactly two Markdown nodes: a limit of 2 returns both and
    /// is NOT truncated; a limit of 1 returns one and IS.
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task TruncatedMeansAFurtherMatchExists_NotThatThePageWasFull()
    {
        var full = JsonDocument.Parse(await Search("nodeType:Markdown", $"@{Partition}", limit: 2)).RootElement;
        full.GetProperty("count").GetInt32().Should().Be(2, "Deep and Leaf are the two Markdown nodes under the base path");
        full.GetProperty("truncated").GetBoolean().Should().BeFalse("the page is full AND complete — nothing lies beyond it");

        var clipped = JsonDocument.Parse(await Search("nodeType:Markdown", $"@{Partition}", limit: 1)).RootElement;
        clipped.GetProperty("count").GetInt32().Should().Be(1);
        clipped.GetProperty("truncated").GetBoolean().Should().BeTrue("a second match exists beyond the page");
    }

    /// <summary>
    /// The control that keeps the widening honest: a query that states its OWN scope keeps it, so
    /// the same base path with <c>scope:exact</c> (immediate children) does NOT reach the leaf.
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task ACallerStatedScopeIsKept()
    {
        var answer = await Search($"nodeType:Markdown name:{Marker} scope:exact", $"@{Partition}");

        Output.WriteLine(answer);
        JsonDocument.Parse(answer).RootElement.GetProperty("count").GetInt32()
            .Should().Be(0, "scope:exact under the base path is its immediate children, and the leaf is deeper");
    }
}
