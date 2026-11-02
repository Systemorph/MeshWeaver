using System;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using Xunit;

namespace MeshWeaver.Layout.Test;

/// <summary>
/// The <see cref="MeshSearchRenderMode.Table"/> contract: a header click is a STORE sort — the view
/// appends <see cref="MeshSearchColumnSort.Term"/> to the query it already sends — and a cell is the
/// column selector's value, formatted the same on every machine. Both are pure functions, pinned here
/// against the production query parser so "the header's order wins" is what the parser reads, not
/// what the string looks like.
/// </summary>
public class MeshSearchTableTest
{
    private static readonly MeshWeaver.Mesh.QueryParser Parser = new();

    [Fact]
    public void Table_IsAppendedLast_SoEveryOlderOrdinalStays()
    {
        var names = Enum.GetNames<MeshSearchRenderMode>();
        Assert.Equal("Table", names[^1]);
        Assert.Equal(7, (int)MeshSearchRenderMode.Table);
        Assert.Equal(6, (int)MeshSearchRenderMode.Icons);
    }

    [Fact]
    public void AFirstClickSortsAscending_ASecondFlips_AThirdFlipsBack()
    {
        var first = MeshSearchColumnSort.Toggle(null, "content.receivedOn");
        Assert.Equal(new MeshSearchColumnSort("content.receivedOn"), first);
        Assert.Equal("sort:content.receivedOn", first.Term);

        var second = MeshSearchColumnSort.Toggle(first, "content.receivedOn");
        Assert.True(second.Descending);
        Assert.Equal("sort:content.receivedOn-desc", second.Term);

        Assert.False(MeshSearchColumnSort.Toggle(second, "content.receivedOn").Descending);
    }

    [Fact]
    public void ClickingAnotherColumn_StartsThatColumnAscending()
    {
        var byDate = new MeshSearchColumnSort("content.receivedOn", Descending: true);
        Assert.Equal(new MeshSearchColumnSort("name"), MeshSearchColumnSort.Toggle(byDate, "name"));
    }

    [Fact]
    public void NoHeaderSort_LeavesTheQueryUntouched()
    {
        const string query = "nodeType:Submission namespace:Acme sort:content.lastActivityAt-desc";
        Assert.Same(query, MeshSearchColumnSort.Apply(query, null));
    }

    /// <summary>
    /// The hidden query (a scope's or a Sort-by option's) carries its own <c>sort:</c>, and the reader
    /// may have typed another. The header's term goes LAST, and the parser keeps the last <c>sort:</c>
    /// — so the header orders, and every filter of the query is still there.
    /// </summary>
    [Theory]
    [InlineData("nodeType:Submission namespace:Acme sort:content.lastActivityAt-desc")]
    [InlineData("nodeType:Submission namespace:Acme sort:content.lastActivityAt-desc fire sort:name")]
    [InlineData("nodeType:Submission namespace:Acme")]
    public void TheHeaderSortWins_OverEverySortEarlierInTheQuery(string query)
    {
        var sorted = MeshSearchColumnSort.Apply(query, new MeshSearchColumnSort("content.receivedOn", Descending: true));

        Assert.Equal($"{query} sort:content.receivedOn-desc", sorted);
        var parsed = Parser.Parse(sorted);
        Assert.Equal("content.receivedOn", parsed.OrderBy?.Property);
        Assert.True(parsed.OrderBy?.Descending);
        Assert.Equal(Parser.Parse(query).Filter?.ToString(), parsed.Filter?.ToString());
    }

    /// <summary>A union (one sub-query per line) is ordered by its FIRST line, so every line carries the
    /// header's order — the same rule <see cref="CatalogSort.Sorted"/> follows.</summary>
    [Fact]
    public void AUnion_CarriesTheHeaderSortOnEveryLine()
    {
        const string query = "nodeType:Submission namespace:Acme\nnodeType:Submission namespace:Globex sort:name";

        var lines = MeshSearchColumnSort.Apply(query, new MeshSearchColumnSort("content.receivedOn")).Split('\n');

        Assert.Equal(2, lines.Length);
        Assert.All(lines, line => Assert.Equal("content.receivedOn", Parser.Parse(line).OrderBy?.Property));
        Assert.All(lines, line => Assert.False(Parser.Parse(line).OrderBy?.Descending));
    }

