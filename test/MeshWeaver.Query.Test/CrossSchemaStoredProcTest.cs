using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using FluentAssertions;
using MeshWeaver.Mesh;
using Xunit;

namespace MeshWeaver.Query.Test;

/// <summary>
/// Tests for the cross-schema query pipeline.
/// Verifies parameter inlining, routing hints, and query parsing for cross-schema scenarios.
/// PostgreSQL-specific SQL generation is tested via Docker DB integration tests.
/// </summary>
public class CrossSchemaStoredProcTest
{
    private readonly QueryParser _parser = new();

    // ── WHERE clause parameter inlining ──────────────────────────────
    // The stored proc receives WHERE clause as TEXT, so parameters must be inlined.

    [Fact]
    public void InlineParameters_StringValue_EscapesSingleQuotes()
    {
        var parameters = new Dictionary<string, object> { ["@p0"] = "it's a test" };
        var clause = "LOWER(n.name) = @p0";

        var inlined = InlineParameters(clause, parameters);

        inlined.Should().Be("LOWER(n.name) = 'it''s a test'");
    }

    [Fact]
    public void InlineParameters_MultipleParams_ReplacesAll()
    {
        var parameters = new Dictionary<string, object>
        {
            ["@p0"] = "markdown",
            ["@p1"] = "partner"
        };
        var clause = "LOWER(n.node_type) = @p0 AND LOWER(n.name) = @p1";

        var inlined = InlineParameters(clause, parameters);

        inlined.Should().Be("LOWER(n.node_type) = 'markdown' AND LOWER(n.name) = 'partner'");
    }

    [Fact]
    public void InlineParameters_IntValue_NoQuotes()
    {
        var parameters = new Dictionary<string, object> { ["@p0"] = 42L };
        var clause = "n.version = @p0";

        var inlined = InlineParameters(clause, parameters);

        inlined.Should().Be("n.version = 42");
    }

    [Fact]
    public void InlineParameters_BoolValue_LowerCase()
    {
        var parameters = new Dictionary<string, object> { ["@p0"] = true };
        var clause = "n.state = @p0";

        var inlined = InlineParameters(clause, parameters);

        inlined.Should().Be("n.state = true");
    }

    [Fact]
    public void InlineParameters_EmptyParams_NoChange()
    {
        var clause = "n.main_node = n.path";
        var inlined = InlineParameters(clause, new Dictionary<string, object>());

        inlined.Should().Be("n.main_node = n.path");
    }

    [Fact]
    public void InlineParameters_LongerParamReplacedFirst()
    {
        // @p10 should be replaced before @p1 to avoid partial matches
        var parameters = new Dictionary<string, object>
        {
            ["@p1"] = "a",
            ["@p10"] = "b"
        };
        var clause = "@p10 AND @p1";

        var inlined = InlineParameters(clause, parameters);

        inlined.Should().Be("'b' AND 'a'");
    }

    [Fact]
    public void InlineParameters_SqlInjection_Escaped()
    {
        var parameters = new Dictionary<string, object> { ["@p0"] = "'; DROP TABLE mesh_nodes; --" };
        var clause = "n.name = @p0";

        var inlined = InlineParameters(clause, parameters);

        // The single quote is escaped to '' — PostgreSQL treats the whole thing as a string literal
        inlined.Should().Be("n.name = '''; DROP TABLE mesh_nodes; --'");
        // The injection attempt is safely inside a string literal (starts with '')
        inlined.Should().StartWith("n.name = '''");
    }

    // ── ParsedQuery extraction ───────────────────────────────────────

    [Fact]
    public void ParsedQuery_TextSearch_ExtractedCorrectly()
    {
        var parsed = _parser.Parse("partner");
        parsed.TextSearch.Should().Be("partner");
        parsed.Filter.Should().BeNull();
    }

    [Fact]
    public void ParsedQuery_NodeTypeFilter_Extracted()
    {
        var parsed = _parser.Parse("nodeType:Thread");
        parsed.ExtractNodeType().Should().Be("Thread");
    }

    [Fact]
    public void ParsedQuery_NamespaceWithScope_Extracted()
    {
        var parsed = _parser.Parse("namespace:PartnerRe scope:descendants");
        parsed.Path.Should().Be("PartnerRe");
        parsed.Scope.Should().Be(QueryScope.Descendants);
    }

