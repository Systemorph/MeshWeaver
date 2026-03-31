using System;
using FluentAssertions;
using MeshWeaver.Hosting.Cosmos;
using MeshWeaver.Mesh;
using Xunit;

namespace MeshWeaver.Hosting.Cosmos.Test;

public class CosmosSqlGeneratorTests
{
    private readonly CosmosSqlGenerator _generator = new();

    #region Comparison Operator Tests

    [Fact]
    public void Equal_GeneratesCorrectSql()
    {
        var query = new ParsedQuery(
            new QueryComparison(new QueryCondition("name", QueryOperator.Equal, ["John"])),
            null);

        var (sql, parameters) = _generator.GenerateWhereClause(query);

        sql.Should().Be("WHERE c.name = @p0");
        parameters.Should().ContainKey("@p0");
        parameters["@p0"].Should().Be("John");
    }

    [Fact]
    public void NotEqual_GeneratesCorrectSql()
    {
        var query = new ParsedQuery(
            new QueryComparison(new QueryCondition("status", QueryOperator.NotEqual, ["inactive"])),
            null);

        var (sql, parameters) = _generator.GenerateWhereClause(query);

        sql.Should().Be("WHERE c.status != @p0");
        parameters["@p0"].Should().Be("inactive");
    }

    [Fact]
    public void GreaterThan_GeneratesCorrectSql()
    {
        var query = new ParsedQuery(
            new QueryComparison(new QueryCondition("price", QueryOperator.GreaterThan, ["100"])),
            null);

        var (sql, parameters) = _generator.GenerateWhereClause(query);

        sql.Should().Be("WHERE c.price > @p0");
        parameters["@p0"].Should().Be(100L);
    }

    [Fact]
    public void LessThan_GeneratesCorrectSql()
    {
        var query = new ParsedQuery(
            new QueryComparison(new QueryCondition("age", QueryOperator.LessThan, ["18"])),
            null);

        var (sql, parameters) = _generator.GenerateWhereClause(query);

        sql.Should().Be("WHERE c.age < @p0");
        parameters["@p0"].Should().Be(18L);
    }

    [Fact]
    public void GreaterOrEqual_GeneratesCorrectSql()
    {
        var query = new ParsedQuery(
            new QueryComparison(new QueryCondition("rating", QueryOperator.GreaterOrEqual, ["4.5"])),
            null);

        var (sql, parameters) = _generator.GenerateWhereClause(query);

        sql.Should().Be("WHERE c.rating >= @p0");
        parameters["@p0"].Should().Be(4.5);
    }

    [Fact]
    public void LessOrEqual_GeneratesCorrectSql()
    {
        var query = new ParsedQuery(
            new QueryComparison(new QueryCondition("quantity", QueryOperator.LessOrEqual, ["10"])),
            null);

        var (sql, parameters) = _generator.GenerateWhereClause(query);

        sql.Should().Be("WHERE c.quantity <= @p0");
        parameters["@p0"].Should().Be(10L);
    }

    [Fact]
    public void Like_UsesContains()
    {
        var query = new ParsedQuery(
            new QueryComparison(new QueryCondition("name", QueryOperator.Like, ["*laptop*"])),
            null);

        var (sql, parameters) = _generator.GenerateWhereClause(query);

        sql.Should().Be("WHERE CONTAINS(c.name, @p0, true)");
        parameters["@p0"].Should().Be("laptop");
    }

    [Fact]
    public void In_GeneratesInClause()
    {
        var query = new ParsedQuery(
            new QueryComparison(new QueryCondition("category", QueryOperator.In, ["Electronics", "Computers", "Gadgets"])),
            null);

        var (sql, parameters) = _generator.GenerateWhereClause(query);

        // Note: parameter indices start at @p1 because @p0 is pre-allocated but unused for IN clauses
        sql.Should().Be("WHERE c.category IN (@p1, @p2, @p3)");
        parameters["@p1"].Should().Be("Electronics");
        parameters["@p2"].Should().Be("Computers");
        parameters["@p3"].Should().Be("Gadgets");
    }

    [Fact]
    public void NotIn_GeneratesNotInClause()
    {
        var query = new ParsedQuery(
            new QueryComparison(new QueryCondition("status", QueryOperator.NotIn, ["deleted", "archived"])),
            null);

        var (sql, parameters) = _generator.GenerateWhereClause(query);

        // Note: parameter indices start at @p1 because @p0 is pre-allocated but unused for NOT IN clauses
        sql.Should().Be("WHERE c.status NOT IN (@p1, @p2)");
        parameters["@p1"].Should().Be("deleted");
        parameters["@p2"].Should().Be("archived");
    }

