using System;
using System.Text.Json;
using FluentAssertions;
using MeshWeaver.Hosting.Persistence.Query;
using MeshWeaver.Mesh.Query;
using Xunit;

namespace MeshWeaver.Hosting.Monolith.Test;

public class QueryEvaluatorTests
{
    private readonly QueryEvaluator _evaluator = new();
    private readonly QueryParser _parser = new();

    #region Test Objects

    private record Product(string Name, decimal Price, string Category, bool InStock, DateTimeOffset CreatedAt);
    private record Customer(string Name, Address Address, int Age);
    private record Address(string City, string Country);

    #endregion

    #region Basic Comparisons

    [Fact]
    public void Matches_EqualOperator_MatchesExact()
    {
        var product = new Product("Laptop", 999.99m, "Electronics", true, DateTimeOffset.Now);
        var query = _parser.Parse("name:Laptop");

        _evaluator.Matches(product, query).Should().BeTrue();
    }

    [Fact]
    public void Matches_EqualOperator_CaseInsensitive()
    {
        var product = new Product("Laptop", 999.99m, "Electronics", true, DateTimeOffset.Now);
        var query = _parser.Parse("name:laptop");

        _evaluator.Matches(product, query).Should().BeTrue();
    }

    [Fact]
    public void Matches_NotEqualOperator_Works()
    {
        var product = new Product("Laptop", 999.99m, "Electronics", true, DateTimeOffset.Now);
        var query = _parser.Parse("-category:Furniture");

        _evaluator.Matches(product, query).Should().BeTrue();
    }

    [Fact]
    public void Matches_GreaterThanOperator_Works()
    {
        var product = new Product("Laptop", 999.99m, "Electronics", true, DateTimeOffset.Now);
        var query = _parser.Parse("price:>500");

        _evaluator.Matches(product, query).Should().BeTrue();
    }

    [Fact]
    public void Matches_LessThanOperator_Works()
    {
        var product = new Product("Mouse", 29.99m, "Electronics", true, DateTimeOffset.Now);
        var query = _parser.Parse("price:<50");

        _evaluator.Matches(product, query).Should().BeTrue();
    }

    [Fact]
    public void Matches_GreaterOrEqualOperator_Works()
    {
        var product = new Product("Laptop", 999.99m, "Electronics", true, DateTimeOffset.Now);
        var query = _parser.Parse("price:>=999.99");

        _evaluator.Matches(product, query).Should().BeTrue();
    }

    [Fact]
    public void Matches_LessOrEqualOperator_Works()
    {
        var product = new Product("Laptop", 999.99m, "Electronics", true, DateTimeOffset.Now);
        var query = _parser.Parse("price:<=1000");

        _evaluator.Matches(product, query).Should().BeTrue();
    }

    #endregion

    #region In/Out Operators

    [Fact]
    public void Matches_InOperator_MatchesAnyValue()
    {
        var product = new Product("Laptop", 999.99m, "Electronics", true, DateTimeOffset.Now);
        var query = _parser.Parse("category:(Electronics OR Computers OR Gadgets)");

        _evaluator.Matches(product, query).Should().BeTrue();
    }

    [Fact]
    public void Matches_InOperator_NoMatchReturnsFalse()
    {
        var product = new Product("Laptop", 999.99m, "Electronics", true, DateTimeOffset.Now);
        var query = _parser.Parse("category:(Furniture OR Clothing)");

        _evaluator.Matches(product, query).Should().BeFalse();
    }

    [Fact]
    public void Matches_OutOperator_ExcludesValues()
    {
        var product = new Product("Laptop", 999.99m, "Electronics", true, DateTimeOffset.Now);
        var query = _parser.Parse("-category:(Furniture OR Clothing)");

        _evaluator.Matches(product, query).Should().BeTrue();
    }

    #endregion

    #region Like/Wildcard Operator

    [Fact]
    public void Matches_LikeOperator_ContainsPattern()
    {
        var product = new Product("Gaming Laptop Pro", 999.99m, "Electronics", true, DateTimeOffset.Now);
        var query = _parser.Parse("name:*Laptop*");

        _evaluator.Matches(product, query).Should().BeTrue();
    }

    [Fact]
    public void Matches_LikeOperator_StartsWithPattern()
    {
        var product = new Product("Gaming Laptop Pro", 999.99m, "Electronics", true, DateTimeOffset.Now);
        var query = _parser.Parse("name:Gaming*");

        _evaluator.Matches(product, query).Should().BeTrue();
    }

    [Fact]
    public void Matches_LikeOperator_EndsWithPattern()
    {
        var product = new Product("Gaming Laptop Pro", 999.99m, "Electronics", true, DateTimeOffset.Now);
        var query = _parser.Parse("name:*Pro");

        _evaluator.Matches(product, query).Should().BeTrue();
    }

    #endregion

    #region Nested Properties

    [Fact]
    public void Matches_NestedProperty_Works()
    {
        var customer = new Customer("John", new Address("Seattle", "USA"), 30);
        var query = _parser.Parse("address.city:Seattle");

        _evaluator.Matches(customer, query).Should().BeTrue();
    }

    [Fact]
    public void Matches_NestedProperty_CaseInsensitive()
    {
        var customer = new Customer("John", new Address("Seattle", "USA"), 30);
        var query = _parser.Parse("Address.City:seattle");

        _evaluator.Matches(customer, query).Should().BeTrue();
    }

    #endregion

    #region Logical Operators

    [Fact]
    public void Matches_AndConditions_AllMustMatch()
    {
        var product = new Product("Laptop", 999.99m, "Electronics", true, DateTimeOffset.Now);
        var query = _parser.Parse("name:Laptop category:Electronics");

        _evaluator.Matches(product, query).Should().BeTrue();
    }

