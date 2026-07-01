using System.Text.Json;
using MeshWeaver.Layout;
using MeshWeaver.Maui.Abstractions;
using Xunit;

namespace MeshWeaver.Maui.Abstractions.Test;

/// <summary>
/// Coercion of a list control's <c>Options</c> into (text, value) pairs — the logic Select/Listbox/
/// Combobox/RadioGroup use. The regression this guards: after the layout-stream round-trip, Options
/// arrive as a <see cref="JsonElement"/> array (not a typed <see cref="Option"/> list), so the naive
/// <c>as IEnumerable&lt;Option&gt;</c> returned null and pickers rendered empty.
/// </summary>
public class MauiOptionCoercionTest
{
    [Fact]
    public void TypedOptionList_YieldsTextAndItemValue()
    {
        var options = new List<Option> { new Option<string>("a", "Apple"), new Option<string>("b", "Banana") };

        var result = MauiOptionCoercion.Coerce(options);

        result.Should().HaveCount(2);
        result[0].Should().Be(("Apple", "a"));
        result[1].Should().Be(("Banana", "b"));
    }

    [Fact]
    public void JsonElementArray_TheRoundTripCase_YieldsTextAndItem()
    {
        // The exact shape Options take after the stream round-trip (what broke the typed cast).
        var json = JsonDocument.Parse("""[{"text":"Apple","item":"a"},{"text":"Banana","item":"b"}]""").RootElement;

        var result = MauiOptionCoercion.Coerce(json);

        result.Should().HaveCount(2);
        result[0].Should().Be(("Apple", "a"));
        result[1].Should().Be(("Banana", "b"));
    }

    [Fact]
    public void JsonElementArray_FallsBackToItemStringThenText()
    {
        var json = JsonDocument.Parse("""[{"text":"Only text"},{"text":"Has itemString","itemString":"x"}]""").RootElement;

        var result = MauiOptionCoercion.Coerce(json);

        result[0].Should().Be(("Only text", "Only text")); // no item/itemString → value defaults to text
        result[1].Should().Be(("Has itemString", "x"));
    }

    [Fact]
    public void JsonElementArray_NonStringItem_UsesRawScalar()
    {
        var json = JsonDocument.Parse("""[{"text":"One","item":1}]""").RootElement;

        var result = MauiOptionCoercion.Coerce(json);

        result[0].Should().Be(("One", "1"));
    }

    [Fact]
    public void Null_YieldsEmpty() => MauiOptionCoercion.Coerce(null).Should().BeEmpty();

    [Fact]
    public void UnrelatedObject_YieldsEmpty() => MauiOptionCoercion.Coerce("not options").Should().BeEmpty();
}
