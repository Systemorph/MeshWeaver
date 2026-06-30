using MeshWeaver.Maui.Abstractions;
using Xunit;

namespace MeshWeaver.Maui.Abstractions.Test;

/// <summary>
/// The editable-combobox filtering + free-text value resolution that the native ComboboxView delegates to.
/// Proves the deterministic logic (case-insensitive contains, max cap, show/hide of the suggestion list, and
/// the option-value-vs-raw-text write-back) so the view itself stays a thin UI shell.
/// </summary>
public class MauiComboboxFilterTest
{
    private static readonly List<(string Text, string? Value)> Options = new()
    {
        ("Apple", "a"), ("Banana", "b"), ("Apricot", "ap"), ("Cherry", "c"),
    };

    [Fact]
    public void EmptyQuery_ReturnsAll_AndShowsList()
    {
        var (matches, show) = MauiComboboxFilter.Filter(Options, "");

        matches.Should().HaveCount(4);
        show.Should().BeTrue();
    }

    [Fact]
    public void Query_FiltersByCaseInsensitiveContains()
    {
        var (matches, show) = MauiComboboxFilter.Filter(Options, "ap");

        matches.Select(m => m.Text).Should().Equal("Apple", "Apricot");   // option order preserved
        show.Should().BeTrue();
    }

    [Fact]
    public void SoleExactMatch_HidesList()
    {
        // Only "Cherry" contains "cherry"; it equals the query exactly → nothing left to pick.
        var (matches, show) = MauiComboboxFilter.Filter(Options, "cherry");

        matches.Should().ContainSingle().Which.Text.Should().Be("Cherry");
        show.Should().BeFalse();
    }

    [Fact]
    public void NoMatch_HidesList_AndIsEmpty()
    {
        var (matches, show) = MauiComboboxFilter.Filter(Options, "zzz");

        matches.Should().BeEmpty();
        show.Should().BeFalse();
    }

    [Fact]
    public void RespectsMaxCap()
    {
        var many = Enumerable.Range(0, 20).Select(i => ($"Item{i}", (string?)i.ToString())).ToList();

        var (matches, _) = MauiComboboxFilter.Filter(many, "Item", max: 8);

        matches.Should().HaveCount(8);
    }

    [Fact]
    public void ResolveFreeText_ExactDisplay_ReturnsOptionValue()
        => MauiComboboxFilter.ResolveFreeText(Options, "banana").Should().Be("b"); // case-insensitive

    [Fact]
    public void ResolveFreeText_NoMatch_ReturnsRawTrimmedText()
        => MauiComboboxFilter.ResolveFreeText(Options, "  custom  ").Should().Be("custom");

    [Fact]
    public void ResolveFreeText_EmptyOrNull_ReturnsNull()
    {
        MauiComboboxFilter.ResolveFreeText(Options, "").Should().BeNull();
        MauiComboboxFilter.ResolveFreeText(Options, "   ").Should().BeNull();
        MauiComboboxFilter.ResolveFreeText(Options, null).Should().BeNull();
    }
}