    #endregion

    #region Logical Operator Tests

    [Fact]
    public void And_GeneratesParenthesizedClause()
    {
        var query = new ParsedQuery(
            new QueryAnd(
                new QueryComparison(new QueryCondition("status", QueryOperator.Equal, ["active"])),
                new QueryComparison(new QueryCondition("price", QueryOperator.GreaterThan, ["100"]))
            ),
            null);

        var (sql, parameters) = _generator.GenerateWhereClause(query);

        sql.Should().Be("WHERE (c.status = @p0 AND c.price > @p1)");
        parameters["@p0"].Should().Be("active");
        parameters["@p1"].Should().Be(100L);
    }

    [Fact]
    public void Or_GeneratesParenthesizedClause()
    {
        var query = new ParsedQuery(
            new QueryOr(
                new QueryComparison(new QueryCondition("name", QueryOperator.Equal, ["Laptop"])),
                new QueryComparison(new QueryCondition("name", QueryOperator.Equal, ["Desktop"]))
            ),
            null);

        var (sql, parameters) = _generator.GenerateWhereClause(query);

        sql.Should().Be("WHERE (c.name = @p0 OR c.name = @p1)");
        parameters["@p0"].Should().Be("Laptop");
        parameters["@p1"].Should().Be("Desktop");
    }

    [Fact]
    public void NestedAndOr_GeneratesCorrectPrecedence()
    {
        // (a AND b) OR c
        var query = new ParsedQuery(
            new QueryOr(
                new QueryAnd(
                    new QueryComparison(new QueryCondition("a", QueryOperator.Equal, ["1"])),
                    new QueryComparison(new QueryCondition("b", QueryOperator.Equal, ["2"]))
                ),
                new QueryComparison(new QueryCondition("c", QueryOperator.Equal, ["3"]))
            ),
            null);

        var (sql, parameters) = _generator.GenerateWhereClause(query);

        sql.Should().Be("WHERE ((c.a = @p0 AND c.b = @p1) OR c.c = @p2)");
        parameters["@p0"].Should().Be(1L);
        parameters["@p1"].Should().Be(2L);
        parameters["@p2"].Should().Be(3L);
    }

    [Fact]
    public void SingleChildAnd_NoExtraParentheses()
    {
        var query = new ParsedQuery(
            new QueryAnd(
                new QueryComparison(new QueryCondition("status", QueryOperator.Equal, ["active"]))
            ),
            null);

        var (sql, _) = _generator.GenerateWhereClause(query);

        sql.Should().Be("WHERE c.status = @p0");
    }

    [Fact]
    public void SingleChildOr_NoExtraParentheses()
    {
        var query = new ParsedQuery(
            new QueryOr(
                new QueryComparison(new QueryCondition("status", QueryOperator.Equal, ["active"]))
            ),
            null);

        var (sql, _) = _generator.GenerateWhereClause(query);

        sql.Should().Be("WHERE c.status = @p0");
    }

    #endregion

    #region Text Search Tests

    [Fact]
    public void TextSearch_SearchesNameDescriptionNodeType()
    {
        var query = new ParsedQuery(null, "laptop");

        var (sql, parameters) = _generator.GenerateWhereClause(query);

        sql.Should().Contain("CONTAINS(LOWER(c.name");
        sql.Should().Contain("CONTAINS(LOWER(c.description");
        sql.Should().Contain("CONTAINS(LOWER(c.nodeType");
        sql.Should().Contain("CONTAINS(LOWER(c.path");
        sql.Should().Contain(" OR ");
        parameters["@p0"].Should().Be("laptop");
    }

    [Fact]
    public void TextSearch_CombinedWithFilter_GeneratesBoth()
    {
        var query = new ParsedQuery(
            new QueryComparison(new QueryCondition("status", QueryOperator.Equal, ["active"])),
            "laptop");

        var (sql, parameters) = _generator.GenerateWhereClause(query);

        sql.Should().Contain("c.status = @p0");
        sql.Should().Contain("CONTAINS(LOWER(c.name");
        sql.Should().Contain(" AND ");
        parameters["@p0"].Should().Be("active");
        parameters["@p1"].Should().Be("laptop");
    }

