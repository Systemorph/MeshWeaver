using FluentAssertions;
using MeshWeaver.Mesh.Query;
using Xunit;

namespace MeshWeaver.Hosting.Monolith.Test;

public class RsqlParserTests
{
    private readonly RsqlParser _parser = new();

    #region Basic Comparison Operators

    [Fact]
    public void Parse_EqualOperator_ReturnsCorrectCondition()
    {
        var result = _parser.Parse("name==John");

        result.Filter.Should().BeOfType<RsqlComparison>();
        var comparison = (RsqlComparison)result.Filter!;
        comparison.Condition.Selector.Should().Be("name");
        comparison.Condition.Operator.Should().Be(RsqlOperator.Equal);
        comparison.Condition.Value.Should().Be("John");
    }

    [Fact]
    public void Parse_NotEqualOperator_ReturnsCorrectCondition()
    {
        var result = _parser.Parse("status!=inactive");

        result.Filter.Should().BeOfType<RsqlComparison>();
        var comparison = (RsqlComparison)result.Filter!;
        comparison.Condition.Selector.Should().Be("status");
        comparison.Condition.Operator.Should().Be(RsqlOperator.NotEqual);
        comparison.Condition.Value.Should().Be("inactive");
    }

    [Fact]
    public void Parse_GreaterThanOperator_ReturnsCorrectCondition()
    {
        var result = _parser.Parse("price=gt=100");

        result.Filter.Should().BeOfType<RsqlComparison>();
        var comparison = (RsqlComparison)result.Filter!;
        comparison.Condition.Selector.Should().Be("price");
        comparison.Condition.Operator.Should().Be(RsqlOperator.GreaterThan);
        comparison.Condition.Value.Should().Be("100");
    }

    [Fact]
    public void Parse_LessThanOperator_ReturnsCorrectCondition()
    {
        var result = _parser.Parse("age=lt=18");

        result.Filter.Should().BeOfType<RsqlComparison>();
        var comparison = (RsqlComparison)result.Filter!;
        comparison.Condition.Selector.Should().Be("age");
        comparison.Condition.Operator.Should().Be(RsqlOperator.LessThan);
        comparison.Condition.Value.Should().Be("18");
    }

    [Fact]
    public void Parse_GreaterOrEqualOperator_ReturnsCorrectCondition()
    {
        var result = _parser.Parse("rating=ge=4.5");

        result.Filter.Should().BeOfType<RsqlComparison>();
        var comparison = (RsqlComparison)result.Filter!;
        comparison.Condition.Selector.Should().Be("rating");
        comparison.Condition.Operator.Should().Be(RsqlOperator.GreaterOrEqual);
        comparison.Condition.Value.Should().Be("4.5");
    }

    [Fact]
    public void Parse_LessOrEqualOperator_ReturnsCorrectCondition()
    {
        var result = _parser.Parse("quantity=le=10");

        result.Filter.Should().BeOfType<RsqlComparison>();
        var comparison = (RsqlComparison)result.Filter!;
        comparison.Condition.Selector.Should().Be("quantity");
        comparison.Condition.Operator.Should().Be(RsqlOperator.LessOrEqual);
        comparison.Condition.Value.Should().Be("10");
    }

    [Fact]
    public void Parse_LikeOperator_ReturnsCorrectCondition()
    {
        var result = _parser.Parse("name=like=*laptop*");

        result.Filter.Should().BeOfType<RsqlComparison>();
        var comparison = (RsqlComparison)result.Filter!;
        comparison.Condition.Selector.Should().Be("name");
        comparison.Condition.Operator.Should().Be(RsqlOperator.Like);
        comparison.Condition.Value.Should().Be("*laptop*");
    }

    #endregion

    #region In/Out Operators with Lists

    [Fact]
    public void Parse_InOperator_ParsesList()
    {
        var result = _parser.Parse("category=in=(Electronics,Computers,Gadgets)");

        result.Filter.Should().BeOfType<RsqlComparison>();
        var comparison = (RsqlComparison)result.Filter!;
        comparison.Condition.Selector.Should().Be("category");
        comparison.Condition.Operator.Should().Be(RsqlOperator.In);
        comparison.Condition.Values.Should().BeEquivalentTo(["Electronics", "Computers", "Gadgets"]);
    }

