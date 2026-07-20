using MeshWeaver.Mesh;
using Xunit;

namespace MeshWeaver.Query.Test;

public class QueryParserTests
{
    private readonly QueryParser _parser = new();

    #region Basic Comparison Operators

    [Fact]
    public void Parse_EqualOperator_ReturnsCorrectCondition()
    {
        var result = _parser.Parse("name:John");

        result.Filter.Should().BeOfType<QueryComparison>();
        var comparison = (QueryComparison)result.Filter!;
        comparison.Condition.Selector.Should().Be("name");
        comparison.Condition.Operator.Should().Be(QueryOperator.Equal);
        comparison.Condition.Value.Should().Be("John");
    }

    [Fact]
    public void Parse_NotEqualOperator_ReturnsCorrectCondition()
    {
        var result = _parser.Parse("-status:inactive");

        result.Filter.Should().BeOfType<QueryComparison>();
        var comparison = (QueryComparison)result.Filter!;
        comparison.Condition.Selector.Should().Be("status");
        comparison.Condition.Operator.Should().Be(QueryOperator.NotEqual);
        comparison.Condition.Value.Should().Be("inactive");
    }

    [Fact]
    public void Parse_GreaterThanOperator_ReturnsCorrectCondition()
    {
        var result = _parser.Parse("price:>100");

        result.Filter.Should().BeOfType<QueryComparison>();
        var comparison = (QueryComparison)result.Filter!;
        comparison.Condition.Selector.Should().Be("price");
        comparison.Condition.Operator.Should().Be(QueryOperator.GreaterThan);
        comparison.Condition.Value.Should().Be("100");
    }

    [Fact]
    public void Parse_LessThanOperator_ReturnsCorrectCondition()
    {
        var result = _parser.Parse("age:<18");

        result.Filter.Should().BeOfType<QueryComparison>();
        var comparison = (QueryComparison)result.Filter!;
        comparison.Condition.Selector.Should().Be("age");
        comparison.Condition.Operator.Should().Be(QueryOperator.LessThan);
        comparison.Condition.Value.Should().Be("18");
    }

    [Fact]
    public void Parse_GreaterOrEqualOperator_ReturnsCorrectCondition()
    {
        var result = _parser.Parse("rating:>=4.5");

        result.Filter.Should().BeOfType<QueryComparison>();
        var comparison = (QueryComparison)result.Filter!;
        comparison.Condition.Selector.Should().Be("rating");
        comparison.Condition.Operator.Should().Be(QueryOperator.GreaterOrEqual);
        comparison.Condition.Value.Should().Be("4.5");
    }

    [Fact]
    public void Parse_LessOrEqualOperator_ReturnsCorrectCondition()
    {
        var result = _parser.Parse("quantity:<=10");

        result.Filter.Should().BeOfType<QueryComparison>();
        var comparison = (QueryComparison)result.Filter!;
        comparison.Condition.Selector.Should().Be("quantity");
        comparison.Condition.Operator.Should().Be(QueryOperator.LessOrEqual);
        comparison.Condition.Value.Should().Be("10");
    }

    [Fact]
    public void Parse_LikeOperator_ReturnsCorrectCondition()
    {
        var result = _parser.Parse("name:*laptop*");

        result.Filter.Should().BeOfType<QueryComparison>();
        var comparison = (QueryComparison)result.Filter!;
        comparison.Condition.Selector.Should().Be("name");
        comparison.Condition.Operator.Should().Be(QueryOperator.Like);
        comparison.Condition.Value.Should().Be("*laptop*");
    }

    #endregion

    #region In/Out Operators with Lists

    [Fact]
    public void Parse_InOperator_ParsesList()
    {
        var result = _parser.Parse("category:(Electronics OR Computers OR Gadgets)");

        result.Filter.Should().BeOfType<QueryComparison>();
        var comparison = (QueryComparison)result.Filter!;
        comparison.Condition.Selector.Should().Be("category");
        comparison.Condition.Operator.Should().Be(QueryOperator.In);
        comparison.Condition.Values.Should().BeEquivalentTo(new[] { "Electronics", "Computers", "Gadgets" }, System.Text.Json.JsonSerializerOptions.Default);
    }

    // ─── Grep-style `|` alternation (`grep -E`'s alternation operator) ───
    // Equivalent to `(A OR B OR C)` but more concise. Pushed down by backends as IN(...).

