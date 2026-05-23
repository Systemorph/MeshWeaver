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
    public void GenerateSelectQuery_NoSelect_FetchesContent()
    {
        var gen = new PostgreSqlSqlGenerator();
        var (sql, _) = gen.GenerateSelectQuery(ParsedQuery.Empty);

        sql.Should().Contain("n.content,").And.NotContain("NULL::jsonb AS content");
    }

    [Fact]
    public void GenerateSelectQuery_SelectExcludesContent_SkipsJsonbFetch()
    {
        // "Is this up-to-date?" callers ask for select:path,version — fetching the
        // JSONB content column is pure waste. The result-set shape stays the same
        // (NULL::jsonb keeps ordinal lookup intact) but the planner skips the heap
        // fetch + de-tuple of large blobs.
        var gen = new PostgreSqlSqlGenerator();
        var query = ParsedQuery.Empty with { Select = ["path", "version"] };

        var includeContent = query.Select is null
            || query.Select.Any(s => s.Equals("content", System.StringComparison.OrdinalIgnoreCase));
        var (sql, _) = gen.GenerateSelectQuery(query, includeContent: includeContent);

        sql.Should().Contain("NULL::jsonb AS content").And.NotContain("n.content,");
    }

    [Fact]
    public void GenerateSelectQuery_SelectIncludesContent_FetchesContent()
    {
        var gen = new PostgreSqlSqlGenerator();
        var query = ParsedQuery.Empty with { Select = ["path", "name", "content"] };

        var includeContent = query.Select is null
            || query.Select.Any(s => s.Equals("content", System.StringComparison.OrdinalIgnoreCase));
        var (sql, _) = gen.GenerateSelectQuery(query, includeContent: includeContent);

        sql.Should().Contain("n.content,").And.NotContain("NULL::jsonb AS content");
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

        sql.Should().Contain("ILIKE");
        sql.Should().Contain("COALESCE(n.name,'')");
        parameters.Values.Should().Contain("%laptop%");
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
    public void GenerateSelectQuery_NotEqualOnContentField_TolerantOfNull()
    {
        // 🚨 Regression: `-content.status:Done` (thread list filter) silently
        // dropped every existing thread in prod because the threads were
        // created before the Done state existed → content.status was null.
        // PostgreSQL three-valued logic: `LOWER(content->>'status') != 'done'`
        // evaluates to NULL when status is NULL, and NULL is not TRUE → the
        // row is filtered out. The fix wraps NotEqual in an `OR sel IS NULL`
        // clause so "field absent" counts as "not equal".
        var gen = new PostgreSqlSqlGenerator();
        var query = new ParsedQuery(
            Filter: new QueryComparison(new QueryCondition("content.status", QueryOperator.NotEqual, ["Done"])),
            TextSearch: null);

        var (sql, _) = gen.GenerateSelectQuery(query);

        sql.Should().Contain("IS NULL",
            "NotEqual must coalesce with IS NULL so rows where the selector is " +
            "NULL pass the negated filter (three-valued logic otherwise drops them)");
        sql.Should().Contain("!=");
    }

    [Fact]
    public void GenerateSelectQuery_NotInOperator_TolerantOfNull()
    {
        // Same null-handling rule for NotIn — `NULL NOT IN (...)` is NULL,
        // not TRUE, so without the IS NULL coalesce the row gets dropped.
        var gen = new PostgreSqlSqlGenerator();
        var query = new ParsedQuery(
            Filter: new QueryComparison(new QueryCondition("content.status", QueryOperator.NotIn, ["Done", "Cancelled"])),
            TextSearch: null);

        var (sql, _) = gen.GenerateSelectQuery(query);

        sql.Should().Contain("NOT IN (");
        sql.Should().Contain("IS NULL");
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
    public void GenerateScopeClause_Descendants_EmptyBasePath_ReturnsNoFilter()
    {
        // basePath="" means "descendants of root" = all nodes in schema.
        // Must NOT generate "n.path LIKE '/%'" which matches nothing.
        var gen = new PostgreSqlSqlGenerator();
        var (clause, parameters) = gen.GenerateScopeClause("", QueryScope.Descendants);

        clause.Should().BeEmpty("descendants of root should return all nodes with no path constraint");
        parameters.Should().BeEmpty();
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
    public void GenerateScopeClause_Subtree_EmptyBasePath_ReturnsNoFilter()
    {
        // basePath="" means "subtree of root" = all nodes in schema. Same fix as Descendants.
        var gen = new PostgreSqlSqlGenerator();
        var (clause, parameters) = gen.GenerateScopeClause("", QueryScope.Subtree);

        clause.Should().BeEmpty("subtree of root should return all nodes with no path constraint");
        parameters.Should().BeEmpty();
    }

    [Fact]
    public void GenerateCrossSchemaSelectQuery_NamespaceSubtree_PushesDownPathFilter()
    {
        // Repro from prod (memex.meshweaver.cloud/Systemorph/Events):
        // `namespace:Systemorph/EventCalendar/Source scope:subtree nodeType:Code`
        // routes to the satellite `code` table → cross-schema fan-out is engaged.
        // Before the fix, the cross-schema SQL generator dropped the path filter
        // for any scope other than Exact, so the UNION returned every Code row
        // across the partition (47 rows in prod, including SocialMedia/, Post/,
        // FutuRe/Pricing/, …). The path-prefix clause MUST be pushed down.
        var parser = new QueryParser();
        var parsed = parser.Parse(
            "namespace:Systemorph/EventCalendar/Source scope:subtree nodeType:Code");
        var gen = new PostgreSqlSqlGenerator();

        var (sql, parameters) = gen.GenerateCrossSchemaSelectQuery(
            parsed, new[] { "systemorph" }, userId: null, tableName: "code");

        // Subtree must produce both an equality AND a prefix LIKE — anything less
        // is either wrong (only equality misses descendants) or wrong (only prefix
        // misses self).
        sql.Should().Contain("n.path = @scopePath",
            "the cross-schema SQL must restrict to the subtree the caller asked for "
            + "(prior bug: any scope != Exact silently dropped the path filter, "
            + "so the UNION returned every Code row in the schema)");
        sql.Should().Contain("n.path LIKE @scopePrefix");
        parameters.Should().ContainKey("@scopePath")
            .WhoseValue.Should().Be("Systemorph/EventCalendar/Source");
        parameters.Should().ContainKey("@scopePrefix")
            .WhoseValue.Should().Be("Systemorph/EventCalendar/Source/");
    }

    [Fact]
    public void GenerateCrossSchemaSelectQuery_NamespaceChildrenDefault_PushesDownPathFilter()
    {
        // Same bug shape with the parser's DEFAULT scope for `namespace:` (Children).
        // `namespace:partition/doc nodeType:Comment` routes to annotations (satellite),
        // engages cross-schema, and before the fix returned every comment in the
        // partition (or every partition).
        var parser = new QueryParser();
        var parsed = parser.Parse("namespace:partition/doc nodeType:Comment");
        var gen = new PostgreSqlSqlGenerator();

        var (sql, parameters) = gen.GenerateCrossSchemaSelectQuery(
            parsed, new[] { "partition" }, userId: null, tableName: "annotations");

        // Default scope for namespace: is Children → namespace equality.
        sql.Should().Contain("n.namespace = @scopeNs",
            "cross-schema satellite UNION must carry the namespace filter through");
        parameters.Should().ContainKey("@scopeNs")
            .WhoseValue.Should().Be("partition/doc");
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