    [Fact]
    public void ParsedQuery_CombinedFilters_Parsed()
    {
        var parsed = _parser.Parse("nodeType:Markdown partner scope:descendants limit:20");
        parsed.ExtractNodeType().Should().Be("Markdown");
        parsed.TextSearch.Should().Be("partner");
        parsed.Scope.Should().Be(QueryScope.Descendants);
        parsed.Limit.Should().Be(20);
    }

    [Fact]
    public void ParsedQuery_SortOrder_Parsed()
    {
        var parsed = _parser.Parse("sort:lastModified-desc");
        parsed.OrderBy.Should().NotBeNull();
        parsed.OrderBy!.Property.Should().Be("lastModified");
        parsed.OrderBy.Descending.Should().BeTrue();
    }

    [Fact]
    public void ParsedQuery_IsMain_Parsed()
    {
        var parsed = _parser.Parse("is:main");
        parsed.IsMain.Should().BeTrue();
    }

    [Fact]
    public void ParsedQuery_SourceActivity_Parsed()
    {
        var parsed = _parser.Parse("source:activity");
        parsed.Source.Should().Be(QuerySource.Activity);
    }

    // ── Routing hints — when cross-schema fires vs single partition ───

    [Fact]
    public void RoutingHints_NoPath_NullPartition_CrossSchemaFires()
    {
        var config = new MeshConfiguration(new Dictionary<string, MeshNode>());
        var parsed = _parser.Parse("partner");
        var hints = config.ResolveRoutingHints(parsed);

        hints.Partition.Should().BeNull("global text search has no path → cross-schema");
    }

    [Fact]
    public void RoutingHints_WithNamespace_ResolvesPartition_NoCrossSchema()
    {
        var rules = new List<QueryRoutingRule>
        {
            query =>
            {
                var path = query.Path;
                if (string.IsNullOrEmpty(path)) return null;
                var slash = path.IndexOf('/');
                return new QueryRoutingHints { Partition = slash > 0 ? path[..slash] : path };
            }
        };
        var config = new MeshConfiguration(
            new Dictionary<string, MeshNode>(), queryRoutingRules: rules);

        var parsed = _parser.Parse("namespace:PartnerRe nodeType:Thread");
        var hints = config.ResolveRoutingHints(parsed);

        hints.Partition.Should().Be("PartnerRe", "namespace resolves partition → single partition query");
    }

    [Fact]
    public void RoutingHints_NodeTypeUser_ResolvesPartition_NoCrossSchema()
    {
        var rules = new List<QueryRoutingRule>
        {
            query => query.ExtractNodeType() == "User"
                ? new QueryRoutingHints { Partition = "User" } : null
        };
        var config = new MeshConfiguration(
            new Dictionary<string, MeshNode>(), queryRoutingRules: rules);

        var parsed = _parser.Parse("nodeType:User");
        var hints = config.ResolveRoutingHints(parsed);

        hints.Partition.Should().Be("User", "nodeType:User routing rule → single partition");
    }

    [Fact]
    public void RoutingHints_SourceActivity_NoPartition_CrossSchemaFires()
    {
        var config = new MeshConfiguration(new Dictionary<string, MeshNode>());
        var parsed = _parser.Parse("source:activity");
        var hints = config.ResolveRoutingHints(parsed);

        hints.Partition.Should().BeNull("activity feed has no path → cross-schema");
    }

    [Fact]
    public void RoutingHints_NodeTypeThread_ResolvesTable()
    {
        var rules = new List<QueryRoutingRule>
        {
            query => query.ExtractNodeType() switch
            {
                "Thread" => new QueryRoutingHints { Table = "threads" },
                _ => null
            }
        };
        var config = new MeshConfiguration(
            new Dictionary<string, MeshNode>(), queryRoutingRules: rules);

        var parsed = _parser.Parse("nodeType:Thread");
        var hints = config.ResolveRoutingHints(parsed);

        hints.Table.Should().Be("threads");
    }

    // ── Helper: mirrors the inlining logic from PostgreSqlCrossSchemaQueryProvider ──

    private static string InlineParameters(string clause, Dictionary<string, object> parameters)
    {
        foreach (var kvp in parameters.OrderByDescending(p => p.Key.Length))
        {
            var name = kvp.Key;
            var value = kvp.Value;
            var sqlValue = value switch
            {
                string s => $"'{s.Replace("'", "''")}'",
                bool b => b ? "true" : "false",
                int i => i.ToString(),
                long l => l.ToString(),
                double d => d.ToString(CultureInfo.InvariantCulture),
                _ => $"'{value?.ToString()?.Replace("'", "''")}'"
            };
            clause = clause.Replace(name, sqlValue);
        }
        return clause;
    }
}