    [Fact]
    public void Parse_PipeAlternation_ProducesInOperator()
    {
        var result = _parser.Parse("nodeType:Story|Task|Bug");

        result.Filter.Should().BeOfType<QueryComparison>();
        var comparison = (QueryComparison)result.Filter!;
        comparison.Condition.Selector.Should().Be("nodeType");
        comparison.Condition.Operator.Should().Be(QueryOperator.In);
        comparison.Condition.Values.Should().BeEquivalentTo(new[] { "Story", "Task", "Bug" }, System.Text.Json.JsonSerializerOptions.Default);
    }

    [Fact]
    public void Parse_PipeAlternation_NegatedProducesNotInOperator()
    {
        var result = _parser.Parse("-nodeType:Spam|Trash");

        result.Filter.Should().BeOfType<QueryComparison>();
        var comparison = (QueryComparison)result.Filter!;
        comparison.Condition.Selector.Should().Be("nodeType");
        comparison.Condition.Operator.Should().Be(QueryOperator.NotIn);
        comparison.Condition.Values.Should().BeEquivalentTo(new[] { "Spam", "Trash" }, System.Text.Json.JsonSerializerOptions.Default);
    }

    [Fact]
    public void Parse_PipeAlternation_SingleValueAfterSplitStaysEqual()
    {
        // "foo|" with trailing pipe but only one non-empty value should remain Equal
        // (RemoveEmptyEntries collapses to one part → no In conversion).
        var result = _parser.Parse("nodeType:foo|");

        result.Filter.Should().BeOfType<QueryComparison>();
        var comparison = (QueryComparison)result.Filter!;
        comparison.Condition.Operator.Should().Be(QueryOperator.Equal);
        comparison.Condition.Value.Should().Be("foo|");
    }

    [Fact]
    public void Parse_PipeAlternation_DoesNotApplyToWildcard()
    {
        // `*` already implies Like — the | inside a Like pattern stays literal,
        // it does NOT split into alternation. Avoids accidental over-conversion.
        var result = _parser.Parse("name:*foo|bar*");

        result.Filter.Should().BeOfType<QueryComparison>();
        var comparison = (QueryComparison)result.Filter!;
        comparison.Condition.Operator.Should().Be(QueryOperator.Like);
        comparison.Condition.Value.Should().Be("*foo|bar*");
    }

    [Fact]
    public void Parse_PipeAlternation_OnPathQualifier_PopulatesPathsList()
    {
        // path:a|b|c is the routing-layer use case — produces multi-value Paths
        // for backends to push down as `WHERE path IN (...)`. The single Path
        // field stays populated with the first value for back-compat with
        // consumers that don't yet read Paths.
        var result = _parser.Parse("path:foo/bar/baz|foo/bar|foo");

        result.Paths.Should().BeEquivalentTo(new[] { "foo/bar/baz", "foo/bar", "foo" }, System.Text.Json.JsonSerializerOptions.Default);
        result.Path.Should().Be("foo/bar/baz");
    }

    [Fact]
    public void Parse_SinglePath_LeavesPathsNull()
    {
        var result = _parser.Parse("path:foo/bar");

        result.Path.Should().Be("foo/bar");
        result.Paths.Should().BeNull();
    }

    [Fact]
    public void Parse_BracketList_PopulatesOrderedPaths()
    {
        // The [a, b, c] surface is an EXPLICIT, ORDERED list of node paths — the
        // "alternatively specify the exact slides" form for a Deck's manifest. It
        // populates the SAME Paths list as path:a|b|c (so backends push it down as
        // WHERE path IN (...)), but the ORDER is preserved in Paths for order-sensitive
        // consumers (a deck presents its slides in the listed order).
        var result = _parser.Parse("[foo/bar/baz, foo/bar, foo]");

        result.Paths.Should().Equal("foo/bar/baz", "foo/bar", "foo");
        result.Path.Should().Be("foo/bar/baz");
    }

    [Fact]
    public void Parse_BracketList_SingleEntry()
    {
        var result = _parser.Parse("[only/one]");

        result.Paths.Should().Equal("only/one");
        result.Path.Should().Be("only/one");
    }

    // ─── SQL-function selectors in sort: ───
    // `sort:length(path)-desc` — function-call syntax in the sort selector lets
    // backends emit `ORDER BY length(n.path) DESC` without hardcoding sort
    // aliases. Routing-layer "longest-matching-prefix" lookups rely on this.

    [Fact]
    public void Parse_SortLengthFunction_ParsesAsSortSelector()
    {
        var result = _parser.Parse("sort:length(path)-desc");

        result.OrderBy.Should().NotBeNull();
        result.OrderBy!.Property.Should().Be("length(path)");
        result.OrderBy.Descending.Should().BeTrue();
    }