    [Fact]
    public void Parse_OutOperator_ParsesList()
    {
        var result = _parser.Parse("status=out=(deleted,archived)");

        result.Filter.Should().BeOfType<RsqlComparison>();
        var comparison = (RsqlComparison)result.Filter!;
        comparison.Condition.Selector.Should().Be("status");
        comparison.Condition.Operator.Should().Be(RsqlOperator.NotIn);
        comparison.Condition.Values.Should().BeEquivalentTo(["deleted", "archived"]);
    }

    #endregion

    #region Logical Operators

    [Fact]
    public void Parse_SemicolonAnd_CombinesConditions()
    {
        var result = _parser.Parse("status==active;price=gt=100");

        result.Filter.Should().BeOfType<RsqlAnd>();
        var and = (RsqlAnd)result.Filter!;
        and.Children.Should().HaveCount(2);
        and.Children[0].Should().BeOfType<RsqlComparison>();
        and.Children[1].Should().BeOfType<RsqlComparison>();
    }

    [Fact]
    public void Parse_KeywordAnd_CombinesConditions()
    {
        var result = _parser.Parse("status==active and price=gt=100");

        result.Filter.Should().BeOfType<RsqlAnd>();
        var and = (RsqlAnd)result.Filter!;
        and.Children.Should().HaveCount(2);
    }

    [Fact]
    public void Parse_CommaOr_CombinesConditions()
    {
        var result = _parser.Parse("name==Laptop,name==Desktop");

        result.Filter.Should().BeOfType<RsqlOr>();
        var or = (RsqlOr)result.Filter!;
        or.Children.Should().HaveCount(2);
    }

    [Fact]
    public void Parse_KeywordOr_CombinesConditions()
    {
        var result = _parser.Parse("category==Electronics or category==Computers");

        result.Filter.Should().BeOfType<RsqlOr>();
        var or = (RsqlOr)result.Filter!;
        or.Children.Should().HaveCount(2);
    }

    [Fact]
    public void Parse_MixedAndOr_RespectsPrecedence()
    {
        // AND has higher precedence than OR
        // a,b;c should be parsed as a OR (b AND c)
        var result = _parser.Parse("a==1,b==2;c==3");

        result.Filter.Should().BeOfType<RsqlOr>();
        var or = (RsqlOr)result.Filter!;
        or.Children.Should().HaveCount(2);
        or.Children[0].Should().BeOfType<RsqlComparison>();
        or.Children[1].Should().BeOfType<RsqlAnd>();
    }

    #endregion

    #region Parentheses Grouping

    [Fact]
    public void Parse_Parentheses_GroupsConditions()
    {
        var result = _parser.Parse("(status==active;price=gt=100)");

        result.Filter.Should().BeOfType<RsqlAnd>();
    }

    [Fact]
    public void Parse_NestedParentheses_GroupsCorrectly()
    {
        var result = _parser.Parse("(a==1,b==2);c==3");

        result.Filter.Should().BeOfType<RsqlAnd>();
        var and = (RsqlAnd)result.Filter!;
        and.Children.Should().HaveCount(2);
        and.Children[0].Should().BeOfType<RsqlOr>();
        and.Children[1].Should().BeOfType<RsqlComparison>();
    }

    #endregion

    #region Reserved Parameters

    [Fact]
    public void Parse_SearchParameter_ExtractsTextSearch()
    {
        var result = _parser.Parse("status==active;$search=laptop gaming");

        result.TextSearch.Should().Be("laptop gaming");
        result.Filter.Should().BeOfType<RsqlComparison>();
        var comparison = (RsqlComparison)result.Filter!;
        comparison.Condition.Selector.Should().Be("status");
    }

    [Fact]
    public void Parse_ScopeParameterDescendants_SetsScope()
    {
        var result = _parser.Parse("$scope=descendants;name==test");

        result.Scope.Should().Be(QueryScope.Descendants);
        result.Filter.Should().BeOfType<RsqlComparison>();
    }

    [Fact]
    public void Parse_ScopeParameterAncestors_SetsScope()
    {
        var result = _parser.Parse("$scope=ancestors");

        result.Scope.Should().Be(QueryScope.Ancestors);
        result.Filter.Should().BeNull();
    }

