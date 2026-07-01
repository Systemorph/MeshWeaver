using System.Collections;
using System.Text.Json;

namespace MeshWeaver.Maui.Abstractions;

/// <summary>
/// Pure projection logic behind the native item-template view. Kept MAUI-free so it is unit-testable from a
/// plain net10.0 project — the native <c>ItemTemplateView</c> only builds the UI (a StackLayout of rendered
/// item controls) and delegates the index-count and per-item DataContext-path computation here.
/// </summary>
public static class MauiItemTemplateProjection
{
    /// <summary>
    /// The per-item DataContext path: <c>{dataContext}{data}/{index}</c> — IDENTICAL to the Blazor
    /// ItemTemplate's <c>GetViewWithPath(i)</c>, so the shared renderer resolves each item against the same
    /// pointer the web view does. <paramref name="data"/> is the control's raw <c>Data</c> property (a JSON
    /// pointer reference whose string form is the collection path).
    /// </summary>
    public static string ItemPath(string? dataContext, object? data, int index) =>
        $"{dataContext}{data}/{index}";

    /// <summary>
    /// Item count from a bound collection value: a JSON array's length, an <see cref="ICollection"/>'s Count,
    /// or a general <see cref="IEnumerable"/>'s element count. Anything else (null, scalar) → 0.
    /// </summary>
    public static int Count(object? value) => value switch
    {
        JsonElement { ValueKind: JsonValueKind.Array } je => je.GetArrayLength(),
        string => 0,   // a string is IEnumerable<char> — never an item collection; guard before IEnumerable
        ICollection c => c.Count,
        IEnumerable e => e.Cast<object>().Count(),
        _ => 0,
    };
}