    [Fact]
    public void Parse_SortLowerFunction_AscendingDefault()
    {
        var result = _parser.Parse("sort:lower(name)");

        result.OrderBy.Should().NotBeNull();
        result.OrderBy!.Property.Should().Be("lower(name)");
        result.OrderBy.Descending.Should().BeFalse();
    }

    [Fact]
    public void Parse_PathQualifier_AcceptsFunctionCallInSort_AlongsidePathFilter()
    {
        // Routing-layer canonical form: path filter with alternation + sort + limit.
        var result = _parser.Parse("path:a/b/c|a/b|a sort:length(path)-desc limit:1");

        result.Paths.Should().BeEquivalentTo(new[] { "a/b/c", "a/b", "a" }, System.Text.Json.JsonSerializerOptions.Default);
        result.OrderBy.Should().NotBeNull();
        result.OrderBy!.Property.Should().Be("length(path)");
        result.OrderBy.Descending.Should().BeTrue();
        result.Limit.Should().Be(1);
    }

    [Fact]
    public void Parse_OutOperator_ParsesList()
    {
        var result = _parser.Parse("-status:(deleted OR archived)");

        result.Filter.Should().BeOfType<QueryComparison>();
        var comparison = (QueryComparison)result.Filter!;
        comparison.Condition.Selector.Should().Be("status");
        comparison.Condition.Operator.Should().Be(QueryOperator.NotIn);
        comparison.Condition.Values.Should().BeEquivalentTo(new[] { "deleted", "archived" }, System.Text.Json.JsonSerializerOptions.Default);
    }

    #endregion

    #region Logical Operators

    [Fact]
    public void Parse_SpaceAnd_CombinesConditions()
    {
        var result = _parser.Parse("status:active price:>100");

        result.Filter.Should().BeOfType<QueryAnd>();
        var and = (QueryAnd)result.Filter!;
        and.Children.Should().HaveCount(2);
        and.Children[0].Should().BeOfType<QueryComparison>();
        and.Children[1].Should().BeOfType<QueryComparison>();
    }

    [Fact]
    public void Parse_KeywordOr_CombinesConditions()
    {
        var result = _parser.Parse("name:Laptop OR name:Desktop");

        result.Filter.Should().BeOfType<QueryOr>();
        var or = (QueryOr)result.Filter!;
        or.Children.Should().HaveCount(2);
    }

    [Fact]
    public void Parse_MixedAndOr_RespectsPrecedence()
    {
        // Space (AND) has higher precedence than OR
        // a OR b c should be parsed as a OR (b AND c)
        var result = _parser.Parse("a:1 OR b:2 c:3");

        result.Filter.Should().BeOfType<QueryOr>();
        var or = (QueryOr)result.Filter!;
        or.Children.Should().HaveCount(2);
        or.Children[0].Should().BeOfType<QueryComparison>();
        or.Children[1].Should().BeOfType<QueryAnd>();
    }

    #endregion

    #region Parentheses Grouping

    [Fact]
    public void Parse_Parentheses_GroupsConditions()
    {
        var result = _parser.Parse("(status:active price:>100)");

        result.Filter.Should().BeOfType<QueryAnd>();
    }

    [Fact]
    public void Parse_NestedParentheses_GroupsCorrectly()
    {
        var result = _parser.Parse("(a:1 OR b:2) c:3");

        result.Filter.Should().BeOfType<QueryAnd>();
        var and = (QueryAnd)result.Filter!;
        and.Children.Should().HaveCount(2);
        and.Children[0].Should().BeOfType<QueryOr>();
        and.Children[1].Should().BeOfType<QueryComparison>();
    }

    #endregion

    #region Reserved Parameters

    [Fact]
    public void Parse_BareTextSearch_ExtractsTextSearch()
    {
        var result = _parser.Parse("status:active laptop gaming");

        result.TextSearch.Should().Be("laptop gaming");
        result.Filter.Should().BeOfType<QueryComparison>();
        var comparison = (QueryComparison)result.Filter!;
        comparison.Condition.Selector.Should().Be("status");
    }

    [Fact]
    public void Parse_ScopeParameterDescendants_SetsScope()
    {
        var result = _parser.Parse("scope:descendants name:test");

        result.Scope.Should().Be(QueryScope.Descendants);
        result.Filter.Should().BeOfType<QueryComparison>();
    }