    [Fact]
    public void Parse_ScopeParameterHierarchy_SetsScope()
    {
        var result = _parser.Parse("$scope=hierarchy");

        result.Scope.Should().Be(QueryScope.Hierarchy);
    }

    [Fact]
    public void Parse_AllReservedParams_ExtractsAll()
    {
        var result = _parser.Parse("status==active;$search=laptop;$scope=descendants");

        result.TextSearch.Should().Be("laptop");
        result.Scope.Should().Be(QueryScope.Descendants);
        result.Filter.Should().BeOfType<RsqlComparison>();
    }

    #endregion

    #region Nested Property Selectors

    [Fact]
    public void Parse_NestedSelector_ParsesCorrectly()
    {
        var result = _parser.Parse("address.city==Seattle");

        result.Filter.Should().BeOfType<RsqlComparison>();
        var comparison = (RsqlComparison)result.Filter!;
        comparison.Condition.Selector.Should().Be("address.city");
    }

    [Fact]
    public void Parse_DeepNestedSelector_ParsesCorrectly()
    {
        var result = _parser.Parse("user.profile.settings.theme==dark");

        result.Filter.Should().BeOfType<RsqlComparison>();
        var comparison = (RsqlComparison)result.Filter!;
        comparison.Condition.Selector.Should().Be("user.profile.settings.theme");
    }

    #endregion

    #region Quoted Values

    [Fact]
    public void Parse_DoubleQuotedValue_RemovesQuotes()
    {
        var result = _parser.Parse("name==\"John Doe\"");

        result.Filter.Should().BeOfType<RsqlComparison>();
        var comparison = (RsqlComparison)result.Filter!;
        comparison.Condition.Value.Should().Be("John Doe");
    }

