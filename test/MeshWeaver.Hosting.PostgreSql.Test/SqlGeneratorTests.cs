using System.Linq;
using FluentAssertions;
using MeshWeaver.Mesh;
using Xunit;

namespace MeshWeaver.Hosting.PostgreSql.Test;

/// <summary>
/// Unit tests for PostgreSqlSqlGenerator. No database required.
/// </summary>
public class SqlGeneratorTests
{
    [Fact]
    public void MapSelector_KnownProperties()
    {
        PostgreSqlSqlGenerator.MapSelector("name").Should().Be("n.name");
        PostgreSqlSqlGenerator.MapSelector("nodeType").Should().Be("n.node_type");
        PostgreSqlSqlGenerator.MapSelector("description").Should().Be("n.description");
        PostgreSqlSqlGenerator.MapSelector("category").Should().Be("n.category");
        PostgreSqlSqlGenerator.MapSelector("version").Should().Be("n.version");
        PostgreSqlSqlGenerator.MapSelector("state").Should().Be("n.state");
        PostgreSqlSqlGenerator.MapSelector("path").Should().Be("n.path");
    }

    [Fact]
    public void MapSelector_ContentFields()
    {
        PostgreSqlSqlGenerator.MapSelector("content.status").Should().Be("n.content->>'status'");
        PostgreSqlSqlGenerator.MapSelector("content.address.city")
            .Should().Be("n.content->'address'->>'city'");
    }

    [Fact]
    public void MapSelector_UnknownFallsToJsonb()
    {
        PostgreSqlSqlGenerator.MapSelector("unknownField")
            .Should().Be("n.content->>'unknownField'");
    }

    [Fact]
    public void GenerateSelectQuery_SimpleFilter()
    {
        var gen = new PostgreSqlSqlGenerator();
        var query = new ParsedQuery(
            Filter: new QueryComparison(new QueryCondition("nodeType", QueryOperator.Equal, ["Story"])),
            TextSearch: null);

        var (sql, parameters) = gen.GenerateSelectQuery(query);

        sql.Should().Contain("WHERE");
        sql.Should().Contain("LOWER(n.node_type) = @p0");
        parameters["@p0"].Should().Be("story");
    }

    [Fact]
    public void GenerateSelectQuery_WithLimit()
    {
        var gen = new PostgreSqlSqlGenerator();
        var query = new ParsedQuery(null, null, Limit: 10);

        var (sql, _) = gen.GenerateSelectQuery(query);
        sql.Should().Contain("LIMIT 10");
    }

    [Fact]
    public void GenerateSelectQuery_WithOrderBy()
    {
        var gen = new PostgreSqlSqlGenerator();
        var query = new ParsedQuery(null, null,
            OrderBy: new OrderByClause("name", Descending: true));

        var (sql, _) = gen.GenerateSelectQuery(query);
        sql.Should().Contain("ORDER BY n.name DESC");
    }

    [Fact]
    public void GenerateSelectQuery_TextSearch()
    {
        var gen = new PostgreSqlSqlGenerator();
        var query = new ParsedQuery(null, "laptop");

        var (sql, parameters) = gen.GenerateSelectQuery(query);

        sql.Should().Contain("to_tsvector");
        sql.Should().Contain("plainto_tsquery");
        parameters.Values.Should().Contain("laptop");
    }

    [Fact]
    public void GenerateSelectQuery_LikeOperator()
    {
        var gen = new PostgreSqlSqlGenerator();
        var query = new ParsedQuery(
            Filter: new QueryComparison(new QueryCondition("name", QueryOperator.Like, ["*test*"])),
            TextSearch: null);

        var (sql, parameters) = gen.GenerateSelectQuery(query);

        sql.Should().Contain("ILIKE");
        parameters["@p0"].Should().Be("%test%");
    }

    [Fact]
    public void GenerateSelectQuery_InOperator()
    {
        var gen = new PostgreSqlSqlGenerator();
        var query = new ParsedQuery(
            Filter: new QueryComparison(new QueryCondition("nodeType", QueryOperator.In, ["Story", "Task"])),
            TextSearch: null);

        var (sql, parameters) = gen.GenerateSelectQuery(query);

        sql.Should().Contain("IN (");
        parameters.Should().ContainKey("@p0");
        parameters.Should().ContainKey("@p1");
    }

    [Fact]
    public void GenerateSelectQuery_AndConditions()
    {
        var gen = new PostgreSqlSqlGenerator();
        var query = new ParsedQuery(
            Filter: new QueryAnd(
                new QueryComparison(new QueryCondition("nodeType", QueryOperator.Equal, ["Story"])),
                new QueryComparison(new QueryCondition("name", QueryOperator.Equal, ["Test"]))
            ),
            TextSearch: null);

        var (sql, _) = gen.GenerateSelectQuery(query);

        sql.Should().Contain("AND");
    }

