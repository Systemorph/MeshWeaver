using System.Text.Json;
using MeshWeaver.Layout;

namespace MeshWeaver.Maui.Abstractions;

/// <summary>
/// Coerces a list control's <c>Options</c> into (display text, value-string) pairs the native
/// picker / list / radio group can show + select. Handles BOTH a typed <see cref="Option"/> list
/// AND — the case the naive <c>as IEnumerable&lt;Option&gt;</c> missed — a <see cref="JsonElement"/>
/// array (Options arrive serialized after the layout-stream round-trip, so the typed cast is null →
/// no options rendered). The value is the option item's string form, used for selection-match and
/// write-back; it covers the common string/enum-valued option.
/// </summary>
public static class MauiOptionCoercion
{
    /// <summary>Returns the (Text, Value) pairs for <paramref name="options"/>, or an empty list.</summary>
    public static List<(string Text, string? Value)> Coerce(object? options)
    {
        var result = new List<(string Text, string? Value)>();
        switch (options)
        {
            case IEnumerable<Option> typed:
                foreach (var o in typed) result.Add((o.Text, o.GetItem()?.ToString()));
                break;
            case JsonElement { ValueKind: JsonValueKind.Array } arr:
                foreach (var el in arr.EnumerateArray())
                {
                    var text = el.TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.String
                        ? t.GetString() ?? ""
                        : el.ToString();
                    var val = el.TryGetProperty("item", out var it) ? JsonScalar(it)
                        : el.TryGetProperty("itemString", out var its) ? its.GetString()
                        : text;
                    result.Add((text, val));
                }
                break;
        }
        return result;
    }

    private static string? JsonScalar(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.String => e.GetString(),
        JsonValueKind.Null => null,
        _ => e.GetRawText()
    };
}