    [Fact]
    public void Parse_SingleQuotedValue_RemovesQuotes()
    {
        var result = _parser.Parse("name=='Jane Doe'");

        result.Filter.Should().BeOfType<RsqlComparison>();
        var comparison = (RsqlComparison)result.Filter!;
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
    public void Parse_OnlySearchParameter_ReturnsSearchWithoutFilter()
    {
        var result = _parser.Parse("$search=laptop");

        result.TextSearch.Should().Be("laptop");
        result.Filter.Should().BeNull();
    }

    [Fact]
    public void Parse_DateValue_ParsesAsString()
    {
        var result = _parser.Parse("createdAt=ge=2024-01-01");

        result.Filter.Should().BeOfType<RsqlComparison>();
        var comparison = (RsqlComparison)result.Filter!;
        comparison.Condition.Value.Should().Be("2024-01-01");
    }

    [Fact]
    public void Parse_CaseInsensitiveOperators_Works()
    {
        var result = _parser.Parse("price=GT=100");

        result.Filter.Should().BeOfType<RsqlComparison>();
        var comparison = (RsqlComparison)result.Filter!;
        comparison.Condition.Operator.Should().Be(RsqlOperator.GreaterThan);
    }

    [Fact]
    public void Parse_ComplexQuery_ParsesCorrectly()
    {
        var result = _parser.Parse("status==active;price=ge=100;price=le=500;category=in=(Electronics,Computers);$search=laptop;$scope=descendants");

        result.TextSearch.Should().Be("laptop");
        result.Scope.Should().Be(QueryScope.Descendants);
        result.Filter.Should().BeOfType<RsqlAnd>();
        var and = (RsqlAnd)result.Filter!;
        and.Children.Should().HaveCount(4);
    }

    #endregion

    #region OrderBy Parameter

    [Fact]
    public void Parse_OrderByAscending_ParsesCorrectly()
    {
        var result = _parser.Parse("$orderBy=name");

        result.OrderBy.Should().NotBeNull();
        result.OrderBy!.Property.Should().Be("name");
        result.OrderBy.Descending.Should().BeFalse();
        result.Filter.Should().BeNull();
    }

    [Fact]
    public void Parse_OrderByDescending_ParsesCorrectly()
    {
        var result = _parser.Parse("$orderBy=lastAccessedAt:desc");

        result.OrderBy.Should().NotBeNull();
        result.OrderBy!.Property.Should().Be("lastAccessedAt");
        result.OrderBy.Descending.Should().BeTrue();
    }

    [Fact]
    public void Parse_OrderByAsc_ParsesCorrectly()
    {
        var result = _parser.Parse("$orderBy=createdAt:asc");

        result.OrderBy.Should().NotBeNull();
        result.OrderBy!.Property.Should().Be("createdAt");
        result.OrderBy.Descending.Should().BeFalse();
    }

    [Fact]
    public void Parse_OrderByWithFilter_ParsesBoth()
    {
        var result = _parser.Parse("status==active;$orderBy=name:desc");

        result.OrderBy.Should().NotBeNull();
        result.OrderBy!.Property.Should().Be("name");
        result.OrderBy.Descending.Should().BeTrue();
        result.Filter.Should().BeOfType<RsqlComparison>();
    }

    #endregion

    #region Limit Parameter

    [Fact]
    public void Parse_Limit_ParsesCorrectly()
    {
        var result = _parser.Parse("$limit=20");

        result.Limit.Should().Be(20);
        result.Filter.Should().BeNull();
    }

    [Fact]
    public void Parse_LimitWithFilter_ParsesBoth()
    {
        var result = _parser.Parse("status==active;$limit=10");

        result.Limit.Should().Be(10);
        result.Filter.Should().BeOfType<RsqlComparison>();
    }

    [Fact]
    public void Parse_LimitInvalidValue_ReturnsNull()
    {
        var result = _parser.Parse("$limit=invalid");

        result.Limit.Should().BeNull();
    }

    #endregion

    #region Source Parameter

    [Fact]
    public void Parse_SourceActivity_ParsesCorrectly()
    {
        var result = _parser.Parse("$source=activity");

        result.Source.Should().Be(QuerySource.Activity);
        result.Filter.Should().BeNull();
    }

    [Fact]
    public void Parse_SourceDefault_ParsesCorrectly()
    {
        var result = _parser.Parse("$source=default");

        result.Source.Should().Be(QuerySource.Default);
    }

    [Fact]
    public void Parse_SourceUnknown_DefaultsToDefault()
    {
        var result = _parser.Parse("$source=unknown");

        result.Source.Should().Be(QuerySource.Default);
    }

    [Fact]
    public void Parse_SourceWithOtherParams_ParsesAll()
    {
        var result = _parser.Parse("$source=activity;nodeType==Story;$orderBy=lastAccessedAt:desc;$limit=20");

        result.Source.Should().Be(QuerySource.Activity);
        result.OrderBy.Should().NotBeNull();
        result.OrderBy!.Property.Should().Be("lastAccessedAt");
        result.OrderBy.Descending.Should().BeTrue();
        result.Limit.Should().Be(20);
        result.Filter.Should().BeOfType<RsqlComparison>();
    }

    #endregion

    #region Combined Parameters

    [Fact]
    public void Parse_AllNewParameters_ParsesCorrectly()
    {
        var result = _parser.Parse("status==active;$search=laptop;$scope=descendants;$orderBy=price:desc;$limit=50;$source=default");

        result.TextSearch.Should().Be("laptop");
        result.Scope.Should().Be(QueryScope.Descendants);
        result.OrderBy.Should().NotBeNull();
        result.OrderBy!.Property.Should().Be("price");
        result.OrderBy.Descending.Should().BeTrue();
        result.Limit.Should().Be(50);
        result.Source.Should().Be(QuerySource.Default);
        result.Filter.Should().BeOfType<RsqlComparison>();
    }

    [Fact]
    public void Parse_ActivityQueryPattern_ParsesCorrectly()
    {
        // This is the typical query pattern for activity-based catalog
        var result = _parser.Parse("$source=activity;nodeType==type/Story;$orderBy=lastAccessedAt:desc;$limit=20");

        result.Source.Should().Be(QuerySource.Activity);
        result.OrderBy.Should().NotBeNull();
        result.OrderBy!.Property.Should().Be("lastAccessedAt");
        result.OrderBy.Descending.Should().BeTrue();
        result.Limit.Should().Be(20);
        result.Filter.Should().BeOfType<RsqlComparison>();
        var comparison = (RsqlComparison)result.Filter!;
        comparison.Condition.Selector.Should().Be("nodeType");
        comparison.Condition.Value.Should().Be("type/Story");
    }

    #endregion
}