    [Fact]
    public void Parse_ScopeParameterAncestors_SetsScope()
    {
        var result = _parser.Parse("scope:ancestors");

        result.Scope.Should().Be(QueryScope.Ancestors);
        result.Filter.Should().BeNull();
    }

    [Fact]
    public void Parse_ScopeParameterHierarchy_SetsScope()
    {
        var result = _parser.Parse("scope:hierarchy");

        result.Scope.Should().Be(QueryScope.Hierarchy);
    }

    [Fact]
    public void Parse_ScopeParameterNextLevel_SetsScope()
    {
        var result = _parser.Parse("namespace:ACME scope:nextLevel");

        result.Scope.Should().Be(QueryScope.NextLevel);
        result.Path.Should().Be("ACME");
    }

    [Fact]
    public void Parse_ScopeParameterPopulated_AliasesNextLevel()
    {
        _parser.Parse("scope:populated").Scope.Should().Be(QueryScope.NextLevel);
    }

    [Fact]
    public void Parse_AllReservedParams_ExtractsAll()
    {
        var result = _parser.Parse("status:active laptop scope:descendants");

        result.TextSearch.Should().Be("laptop");
        result.Scope.Should().Be(QueryScope.Descendants);
        result.Filter.Should().BeOfType<QueryComparison>();
    }

    #endregion

    #region Nested Property Selectors

    [Fact]
    public void Parse_NestedSelector_ParsesCorrectly()
    {
        var result = _parser.Parse("address.city:Seattle");

        result.Filter.Should().BeOfType<QueryComparison>();
        var comparison = (QueryComparison)result.Filter!;
        comparison.Condition.Selector.Should().Be("address.city");
    }

    [Fact]
    public void Parse_DeepNestedSelector_ParsesCorrectly()
    {
        var result = _parser.Parse("user.profile.settings.theme:dark");

        result.Filter.Should().BeOfType<QueryComparison>();
        var comparison = (QueryComparison)result.Filter!;
        comparison.Condition.Selector.Should().Be("user.profile.settings.theme");
    }

    #endregion

    #region Quoted Values

    [Fact]
    public void Parse_DoubleQuotedValue_RemovesQuotes()
    {
        var result = _parser.Parse("name:\"John Doe\"");

        result.Filter.Should().BeOfType<QueryComparison>();
        var comparison = (QueryComparison)result.Filter!;
        comparison.Condition.Value.Should().Be("John Doe");
    }

    [Fact]
    public void Parse_SingleQuotedValue_RemovesQuotes()
    {
        var result = _parser.Parse("name:'Jane Doe'");

        result.Filter.Should().BeOfType<QueryComparison>();
        var comparison = (QueryComparison)result.Filter!;
        comparison.Condition.Value.Should().Be("Jane Doe");
    }

    #endregion

    #region Edge Cases

    [Fact]
    public void Parse_EmptyQuery_ReturnsEmptyParsedQuery()
    {
        var result = _parser.Parse("");

        result.Should().Be(ParsedQuery.Empty);
        result.Filter.Should().BeNull();
        result.TextSearch.Should().BeNull();
        result.Scope.Should().Be(QueryScope.Exact);
    }

    [Fact]
    public void Parse_NullQuery_ReturnsEmptyParsedQuery()
    {
        var result = _parser.Parse(null);

        result.Should().Be(ParsedQuery.Empty);
    }

    [Fact]
    public void Parse_WhitespaceQuery_ReturnsEmptyParsedQuery()
    {
        var result = _parser.Parse("   ");

        result.Filter.Should().BeNull();
    }

    [Fact]
    public void Parse_OnlyTextSearch_ReturnsSearchWithoutFilter()
    {
        var result = _parser.Parse("laptop");

        result.TextSearch.Should().Be("laptop");
        result.Filter.Should().BeNull();
    }

    [Fact]
    public void Parse_QuotedTextSearch_ReturnsSearchWithoutFilter()
    {
        var result = _parser.Parse("\"laptop gaming\"");

        result.TextSearch.Should().Be("laptop gaming");
        result.Filter.Should().BeNull();
    }

    [Fact]
    public void Parse_DateValue_ParsesAsString()
    {
        var result = _parser.Parse("createdAt:>=2024-01-01");

        result.Filter.Should().BeOfType<QueryComparison>();
        var comparison = (QueryComparison)result.Filter!;
        comparison.Condition.Value.Should().Be("2024-01-01");
    }