    [Fact]
    public void WithColumns_DeclaresTheColumnsInOrder_AndAScopeCanCarryItsOwn()
    {
        var control = Controls.MeshSearch
            .WithRenderMode(MeshSearchRenderMode.Table)
            .WithColumns(
                new MeshSearchColumn("Name", "name"),
                new MeshSearchColumn("Received", "content.receivedOn"),
                new MeshSearchColumn("Premium", "content.premium") { Format = "N0", Sortable = false });

        Assert.Equal(["name", "content.receivedOn", "content.premium"], control.Columns!.Select(c => c.Field));
        Assert.True(control.Columns![0].Sortable);
        Assert.False(control.Columns![2].Sortable);

        var scope = new MeshSearchScopeTab("Pipeline", "nodeType:Submission") { Columns = [new MeshSearchColumn("Name", "name")] };
        Assert.Single(scope.Columns!);
    }

    [Theory]
    [InlineData("2026-10-01", "2026-10-01")]
    [InlineData("2026-10-01T23:30:00+02:00", "2026-10-01")] // the value's own date — no zone conversion
    [InlineData("2026-10-01T09:30:00Z", "2026-10-01")]
    [InlineData("1/2", "1/2")]                                // not ISO ⇒ not a date
    [InlineData("Acme Re", "Acme Re")]
    [InlineData("", "")]
    public void AStringCell_IsAnIsoDateOnlyWhenItLooksLikeOne(string value, string expected) =>
        Assert.Equal(expected, MeshSearchColumn.FormatValue(value));

    [Fact]
    public void Cells_AreFormattedInvariantly_WhateverTheCurrentCulture()
    {
        var saved = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-CH");
            Assert.Equal("2026-10-01", MeshSearchColumn.FormatValue(new DateTime(2026, 10, 1, 14, 0, 0)));
            Assert.Equal("2026-10-01", MeshSearchColumn.FormatValue(new DateOnly(2026, 10, 1)));
            Assert.Equal("2026-10-01 14:00", MeshSearchColumn.FormatValue(new DateTimeOffset(2026, 10, 1, 14, 0, 0, TimeSpan.Zero), "yyyy-MM-dd HH:mm"));
            Assert.Equal("1,234,567", MeshSearchColumn.FormatValue(1234567m, "N0"));
            Assert.Equal("0.5", MeshSearchColumn.FormatValue(0.5));
        }
        finally
        {
            CultureInfo.CurrentCulture = saved;
        }
    }

    [Fact]
    public void EmptyCellsStayEmpty_AndOtherShapesReadPlainly()
    {
        Assert.Equal("", MeshSearchColumn.FormatValue(null));
        Assert.Equal("", MeshSearchColumn.FormatValue(JsonDocument.Parse("null").RootElement));
        Assert.Equal(MeshSearchColumn.TrueText, MeshSearchColumn.FormatValue(true));
        Assert.Equal(MeshSearchColumn.FalseText, MeshSearchColumn.FormatValue(false));
        Assert.Equal("Bound", MeshSearchColumn.FormatValue(DayStatus.Bound));
        Assert.Equal("Fire, Marine", MeshSearchColumn.FormatValue(new[] { "Fire", null, "Marine" }));
        Assert.Equal("2026-10-01", MeshSearchColumn.FormatValue(JsonDocument.Parse("\"2026-10-01T08:00:00Z\"").RootElement));
        Assert.Equal("42", MeshSearchColumn.FormatValue(JsonDocument.Parse("42").RootElement));
    }

    /// <summary>The parser reads the text after the LAST hyphen as the direction, so a hyphenated
    /// selector must sort by ITSELF in both directions — never by its prefix on the ascending click.</summary>
    [Fact]
    public void AHyphenatedSelector_SortsByItself_InBothDirections()
    {
        var ascending = MeshSearchColumnSort.Toggle(null, "content.policy-id");
        var descending = MeshSearchColumnSort.Toggle(ascending, "content.policy-id");

        foreach (var (sort, desc) in new[] { (ascending, false), (descending, true) })
        {
            var parsed = Parser.Parse(MeshSearchColumnSort.Apply("nodeType:Submission", sort)).OrderBy;
            Assert.Equal("content.policy-id", parsed?.Property);
            Assert.Equal(desc, parsed?.Descending);
        }
    }

    [Fact]
    public void AJsonNumberBeyondDecimal_StillRenders() =>
        Assert.Equal("1E+100", MeshSearchColumn.FormatValue(JsonDocument.Parse("1e100").RootElement));

    [Fact]
    public void WithColumns_SnapshotsTheCallersArray()
    {
        var columns = new[] { new MeshSearchColumn("Name", "name") };
        var control = Controls.MeshSearch.WithColumns(columns);

        columns[0] = new MeshSearchColumn("Other", "other");

        Assert.Equal("name", control.Columns![0].Field);
    }

    [Fact]
    public void AFormatTheValueCannotTake_RendersTheValue_NotAnError() =>
        Assert.Equal("2026-10-01", MeshSearchColumn.FormatValue(new DateOnly(2026, 10, 1), "Q"));

    private enum DayStatus { Bound }
}