    #endregion

    #region Parameter Binding Tests

    [Fact]
    public void Parameters_NamedSequentially()
    {
        var query = new ParsedQuery(
            new QueryAnd(
                new QueryComparison(new QueryCondition("a", QueryOperator.Equal, ["1"])),
                new QueryComparison(new QueryCondition("b", QueryOperator.Equal, ["2"])),
                new QueryComparison(new QueryCondition("c", QueryOperator.Equal, ["3"]))
            ),
            null);

        var (_, parameters) = _generator.GenerateWhereClause(query);

        parameters.Should().ContainKey("@p0");
        parameters.Should().ContainKey("@p1");
        parameters.Should().ContainKey("@p2");
    }

    [Fact]
    public void ValueConversion_HandlesBool()
    {
        var query = new ParsedQuery(
            new QueryComparison(new QueryCondition("isActive", QueryOperator.Equal, ["true"])),
            null);

        var (_, parameters) = _generator.GenerateWhereClause(query);

        parameters["@p0"].Should().Be(true);
    }

    [Fact]
    public void ValueConversion_HandlesLong()
    {
        var query = new ParsedQuery(
            new QueryComparison(new QueryCondition("count", QueryOperator.Equal, ["12345"])),
            null);

        var (_, parameters) = _generator.GenerateWhereClause(query);

        parameters["@p0"].Should().Be(12345L);
    }

    [Fact]
    public void ValueConversion_HandlesDouble()
    {
        var query = new ParsedQuery(
            new QueryComparison(new QueryCondition("price", QueryOperator.Equal, ["99.99"])),
            null);

        var (_, parameters) = _generator.GenerateWhereClause(query);

        parameters["@p0"].Should().Be(99.99);
    }

    [Fact]
    public void ValueConversion_HandlesDate()
    {
        var query = new ParsedQuery(
            new QueryComparison(new QueryCondition("createdAt", QueryOperator.GreaterOrEqual, ["2024-01-15"])),
            null);

        var (_, parameters) = _generator.GenerateWhereClause(query);

        parameters["@p0"].Should().BeOfType<DateTimeOffset>();
    }

    [Fact]
    public void ValueConversion_PreservesStringWithSlash()
    {
        var query = new ParsedQuery(
            new QueryComparison(new QueryCondition("nodeType", QueryOperator.Equal, ["ACME/Project/Todo"])),
            null);

        var (_, parameters) = _generator.GenerateWhereClause(query);

        parameters["@p0"].Should().Be("ACME/Project/Todo");
    }

    #endregion

    #region GenerateSelectQuery Tests

    [Fact]
    public void GenerateSelectQuery_BasicFilter_GeneratesCorrectSql()
    {
        var query = new ParsedQuery(
            new QueryComparison(new QueryCondition("status", QueryOperator.Equal, ["active"])),
            null);

        var (sql, _) = _generator.GenerateSelectQuery(query);

        sql.Should().Be("SELECT * FROM c WHERE c.status = @p0");
    }

    [Fact]
    public void GenerateSelectQuery_NoFilter_GeneratesSelectAll()
    {
        var query = ParsedQuery.Empty;

        var (sql, _) = _generator.GenerateSelectQuery(query);

        sql.Should().Be("SELECT * FROM c");
    }

    [Fact]
    public void GenerateSelectQuery_WithOrderByAscending_IncludesOrderBy()
    {
        var query = new ParsedQuery(
            new QueryComparison(new QueryCondition("status", QueryOperator.Equal, ["active"])),
            null,
            OrderBy: new OrderByClause("name", false));

        var (sql, _) = _generator.GenerateSelectQuery(query);

        sql.Should().Contain("ORDER BY c.name ASC");
    }

    [Fact]
    public void GenerateSelectQuery_WithOrderByDescending_IncludesOrderByDesc()
    {
        var query = new ParsedQuery(
            null,
            null,
            OrderBy: new OrderByClause("lastAccessedAt", true));

        var (sql, _) = _generator.GenerateSelectQuery(query);

        sql.Should().Contain("ORDER BY c.lastAccessedAt DESC");
    }

    [Fact]
    public void GenerateSelectQuery_WithLimit_IncludesTop()
    {
        var query = new ParsedQuery(
            null,
            null,
            Limit: 10);

        var (sql, _) = _generator.GenerateSelectQuery(query);

        sql.Should().Contain("SELECT TOP 10");
    }