    [Fact]
    public void Parse_ComplexQuery_ParsesCorrectly()
    {
        var result = _parser.Parse("status:active price:>=100 price:<=500 category:(Electronics OR Computers) laptop scope:descendants");

        result.TextSearch.Should().Be("laptop");
        result.Scope.Should().Be(QueryScope.Descendants);
        result.Filter.Should().BeOfType<QueryAnd>();
        var and = (QueryAnd)result.Filter!;
        and.Children.Should().HaveCount(4);
    }

    #endregion

    #region Sort Parameter

    [Fact]
    public void Parse_SortAscending_ParsesCorrectly()
    {
        var result = _parser.Parse("sort:name");

        result.OrderBy.Should().NotBeNull();
        result.OrderBy!.Property.Should().Be("name");
        result.OrderBy.Descending.Should().BeFalse();
        result.Filter.Should().BeNull();
    }

    [Fact]
    public void Parse_SortDescending_ParsesCorrectly()
    {
        var result = _parser.Parse("sort:lastAccessedAt-desc");

        result.OrderBy.Should().NotBeNull();
        result.OrderBy!.Property.Should().Be("lastAccessedAt");
        result.OrderBy.Descending.Should().BeTrue();
    }

    [Fact]
    public void Parse_SortAsc_ParsesCorrectly()
    {
        var result = _parser.Parse("sort:createdAt-asc");

        result.OrderBy.Should().NotBeNull();
        result.OrderBy!.Property.Should().Be("createdAt");
        result.OrderBy.Descending.Should().BeFalse();
    }

    [Fact]
    public void Parse_SortWithFilter_ParsesBoth()
    {
        var result = _parser.Parse("status:active sort:name-desc");

        result.OrderBy.Should().NotBeNull();
        result.OrderBy!.Property.Should().Be("name");
        result.OrderBy.Descending.Should().BeTrue();
        result.Filter.Should().BeOfType<QueryComparison>();
    }

    #endregion

    #region Limit Parameter

    [Fact]
    public void Parse_Limit_ParsesCorrectly()
    {
        var result = _parser.Parse("limit:20");

        result.Limit.Should().Be(20);
        result.Filter.Should().BeNull();
    }

    [Fact]
    public void Parse_LimitWithFilter_ParsesBoth()
    {
        var result = _parser.Parse("status:active limit:10");

        result.Limit.Should().Be(10);
        result.Filter.Should().BeOfType<QueryComparison>();
    }

    [Fact]
    public void Parse_LimitInvalidValue_ReturnsNull()
    {
        var result = _parser.Parse("limit:invalid");

        result.Limit.Should().BeNull();
    }

    #endregion

    #region Source Parameter

    [Fact]
    public void Parse_SourceActivity_ParsesCorrectly()
    {
        var result = _parser.Parse("source:activity");

        result.Source.Should().Be(QuerySource.Activity);
        result.Filter.Should().BeNull();
    }

    [Fact]
    public void Parse_SourceDefault_ParsesCorrectly()
    {
        var result = _parser.Parse("source:default");

        result.Source.Should().Be(QuerySource.Default);
    }

    [Fact]
    public void Parse_SourceUnknown_DefaultsToDefault()
    {
        var result = _parser.Parse("source:unknown");

        result.Source.Should().Be(QuerySource.Default);
    }

    [Fact]
    public void Parse_SourceWithOtherParams_ParsesAll()
    {
        var result = _parser.Parse("source:activity nodeType:Story sort:lastAccessedAt-desc limit:20");

        result.Source.Should().Be(QuerySource.Activity);
        result.OrderBy.Should().NotBeNull();
        result.OrderBy!.Property.Should().Be("lastAccessedAt");
        result.OrderBy.Descending.Should().BeTrue();
        result.Limit.Should().Be(20);
        result.Filter.Should().BeOfType<QueryComparison>();
    }

    #endregion

    #region Combined Parameters

    [Fact]
    public void Parse_AllNewParameters_ParsesCorrectly()
    {
        var result = _parser.Parse("status:active laptop scope:descendants sort:price-desc limit:50 source:default");

        result.TextSearch.Should().Be("laptop");
        result.Scope.Should().Be(QueryScope.Descendants);
        result.OrderBy.Should().NotBeNull();
        result.OrderBy!.Property.Should().Be("price");
        result.OrderBy.Descending.Should().BeTrue();
        result.Limit.Should().Be(50);
        result.Source.Should().Be(QuerySource.Default);
        result.Filter.Should().BeOfType<QueryComparison>();
    }

