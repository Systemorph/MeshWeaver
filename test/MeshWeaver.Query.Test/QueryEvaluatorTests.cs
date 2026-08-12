using System;
using System.Text.Json;
using MeshWeaver.Hosting.Persistence.Query;
using MeshWeaver.Mesh;
using Xunit;

namespace MeshWeaver.Query.Test;

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

    #region Wildcard Namespace — the ONE vocabulary (#1235)

    /// <summary>A node-shaped row, so a <c>namespace:</c> filter has something to bind to.</summary>
    private record Row(string Name, string Namespace);

    /// <summary>
    /// 🚨 THE REGRESSION FOR #1235. A wildcard NAMESPACE filter was the one place the parser
    /// rewrote the user's <c>*</c> into SQL's <c>%</c>. This evaluator speaks <c>*</c>, so
    /// <c>%/Source</c> matched neither the leading- nor the trailing-star branch of the old
    /// hand-rolled comparison and fell through to an EQUALITY test against the literal string
    /// <c>"%/Source"</c> — which no namespace can equal. The filter therefore matched NOTHING in
    /// memory, silently: no error, no warning, an empty result.
    ///
    /// <para>The asymmetry is what made it survive review: <c>name:*Laptop*</c> (asserted above)
    /// works, because only the namespace branch did the rewrite.</para>
    /// </summary>
    [Theory]
    [InlineData("acme/SampleData/Source", true)]
    [InlineData("acme/Source", true)]
    [InlineData("acme/SampleData/Source/Fixtures", false)] // nested — needs scope:subtree, below
    [InlineData("acme/SampleData/Other", false)]
    public void Matches_WildcardNamespace_WithoutScope_MatchesThatLevelOnly(string ns, bool expected)
    {
        var query = _parser.Parse("namespace:*/Source");

        _evaluator.Matches(new Row("x", ns), query).Should().Be(expected);
    }

    /// <summary>
    /// The MULTI-wildcard half of #1235. <c>scope:subtree</c> widens the filter to
    /// <c>(*/Source OR */Source/*)</c>, and that second pattern carries TWO wildcards. The old
    /// matcher split on the FIRST one only and took everything after it as a literal suffix, so
    /// <c>*/Source/*</c> degenerated to <c>EndsWith("/Source/*")</c> — which nothing ends with.
    /// Fixing only the <c>%</c>→<c>*</c> spelling would leave this arm still matching nothing,
    /// which is why the vocabulary and the glob had to be fixed together.
    /// </summary>
    [Theory]
    [InlineData("acme/SampleData/Source", true)]
    [InlineData("acme/SampleData/Source/Fixtures", true)]
    [InlineData("acme/SampleData/Source/Fixtures/Deep/Deeper", true)]
    [InlineData("acme/SampleDataSource", false)]      // no '/' boundary — the literal must match
    [InlineData("acme/SampleData/Sources", false)]    // '/Source' is not a prefix match
    [InlineData("acme/SampleData/Other", false)]
    public void Matches_WildcardNamespace_WithSubtreeScope_ReachesNestedNamespaces(string ns, bool expected)
    {
        var query = _parser.Parse("namespace:*/Source scope:subtree");

        _evaluator.Matches(new Row("x", ns), query).Should().Be(expected);
    }

    /// <summary>
    /// A pattern may carry wildcards anywhere and in any number — the glob is real, not a
    /// four-case approximation off <c>Trim('*')</c> (which cannot see an INTERIOR wildcard at all:
    /// <c>*a*b*</c> trimmed to <c>a*b</c> was compared as a literal containing a star).
    /// </summary>
    [Theory]
    [InlineData("a*c", "abc", true)]
    [InlineData("a*c", "ac", true)]                    // '*' matches the empty run
    [InlineData("a*b*c", "a-b-c", true)]
    [InlineData("a*b*c", "a-c-b", false)]              // interior literals must appear IN ORDER
    [InlineData("*/x/*/y", "p/x/q/y", true)]
    [InlineData("*/x/*/y", "p/x/y", false)]            // the second literal needs its own room
    [InlineData("a*a", "a", false)]                    // suffix may not reuse the prefix's chars
    [InlineData("**b", "ab", true)]                    // a redundant second wildcard adds nothing
    [InlineData("%/Source", "acme/Source", false)]     // '%' is a LITERAL here — see below
    public void Matches_Glob_HandlesAnyNumberOfWildcards(string pattern, string value, bool expected)
    {
        QueryWildcard.IsMatch(value, pattern).Should().Be(expected);
    }

    /// <summary>
    /// 🚨 The matcher is deliberately INTOLERANT of <c>%</c>. Accepting both spellings is what let
    /// the two vocabularies drift apart unnoticed in the first place — a <c>%</c> leaking back into
    /// the AST would keep working in memory while meaning something different in SQL. With <c>*</c>
    /// as the only wildcard, any re-introduction fails loudly here instead of silently in prod.
    /// </summary>
    [Fact]
    public void Matches_PercentIsALiteral_NotAWildcard()
    {
        _evaluator.Matches(new Row("x", "acme/Source"), _parser.Parse("namespace:*/Source"))
            .Should().BeTrue("the parser emits the `*` form");
        QueryWildcard.IsMatch("acme/Source", "%/Source")
            .Should().BeFalse("`%` is SQL dialect — it exists only inside a SQL generator's parameter");
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