    [Fact]
    public void GenerateSelectQuery_OrConditions()
    {
        var gen = new PostgreSqlSqlGenerator();
        var query = new ParsedQuery(
            Filter: new QueryOr(
                new QueryComparison(new QueryCondition("nodeType", QueryOperator.Equal, ["Story"])),
                new QueryComparison(new QueryCondition("nodeType", QueryOperator.Equal, ["Task"]))
            ),
            TextSearch: null);

        var (sql, _) = gen.GenerateSelectQuery(query);

        sql.Should().Contain("OR");
    }

    [Fact]
    public void GenerateScopeClause_Exact()
    {
        var gen = new PostgreSqlSqlGenerator();
        var (clause, parameters) = gen.GenerateScopeClause("ACME/Project", QueryScope.Exact);

        clause.Should().Contain("n.path = @scopePath");
        parameters["@scopePath"].Should().Be("ACME/Project");
    }

    [Fact]
    public void GenerateScopeClause_Children()
    {
        var gen = new PostgreSqlSqlGenerator();
        var (clause, parameters) = gen.GenerateScopeClause("ACME/Project", QueryScope.Children);

        clause.Should().Contain("n.namespace = @scopeNs");
        parameters["@scopeNs"].Should().Be("ACME/Project");
    }

    [Fact]
    public void GenerateScopeClause_Descendants()
    {
        var gen = new PostgreSqlSqlGenerator();
        var (clause, parameters) = gen.GenerateScopeClause("ACME", QueryScope.Descendants);

        clause.Should().Contain("n.path LIKE @scopePrefix || '%'");
        parameters["@scopePrefix"].Should().Be("ACME/");
    }

    [Fact]
    public void GenerateScopeClause_Subtree()
    {
        var gen = new PostgreSqlSqlGenerator();
        var (clause, _) = gen.GenerateScopeClause("ACME", QueryScope.Subtree);

        clause.Should().Contain("n.path = @scopePath");
        clause.Should().Contain("n.path LIKE @scopePrefix || '%'");
        clause.Should().Contain("OR");
    }

    [Fact]
    public void GenerateScopeClause_Ancestors()
    {
        var gen = new PostgreSqlSqlGenerator();
        var (clause, parameters) = gen.GenerateScopeClause("ACME/Project/Story1", QueryScope.Ancestors);

        clause.Should().Contain("n.path IN");
        parameters.Should().ContainKey("@ancestor0"); // ACME
        parameters.Should().ContainKey("@ancestor1"); // ACME/Project
        parameters["@ancestor0"].Should().Be("ACME");
        parameters["@ancestor1"].Should().Be("ACME/Project");
    }

    [Fact]
    public void GenerateScopeClause_AncestorsAndSelf()
    {
        var gen = new PostgreSqlSqlGenerator();
        var (clause, parameters) = gen.GenerateScopeClause("ACME/Project/Story1", QueryScope.AncestorsAndSelf);

        clause.Should().Contain("n.path IN");
        parameters.Should().ContainKey("@ancestor0"); // ACME
        parameters.Should().ContainKey("@ancestor1"); // ACME/Project
        parameters.Should().ContainKey("@ancestor2"); // ACME/Project/Story1 (self)
    }

    [Fact]
    public void GenerateScopeClause_Hierarchy()
    {
        var gen = new PostgreSqlSqlGenerator();
        var (clause, parameters) = gen.GenerateScopeClause("ACME/Project", QueryScope.Hierarchy);

        clause.Should().Contain("n.path IN");
        clause.Should().Contain("n.path LIKE @scopePrefix || '%'");
        clause.Should().Contain("OR");
        parameters.Should().ContainKey("@ancestor0"); // ACME
        parameters.Should().ContainKey("@ancestor1"); // ACME/Project (self)
    }

    [Fact]
    public void GenerateSelectQuery_WithAccessControl()
    {
        var gen = new PostgreSqlSqlGenerator();
        var query = new ParsedQuery(null, null);

        var (sql, parameters) = gen.GenerateSelectQuery(query, userId: "alice");

        sql.Should().Contain("user_effective_permissions");
        sql.Should().Contain("'Read'");
        parameters.Values.Should().Contain("alice");
    }

    [Fact]
    public void GetAncestorPaths_ReturnsCorrectPaths()
    {
        PostgreSqlSqlGenerator.GetAncestorPaths("ACME/Project/Story1")
            .Should().BeEquivalentTo("ACME", "ACME/Project");

        PostgreSqlSqlGenerator.GetAncestorPaths("ACME")
            .Should().BeEmpty();

        PostgreSqlSqlGenerator.GetAncestorPaths("")
            .Should().BeEmpty();
    }
}