    [Fact]
    public void Parse_ActivityQueryPattern_ParsesCorrectly()
    {
        // This is the typical query pattern for activity-based catalog
        var result = _parser.Parse("source:activity nodeType:type/Story sort:lastAccessedAt-desc limit:20");

        result.Source.Should().Be(QuerySource.Activity);
        result.OrderBy.Should().NotBeNull();
        result.OrderBy!.Property.Should().Be("lastAccessedAt");
        result.OrderBy.Descending.Should().BeTrue();
        result.Limit.Should().Be(20);
        result.Filter.Should().BeOfType<QueryComparison>();
        var comparison = (QueryComparison)result.Filter!;
        comparison.Condition.Selector.Should().Be("nodeType");
        comparison.Condition.Value.Should().Be("type/Story");
    }

    #endregion

    #region Path Values with Slashes

    [Fact]
    public void Parse_PathValueWithSlash_ParsesCorrectly()
    {
        var result = _parser.Parse("nodeType:ACME/Project/Todo");

        result.Filter.Should().BeOfType<QueryComparison>();
        var comparison = (QueryComparison)result.Filter!;
        comparison.Condition.Selector.Should().Be("nodeType");
        comparison.Condition.Value.Should().Be("ACME/Project/Todo");
    }

    [Theory]
    [InlineData("nodeType:Edu/Exercise", "Edu/Exercise")]
    [InlineData("nodeType:Store/Plugin", "Store/Plugin")]
    [InlineData("nodeType:Store/Catalog", "Store/Catalog")]
    public void Parse_SlashedNodeTypeValue_KeepsFullValue(string query, string expected)
    {
        var result = _parser.Parse(query);

        result.Filter.Should().BeOfType<QueryComparison>();
        var comparison = (QueryComparison)result.Filter!;
        comparison.Condition.Selector.Should().Be("nodeType");
        comparison.Condition.Operator.Should().Be(QueryOperator.Equal);
        comparison.Condition.Value.Should().Be(expected);
        result.TextSearch.Should().BeNull("the slashed value is a literal, not a free-text token");
    }

    #endregion

    #region Namespace Parameter

    [Fact]
    public void Parse_NamespaceWithoutScope_DefaultsToChildren()
    {
        var result = _parser.Parse("namespace:MeshWeaver");

        result.Path.Should().Be("MeshWeaver");
        result.Scope.Should().Be(QueryScope.Children);
        result.Filter.Should().BeNull();
    }

    [Fact]
    public void Parse_NamespaceWithDescendantsScope_UsesDescendants()
    {
        var result = _parser.Parse("namespace:MeshWeaver scope:descendants");

        result.Path.Should().Be("MeshWeaver");
        result.Scope.Should().Be(QueryScope.Descendants);
        result.Filter.Should().BeNull();
    }

    [Fact]
    public void Parse_NamespaceWithAncestorsScope_UsesAncestors()
    {
        var result = _parser.Parse("namespace:MeshWeaver/Sub scope:ancestors");

        result.Path.Should().Be("MeshWeaver/Sub");
        result.Scope.Should().Be(QueryScope.Ancestors);
    }

    [Fact]
    public void Parse_NamespaceOnly_DefaultsToChildren()
    {
        var result = _parser.Parse("namespace:MeshWeaver");

        result.Path.Should().Be("MeshWeaver");
        result.Scope.Should().Be(QueryScope.Children);
    }

    [Fact]
    public void Parse_NamespaceWithFilter_CombinesBoth()
    {
        var result = _parser.Parse("namespace:MeshWeaver nodeType:Story");

        result.Path.Should().Be("MeshWeaver");
        result.Scope.Should().Be(QueryScope.Children);
        result.Filter.Should().BeOfType<QueryComparison>();
        var comparison = (QueryComparison)result.Filter!;
        comparison.Condition.Selector.Should().Be("nodeType");
        comparison.Condition.Value.Should().Be("Story");
    }

    [Fact]
    public void Parse_NamespaceWithFilterAndDescendants_CombinesAll()
    {
        var result = _parser.Parse("namespace:MeshWeaver nodeType:Story scope:descendants");

        result.Path.Should().Be("MeshWeaver");
        result.Scope.Should().Be(QueryScope.Descendants);
        result.Filter.Should().BeOfType<QueryComparison>();
    }

    [Fact]
    public void Parse_NamespaceWithNestedPath_ParsesCorrectly()
    {
        var result = _parser.Parse("namespace:ACME/ProductLaunch/Todo");

        result.Path.Should().Be("ACME/ProductLaunch/Todo");
        result.Scope.Should().Be(QueryScope.Children);
    }

