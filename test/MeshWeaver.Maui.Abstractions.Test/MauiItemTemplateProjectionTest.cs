using System.Collections.Immutable;
using System.Text.Json;
using MeshWeaver.Maui.Abstractions;
using Xunit;

namespace MeshWeaver.Maui.Abstractions.Test;

/// <summary>
/// The item-template projection the native ItemTemplateView delegates to: the per-item DataContext path (must
/// match the Blazor ItemTemplate's <c>GetViewWithPath(i)</c> EXACTLY, or items bind to the wrong pointer) and
/// the item-count from a bound collection value (JSON array / collection / enumerable).
/// </summary>
public class MauiItemTemplateProjectionTest
{
    [Fact]
    public void ItemPath_CombinesDataContextDataAndIndex()
        => MauiItemTemplateProjection.ItemPath("/ctx", "/items", 2).Should().Be("/ctx/items/2");

    [Fact]
    public void ItemPath_NullDataContext_OmitsIt()
        => MauiItemTemplateProjection.ItemPath(null, "/items", 0).Should().Be("/items/0");

    [Fact]
    public void Count_JsonArray_ReturnsLength()
    {
        var arr = JsonDocument.Parse("""[1,2,3]""").RootElement;
        MauiItemTemplateProjection.Count(arr).Should().Be(3);
    }

    [Fact]
    public void Count_JsonNonArray_ReturnsZero()
    {
        var str = JsonDocument.Parse("\"hello\"").RootElement;
        MauiItemTemplateProjection.Count(str).Should().Be(0);
    }

    [Fact]
    public void Count_Collection_ReturnsCount()
        => MauiItemTemplateProjection.Count(ImmutableList.Create("a", "b")).Should().Be(2);

    [Fact]
    public void Count_Enumerable_ReturnsCount()
        => MauiItemTemplateProjection.Count(Enumerable.Range(0, 5)).Should().Be(5);

    [Fact]
    public void Count_RawString_ReturnsZero_NotCharCount()
        => MauiItemTemplateProjection.Count("abc").Should().Be(0); // string is IEnumerable<char> — must NOT count chars

    [Fact]
    public void Count_NullOrScalar_ReturnsZero()
    {
        MauiItemTemplateProjection.Count(null).Should().Be(0);
        MauiItemTemplateProjection.Count(42).Should().Be(0);
    }
}