    [Fact]
    public void GenerateSelectQuery_WithLimitAndOrderBy_CorrectOrder()
    {
        var query = new ParsedQuery(
            new QueryComparison(new QueryCondition("status", QueryOperator.Equal, ["active"])),
            null,
            OrderBy: new OrderByClause("price", true),
            Limit: 20);

        var (sql, _) = _generator.GenerateSelectQuery(query);

        sql.Should().StartWith("SELECT TOP 20 *");
        sql.Should().Contain("WHERE c.status = @p0");
        sql.Should().EndWith("ORDER BY c.price DESC");
    }

    [Fact]
    public void GenerateSelectQuery_ComplexQuery_AllElementsPresent()
    {
        var query = new ParsedQuery(
            new QueryAnd(
                new QueryComparison(new QueryCondition("status", QueryOperator.Equal, ["active"])),
                new QueryComparison(new QueryCondition("price", QueryOperator.GreaterThan, ["100"]))
            ),
            "laptop",
            OrderBy: new OrderByClause("createdAt", true),
            Limit: 50);

        var (sql, parameters) = _generator.GenerateSelectQuery(query);

        sql.Should().StartWith("SELECT TOP 50 *");
        sql.Should().Contain("c.status = @p0");
        sql.Should().Contain("c.price > @p1");
        sql.Should().Contain("CONTAINS(LOWER(c.name");
        sql.Should().EndWith("ORDER BY c.createdAt DESC");
        parameters.Count.Should().BeGreaterThanOrEqualTo(3);
    }

    [Fact]
    public void GenerateSelectQuery_CustomAlias_UsesAlias()
    {
        var query = new ParsedQuery(
            new QueryComparison(new QueryCondition("name", QueryOperator.Equal, ["test"])),
            null);

        var (sql, _) = _generator.GenerateSelectQuery(query, "node");

        sql.Should().Contain("FROM node");
        sql.Should().Contain("node.name = @p0");
    }

    #endregion

    #region Edge Cases

    [Fact]
    public void EmptyQuery_ReturnsEmptyWhereClause()
    {
        var query = ParsedQuery.Empty;

        var (sql, parameters) = _generator.GenerateWhereClause(query);

        sql.Should().BeEmpty();
        parameters.Should().BeEmpty();
    }

    [Fact]
    public void NullFilter_WithNullTextSearch_ReturnsEmptyClause()
    {
        var query = new ParsedQuery(null, null);

        var (sql, parameters) = _generator.GenerateWhereClause(query);

        sql.Should().BeEmpty();
        parameters.Should().BeEmpty();
    }

    [Fact]
    public void EmptyTextSearch_NotIncluded()
    {
        var query = new ParsedQuery(
            new QueryComparison(new QueryCondition("status", QueryOperator.Equal, ["active"])),
            "");

        var (sql, _) = _generator.GenerateWhereClause(query);

        sql.Should().Be("WHERE c.status = @p0");
        sql.Should().NotContain("CONTAINS");
    }

    [Fact]
    public void MultipleQueriesOnSameGenerator_ParametersReset()
    {
        var query1 = new ParsedQuery(
            new QueryComparison(new QueryCondition("a", QueryOperator.Equal, ["1"])),
            null);
        var query2 = new ParsedQuery(
            new QueryComparison(new QueryCondition("b", QueryOperator.Equal, ["2"])),
            null);

        _generator.GenerateWhereClause(query1);
        var (_, params2) = _generator.GenerateWhereClause(query2);

        // Second query should start from @p0, not continue from @p1
        params2.Should().ContainKey("@p0");
        params2.Should().NotContainKey("@p1");
    }

    #endregion

    #region Scope Clause Tests

    [Fact]
    public void GenerateScopeClause_Exact_GeneratesEquality()
    {
        var (clause, parameters) = _generator.GenerateScopeClause("products/electronics", QueryScope.Exact);

        clause.Should().Contain("c.path = @scopePath");
        parameters["@scopePath"].Should().Be("products/electronics");
    }

    [Fact]
    public void GenerateScopeClause_Descendants_GeneratesStartsWith()
    {
        var (clause, parameters) = _generator.GenerateScopeClause("products", QueryScope.Descendants);

        clause.Should().Contain("STARTSWITH(c.path, @scopePrefix)");
        parameters["@scopePrefix"].Should().Be("products/");
    }