    [Fact]
    public void Parse_NamespaceWithChildrenScope_UsesChildren()
    {
        var result = _parser.Parse("namespace:MeshWeaver");

        result.Path.Should().Be("MeshWeaver");
        result.Scope.Should().Be(QueryScope.Children);
    }

    #endregion

    #region Namespace Alternation (A|B|C exact membership — the agent / model registry union)

    private static QueryCondition? FindCondition(QueryNode? node, string selector) => node switch
    {
        QueryComparison c when c.Condition.Selector.Equals(selector, System.StringComparison.OrdinalIgnoreCase)
            => c.Condition,
        QueryAnd a => a.Children.Select(ch => FindCondition(ch, selector)).FirstOrDefault(x => x != null),
        QueryOr o => o.Children.Select(ch => FindCondition(ch, selector)).FirstOrDefault(x => x != null),
        _ => null,
    };

    [Fact]
    public void Parse_NamespaceAlternation_IsExactMembershipFilter()
    {
        // The canonical agent registry query. `namespace:A|B|C` lists the platform + space + user
        // namespaces DIRECTLY — exact membership (`namespace IN (...)`), no ancestor/graph walk.
        var result = _parser.Parse("nodeType:Agent namespace:rbuergi/Agent|AgenticPension/Agent|Agent");

        // No single base namespace/scope survives — it's purely a filter, so no path-based walk.
        result.Path.Should().BeNull();
        result.Scope.Should().Be(QueryScope.Exact);

        var ns = FindCondition(result.Filter, "namespace");
        ns.Should().NotBeNull();
        ns!.Operator.Should().Be(QueryOperator.In);
        ns.Values.Should().BeEquivalentTo(new[] { "rbuergi/Agent", "AgenticPension/Agent", "Agent" },
            System.Text.Json.JsonSerializerOptions.Default);

        // The nodeType filter is still ANDed in alongside the namespace membership.
        FindCondition(result.Filter, "nodeType")!.Value.Should().Be("Agent");

        // Partition routing sees the namespace candidates (so the user + space schemas are queried).
        result.ExtractNamespaces().Should().BeEquivalentTo(
            new[] { "rbuergi/Agent", "AgenticPension/Agent", "Agent" },
            System.Text.Json.JsonSerializerOptions.Default);
    }

    [Fact]
    public void Parse_NamespaceAlternation_DedupesAndTrims()
    {
        var result = _parser.Parse("namespace:Agent|Agent|/Model/ nodeType:Agent");

        var ns = FindCondition(result.Filter, "namespace");
        ns!.Operator.Should().Be(QueryOperator.In);
        ns.Values.Should().BeEquivalentTo(new[] { "Agent", "Model" },
            System.Text.Json.JsonSerializerOptions.Default);
    }

    [Fact]
    public void Parse_SingleNamespace_Unchanged_StaysPathScoped()
    {
        // A SINGLE namespace (no alternation) is deliberately left untouched — still a base namespace
        // + scope walk — so existing Role/Group/AccessAssignment callers keep their behaviour. Only
        // the `|` alternation form becomes an exact-membership filter.
        var result = _parser.Parse("namespace:ACME/Project nodeType:Role scope:selfAndAncestors");

        result.Path.Should().Be("ACME/Project");
        result.Scope.Should().Be(QueryScope.AncestorsAndSelf);
        FindCondition(result.Filter, "namespace").Should().BeNull("single namespace is NOT a filter");
    }

    #endregion

    #region Children Scope

    [Fact]
    public void Parse_ScopeChildren_SetsScope()
    {
        var result = _parser.Parse("namespace:");

        result.Scope.Should().Be(QueryScope.Children);
        result.Filter.Should().BeNull();
    }

    [Fact]
    public void Parse_PathWithChildrenScope_ParsesBoth()
    {
        var result = _parser.Parse("namespace:products");

        result.Path.Should().Be("products");
        result.Scope.Should().Be(QueryScope.Children);
    }

    #endregion

    #region Subtree and Self Scope

    [Fact]
    public void Parse_ScopeSubtree_SetsScope()
    {
        var result = _parser.Parse("scope:subtree");

        result.Scope.Should().Be(QueryScope.Subtree);
    }

    [Fact]
    public void Parse_ScopeMyselfAndAncestors_SetsAncestorsAndSelf()
    {
        // myselfAndAncestors should be an alias for ancestorsAndSelf
        var result = _parser.Parse("scope:myselfAndAncestors");

        result.Scope.Should().Be(QueryScope.AncestorsAndSelf);
    }