    [Fact]
    public void Matches_AndConditions_OneFails()
    {
        var product = new Product("Laptop", 999.99m, "Electronics", true, DateTimeOffset.Now);
        var query = _parser.Parse("name:Laptop category:Furniture");

        _evaluator.Matches(product, query).Should().BeFalse();
    }

    [Fact]
    public void Matches_OrConditions_OneMatchSuffices()
    {
        var product = new Product("Laptop", 999.99m, "Electronics", true, DateTimeOffset.Now);
        var query = _parser.Parse("name:Desktop OR name:Laptop");

        _evaluator.Matches(product, query).Should().BeTrue();
    }

    [Fact]
    public void Matches_ComplexLogic_Works()
    {
        var product = new Product("Laptop", 999.99m, "Electronics", true, DateTimeOffset.Now);
        var query = _parser.Parse("(name:Laptop OR name:Desktop) category:Electronics");

        _evaluator.Matches(product, query).Should().BeTrue();
    }

    #endregion

    #region JsonElement Support

    [Fact]
    public void Matches_JsonElement_WorksWithProperties()
    {
        var json = """{"name": "Laptop", "price": 999.99, "category": "Electronics"}""";
        var jsonElement = JsonDocument.Parse(json).RootElement;
        var query = _parser.Parse("name:Laptop");

        _evaluator.Matches(jsonElement, query).Should().BeTrue();
    }

    [Fact]
    public void Matches_JsonElement_NumericComparison()
    {
        var json = """{"name": "Laptop", "price": 999.99, "category": "Electronics"}""";
        var jsonElement = JsonDocument.Parse(json).RootElement;
        var query = _parser.Parse("price:>500");

        _evaluator.Matches(jsonElement, query).Should().BeTrue();
    }

    [Fact]
    public void Matches_JsonElement_NestedProperty()
    {
        var json = """{"name": "John", "address": {"city": "Seattle", "country": "USA"}}""";
        var jsonElement = JsonDocument.Parse(json).RootElement;
        var query = _parser.Parse("address.city:Seattle");

        _evaluator.Matches(jsonElement, query).Should().BeTrue();
    }

    #endregion

    #region Date Comparisons

    [Fact]
    public void Matches_DateComparison_GreaterOrEqual()
    {
        var product = new Product("Laptop", 999.99m, "Electronics", true, new DateTimeOffset(2024, 6, 15, 0, 0, 0, TimeSpan.Zero));
        var query = _parser.Parse("createdAt:>=2024-01-01");

        _evaluator.Matches(product, query).Should().BeTrue();
    }

    [Fact]
    public void Matches_DateComparison_LessThan()
    {
        var product = new Product("Laptop", 999.99m, "Electronics", true, new DateTimeOffset(2024, 6, 15, 0, 0, 0, TimeSpan.Zero));
        var query = _parser.Parse("createdAt:<2025-01-01");

        _evaluator.Matches(product, query).Should().BeTrue();
    }

    #endregion

    #region Fuzzy Text Search

    [Fact]
    public void Matches_TextSearch_MatchesStringProperty()
    {
        var product = new Product("Gaming Laptop Pro", 999.99m, "Electronics", true, DateTimeOffset.Now);
        var query = _parser.Parse("laptop");

        _evaluator.Matches(product, query).Should().BeTrue();
    }

    [Fact]
    public void Matches_TextSearch_NoMatchReturnsFalse()
    {
        var product = new Product("Desktop Computer", 999.99m, "Electronics", true, DateTimeOffset.Now);
        var query = _parser.Parse("laptop");

        _evaluator.Matches(product, query).Should().BeFalse();
    }

    [Fact]
    public void GetFuzzyScore_ReturnsHigherScoreForBetterMatch()
    {
        var product1 = new Product("Gaming Laptop Pro", 999.99m, "Electronics", true, DateTimeOffset.Now);
        var product2 = new Product("Old Laptop", 299.99m, "Electronics", true, DateTimeOffset.Now);

        var score1 = _evaluator.GetFuzzyScore(product1, "Gaming Laptop");
        var score2 = _evaluator.GetFuzzyScore(product2, "Gaming Laptop");

        score1.Should().BeGreaterThan(score2);
    }

    [Fact]
    public void Matches_CombinedFilterAndSearch_Works()
    {
        var product = new Product("Gaming Laptop Pro", 999.99m, "Electronics", true, DateTimeOffset.Now);
        var query = _parser.Parse("category:Electronics laptop");

        _evaluator.Matches(product, query).Should().BeTrue();
    }

    #endregion

    #region Edge Cases

    [Fact]
    public void Matches_EmptyQuery_ReturnsTrue()
    {
        var product = new Product("Laptop", 999.99m, "Electronics", true, DateTimeOffset.Now);
        var query = ParsedQuery.Empty;

        _evaluator.Matches(product, query).Should().BeTrue();
    }

    [Fact]
    public void Matches_NonExistentProperty_ReturnsFalse()
    {
        var product = new Product("Laptop", 999.99m, "Electronics", true, DateTimeOffset.Now);
        var query = _parser.Parse("nonexistent:value");

        _evaluator.Matches(product, query).Should().BeFalse();
    }

    [Fact]
    public void Matches_NullPropertyValue_HandledGracefully()
    {
        var product = new Product(null!, 999.99m, "Electronics", true, DateTimeOffset.Now);
        var query = _parser.Parse("name:Laptop");

        _evaluator.Matches(product, query).Should().BeFalse();
    }

    #endregion
}