    [Fact]
    public void GenerateScopeClause_Subtree_GeneratesOrClause()
    {
        var (clause, parameters) = _generator.GenerateScopeClause("products", QueryScope.Subtree);

        clause.Should().Contain("c.path = @scopePath");
        clause.Should().Contain("STARTSWITH(c.path, @scopePrefix)");
        clause.Should().Contain(" OR ");
        parameters["@scopePath"].Should().Be("products");
        parameters["@scopePrefix"].Should().Be("products/");
    }

    [Fact]
    public void GenerateScopeClause_Children_GeneratesRegexMatch()
    {
        var (clause, parameters) = _generator.GenerateScopeClause("products", QueryScope.Children);

        clause.Should().Contain("STARTSWITH(c.path, @scopePrefix)");
        clause.Should().Contain("RegexMatch(c.path, @childPattern)");
        parameters["@childPattern"].Should().Be("^products/[^/]+$");
    }

    [Fact]
    public void GenerateScopeClause_Ancestors_GeneratesInClause()
    {
        var (clause, parameters) = _generator.GenerateScopeClause("a/b/c", QueryScope.Ancestors);

        clause.Should().Contain("c.path IN (");
        parameters.Should().ContainKey("@ancestor0");
        parameters.Should().ContainKey("@ancestor1");
        parameters["@ancestor0"].Should().Be("a");
        parameters["@ancestor1"].Should().Be("a/b");
    }

    [Fact]
    public void GenerateScopeClause_AncestorsAndSelf_IncludesSelf()
    {
        var (clause, parameters) = _generator.GenerateScopeClause("a/b/c", QueryScope.AncestorsAndSelf);

        clause.Should().Contain("c.path IN (");
        parameters.Should().ContainKey("@ancestor0");
        parameters.Should().ContainKey("@ancestor1");
        parameters.Should().ContainKey("@ancestor2");
        parameters["@ancestor2"].Should().Be("a/b/c");
    }

    [Fact]
    public void GenerateScopeClause_Hierarchy_GeneratesFullHierarchy()
    {
        var (clause, parameters) = _generator.GenerateScopeClause("a/b", QueryScope.Hierarchy);

        // Should include ancestors, self, and descendants
        clause.Should().Contain("c.path IN (");
        clause.Should().Contain("STARTSWITH(c.path, @scopePrefix)");
        clause.Should().Contain(" OR ");
    }

    [Fact]
    public void GenerateScopeClause_EmptyPath_ReturnsEmpty()
    {
        var (clause, parameters) = _generator.GenerateScopeClause("", QueryScope.Descendants);

        clause.Should().BeEmpty();
        parameters.Should().BeEmpty();
    }

    [Fact]
    public void GenerateScopeClause_NullPath_ReturnsEmpty()
    {
        var (clause, parameters) = _generator.GenerateScopeClause(null, QueryScope.Descendants);

        clause.Should().BeEmpty();
        parameters.Should().BeEmpty();
    }

    #endregion

    #region Vector Search Tests

    [Fact]
    public void GenerateVectorSearchQuery_BasicSearch_GeneratesCorrectSql()
    {
        var queryVector = new float[] { 0.1f, 0.2f, 0.3f };

        var (sql, parameters) = _generator.GenerateVectorSearchQuery(null, queryVector, topK: 10);

        sql.Should().Contain("SELECT TOP 10 *");
        sql.Should().Contain("ORDER BY VectorDistance(c.embedding, @queryVector)");
        parameters["@queryVector"].Should().BeEquivalentTo(queryVector);
    }

    [Fact]
    public void GenerateVectorSearchQuery_WithFilter_IncludesWhereClause()
    {
        var queryVector = new float[] { 0.1f, 0.2f, 0.3f };
        var filter = new ParsedQuery(
            new QueryComparison(new QueryCondition("status", QueryOperator.Equal, ["active"])),
            null);

        var (sql, parameters) = _generator.GenerateVectorSearchQuery(filter, queryVector, topK: 5);

        sql.Should().Contain("SELECT TOP 5 *");
        sql.Should().Contain("WHERE c.status = @p0");
        sql.Should().Contain("ORDER BY VectorDistance(c.embedding, @queryVector)");
        parameters["@p0"].Should().Be("active");
        parameters["@queryVector"].Should().BeEquivalentTo(queryVector);
    }