    [Fact]
    public void Parse_ScopeAncestorsAndSelf_SetsScope()
    {
        var result = _parser.Parse("scope:ancestorsAndSelf");

        result.Scope.Should().Be(QueryScope.AncestorsAndSelf);
    }

    [Fact]
    public void Parse_NamespaceWithSubtreeScope_DegradesToDescendants()
    {
        // `namespace:X` names a NAMESPACE, never the node AT path X (that node's own namespace is
        // its parent — the user root `rbuergi` has namespace ""). Subtree would include the self
        // node in every backend's path walk, which is how the user node leaked into its own
        // `source:activity namespace:{user} scope:subtree` activity feed — so namespace-derived
        // paths degrade subtree to descendants.
        var result = _parser.Parse("namespace:rbuergi scope:subtree");

        result.Path.Should().Be("rbuergi");
        result.Scope.Should().Be(QueryScope.Descendants);
    }

    [Fact]
    public void Parse_NamespaceWithSelfAndDescendantsAlias_DegradesToDescendants()
    {
        var result = _parser.Parse("namespace:rbuergi scope:selfAndDescendants");

        result.Path.Should().Be("rbuergi");
        result.Scope.Should().Be(QueryScope.Descendants);
    }

    [Fact]
    public void Parse_EmptyNamespace_KeepsEmptyPathWithChildrenScope()
    {
        // `namespace:` (empty value) = the root-level rows (namespace == "") — Path must stay ""
        // (NOT null) so backends still push the root-children scope instead of dropping it.
        var result = _parser.Parse("namespace: is:main");

        result.Path.Should().Be("");
        result.Scope.Should().Be(QueryScope.Children);
    }

    [Fact]
    public void Parse_PathWithSubtreeScope_FindsSelfAndDescendants()
    {
        // subtree = self + descendants - important for finding agents under a NodeType
        var result = _parser.Parse("path:ACME/Project nodeType:Agent scope:subtree");

        result.Path.Should().Be("ACME/Project");
        result.Scope.Should().Be(QueryScope.Subtree);
        result.Filter.Should().BeOfType<QueryComparison>();
        var comparison = (QueryComparison)result.Filter!;
        comparison.Condition.Selector.Should().Be("nodeType");
        comparison.Condition.Value.Should().Be("Agent");
    }

    #endregion

    #region Select Parameter

    [Fact]
    public void Parse_SelectSingleProperty_ParsesCorrectly()
    {
        var result = _parser.Parse("select:name");

        result.Select.Should().NotBeNull();
        result.Select.Should().BeEquivalentTo(new[] { "name" }, System.Text.Json.JsonSerializerOptions.Default);
        result.Filter.Should().BeNull();
    }

    [Fact]
    public void Parse_SelectMultipleProperties_ParsesCommaSeparated()
    {
        var result = _parser.Parse("select:name,nodeType,icon");

        result.Select.Should().NotBeNull();
        result.Select.Should().BeEquivalentTo(new[] { "name", "nodeType", "icon" }, System.Text.Json.JsonSerializerOptions.Default);
    }

    [Fact]
    public void Parse_SelectWithFilter_ParsesBoth()
    {
        var result = _parser.Parse("nodeType:Story select:path,name");

        result.Select.Should().NotBeNull();
        result.Select.Should().BeEquivalentTo(new[] { "path", "name" }, System.Text.Json.JsonSerializerOptions.Default);
        result.Filter.Should().BeOfType<QueryComparison>();
        var comparison = (QueryComparison)result.Filter!;
        comparison.Condition.Selector.Should().Be("nodeType");
        comparison.Condition.Value.Should().Be("Story");
    }

    [Fact]
    public void Parse_SelectWithAllParams_ParsesAll()
    {
        var result = _parser.Parse("namespace:Systemorph select:name,nodeType sort:name limit:10");

        result.Select.Should().NotBeNull();
        result.Select.Should().BeEquivalentTo(new[] { "name", "nodeType" }, System.Text.Json.JsonSerializerOptions.Default);
        result.Path.Should().Be("Systemorph");
        result.OrderBy.Should().NotBeNull();
        result.OrderBy!.Property.Should().Be("name");
        result.Limit.Should().Be(10);
    }

    [Fact]
    public void Parse_NoSelect_SelectIsNull()
    {
        var result = _parser.Parse("nodeType:Story");

        result.Select.Should().BeNull();
    }

    #endregion
}
