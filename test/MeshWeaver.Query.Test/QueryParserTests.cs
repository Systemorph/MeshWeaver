using FluentAssertions;
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
        comparison.Condition.Values.Should().BeEquivalentTo(["Electronics", "Computers", "Gadgets"]);
    }

    [Fact]
    public void Parse_OutOperator_ParsesList()
    {
        var result = _parser.Parse("-status:(deleted OR archived)");

        result.Filter.Should().BeOfType<QueryComparison>();
        var comparison = (QueryComparison)result.Filter!;
        comparison.Condition.Selector.Should().Be("status");
        comparison.Condition.Operator.Should().Be(QueryOperator.NotIn);
        comparison.Condition.Values.Should().BeEquivalentTo(["deleted", "archived"]);
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
    public void Parse_ScopeSelf_SetsScopeExact()
    {
        var result = _parser.Parse("scope:self");

        result.Scope.Should().Be(QueryScope.Exact);
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
        result.Select.Should().BeEquivalentTo(["name"]);
        result.Filter.Should().BeNull();
    }

    [Fact]
    public void Parse_SelectMultipleProperties_ParsesCommaSeparated()
    {
        var result = _parser.Parse("select:name,nodeType,icon");

        result.Select.Should().NotBeNull();
        result.Select.Should().BeEquivalentTo(["name", "nodeType", "icon"]);
    }

    [Fact]
    public void Parse_SelectWithFilter_ParsesBoth()
    {
        var result = _parser.Parse("nodeType:Story select:path,name");

        result.Select.Should().NotBeNull();
        result.Select.Should().BeEquivalentTo(["path", "name"]);
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
        result.Select.Should().BeEquivalentTo(["name", "nodeType"]);
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
