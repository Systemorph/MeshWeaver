using MeshWeaver.Fixture;
using MeshWeaver.Mesh;
using Xunit;

namespace MeshWeaver.Hosting.Test;

/// <summary>
/// Systemorph/MeshWeaver#7918 — the portal search bar's suggestion query for the typed text
/// <c>Ann's b</c> was <c>*Ann's b* scope:descendants context:search is:main partitions:all limit:50 …</c>:
/// DECLARED mesh-wide, yet the Postgres planner logged it as <c>[FanOut] UNANCHORED</c> with
/// <c>scope:Exact</c>. The apostrophe opened a quoted phrase that never closed, ran to the end of the
/// input and swallowed every qualifier after it — the declaration, the scope, the context, the limit
/// and the exclusions all became search text. These tests parse that exact query.
/// </summary>
public class UnbalancedQuoteKeepsTheDeclarationTest
{
    private static ParsedQuery Parse(string query) => new QueryParser().Parse(query);

    /// <summary>The query the incident logged, the typed text spliced in by the suggestion caller.</summary>
    private const string Incident =
        "*Ann's b* scope:descendants context:search is:main partitions:all limit:50 "
        + "-namespace:*_MailCopy -namespace:*/_Mailbox -namespace:TeamsChat_*";

    [Fact]
    public void TheIncidentQuery_KeepsItsDeclarationScopeAndFilters()
    {
        var parsed = Parse(Incident);

        parsed.CrossPartition.Should().BeTrue("the query declares partitions:all after the typed text");
        parsed.IsSufficientlySpecified().Should().BeTrue("a declared fan-out is never UNANCHORED");
        parsed.Scope.Should().Be(QueryScope.Descendants);
        parsed.Context.Should().Be("search");
        parsed.IsMain.Should().BeTrue();
        parsed.Limit.Should().Be(50);
        parsed.Filter.Should().NotBeNull("the namespace exclusions are filters, not search text");
        parsed.TextSearch.Should().Be("Ann's b", "the apostrophe is part of the word the person typed");
    }

    /// <summary>A quote that never closes is data wherever it sits — the terms after it still parse.</summary>
    [Theory]
    [InlineData("Ann' partitions:all scope:descendants")]
    [InlineData("'Ann partitions:all scope:descendants")]
    [InlineData("\"Ann partitions:all scope:descendants")]
    [InlineData("-'Ann partitions:all scope:descendants")]
    public void AnUnclosedQuote_DoesNotSwallowTheRestOfTheQuery(string query)
    {
        var parsed = Parse(query);

        parsed.CrossPartition.Should().BeTrue($"'{query}': the declaration follows the unclosed quote");
        parsed.Scope.Should().Be(QueryScope.Descendants, $"'{query}'");
        parsed.TextSearch.Should().Be("Ann", $"'{query}': the stray quote is not search text");
    }

    /// <summary>
    /// 🚨 NEGATIVE CONTROL: a BALANCED quote still makes a phrase, and a phrase that wraps the
    /// declaration hides it — so the assertions above can fail, and the fix did not turn quoting off.
    /// </summary>
    [Fact]
    public void ABalancedQuote_StillMakesAPhrase_AndCanHideTheDeclaration()
    {
        var hidden = Parse("Ann 'partitions:all scope:descendants'");
        hidden.CrossPartition.Should().BeFalse("a declaration inside a closed quote is text, by design");
        hidden.IsSufficientlySpecified().Should().BeFalse("nothing outside the phrase says where to look");
        hidden.TextSearch.Should().Be("Ann partitions:all scope:descendants");

        var phrase = Parse("\"Ann s b\" partitions:all");
        phrase.CrossPartition.Should().BeTrue();
        phrase.TextSearch.Should().Be("Ann s b", "a closed double-quoted phrase is read whole");
    }

    /// <summary>A quote at the start of a term still opens a phrase when it closes, apostrophes inside included.</summary>
    [Fact]
    public void AQuotedPhraseMayCarryAnApostrophe()
    {
        var parsed = Parse("\"Ann's budget\" partitions:all");

        parsed.TextSearch.Should().Be("Ann's budget");
        parsed.CrossPartition.Should().BeTrue();
    }
}