    [Fact]
    public void GenerateVectorSearchQuery_CustomEmbeddingField_UsesCustomField()
    {
        var queryVector = new float[] { 0.1f, 0.2f, 0.3f };

        var (sql, _) = _generator.GenerateVectorSearchQuery(null, queryVector, topK: 10, embeddingField: "contentVector");

        sql.Should().Contain("VectorDistance(c.contentVector, @queryVector)");
    }

    [Fact]
    public void GenerateVectorSearchQuery_CustomAlias_UsesAlias()
    {
        var queryVector = new float[] { 0.1f, 0.2f, 0.3f };

        var (sql, _) = _generator.GenerateVectorSearchQuery(null, queryVector, topK: 10, alias: "node");

        sql.Should().Contain("FROM node");
        sql.Should().Contain("VectorDistance(node.embedding, @queryVector)");
    }

    #endregion

    #region HierarchyPatterns Tests

    [Fact]
    public void HierarchyPatterns_DirectChildren_GeneratesCorrectPattern()
    {
        var pattern = HierarchyPatterns.DirectChildren("a/b");

        pattern.Should().Be("^a/b/[^/]+$");
    }

    [Fact]
    public void HierarchyPatterns_DirectChildren_RootLevel_GeneratesCorrectPattern()
    {
        var pattern = HierarchyPatterns.DirectChildren("");

        pattern.Should().Be("^[^/]+$");
    }

    [Fact]
    public void HierarchyPatterns_DirectChildren_NullPath_GeneratesRootPattern()
    {
        var pattern = HierarchyPatterns.DirectChildren(null);

        pattern.Should().Be("^[^/]+$");
    }

    [Fact]
    public void HierarchyPatterns_ExactDepth_OneLevel_GeneratesCorrectPattern()
    {
        var pattern = HierarchyPatterns.ExactDepth("a/b", 1);

        pattern.Should().Be("^a/b/[^/]+$");
    }

    [Fact]
    public void HierarchyPatterns_ExactDepth_TwoLevels_GeneratesCorrectPattern()
    {
        var pattern = HierarchyPatterns.ExactDepth("a/b", 2);

        pattern.Should().Be("^a/b/[^/]+/[^/]+$");
    }

    [Fact]
    public void HierarchyPatterns_ExactDepth_FromRoot_GeneratesCorrectPattern()
    {
        var pattern = HierarchyPatterns.ExactDepth("", 3);

        pattern.Should().Be("^/[^/]+/[^/]+/[^/]+$");
    }

    [Fact]
    public void HierarchyPatterns_ContainsSegment_GeneratesCorrectPattern()
    {
        var pattern = HierarchyPatterns.ContainsSegment("electronics");

        pattern.Should().Be("/electronics/");
    }

    [Fact]
    public void HierarchyPatterns_WildcardInPath_SingleWildcard_GeneratesCorrectPattern()
    {
        var pattern = HierarchyPatterns.WildcardInPath("a/*/c");

        pattern.Should().Be("^a/[^/]+/c$");
    }

    [Fact]
    public void HierarchyPatterns_WildcardInPath_MultipleWildcards_GeneratesCorrectPattern()
    {
        var pattern = HierarchyPatterns.WildcardInPath("*/b/*");

        pattern.Should().Be("^[^/]+/b/[^/]+$");
    }

    [Fact]
    public void HierarchyPatterns_GetAncestorPaths_ReturnsAllAncestors()
    {
        var ancestors = HierarchyPatterns.GetAncestorPaths("a/b/c/d");

        ancestors.Should().BeEquivalentTo(["a", "a/b", "a/b/c"]);
    }

    [Fact]
    public void HierarchyPatterns_GetAncestorPaths_SingleSegment_ReturnsEmpty()
    {
        var ancestors = HierarchyPatterns.GetAncestorPaths("a");

        ancestors.Should().BeEmpty();
    }

    [Fact]
    public void HierarchyPatterns_GetAncestorPaths_TwoSegments_ReturnsSingleAncestor()
    {
        var ancestors = HierarchyPatterns.GetAncestorPaths("a/b");

        ancestors.Should().BeEquivalentTo(["a"]);
    }

    [Fact]
    public void HierarchyPatterns_EscapesSpecialCharacters()
    {
        var pattern = HierarchyPatterns.DirectChildren("a.b+c");

        // Regex special chars should be escaped
        pattern.Should().Be("^a\\.b\\+c/[^/]+$");
    }

    #endregion
}
