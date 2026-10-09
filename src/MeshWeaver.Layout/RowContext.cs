using System.Text.Json;
using System.Text.Json.Nodes;

namespace MeshWeaver.Layout;

/// <summary>
/// The ROW a user action came from, when the control that raised it is declared inside a bound
/// row template: a <c>BindMany</c> list (<see cref="ItemTemplateControl"/>) or a data grid's
/// <see cref="DataGrid.TemplateColumnControl"/>. The template is declared ONCE and rendered once
/// per row by the client, so the control's area names the TEMPLATE, never the row; this record
/// is what tells a row-scoped action which row was clicked (Doc/GUI/DataBinding → "Row-scoped
/// actions").
///
/// <para>🚨 It is the row AS THE CLIENT RENDERED IT — the value the person saw when they clicked —
/// not a position the owner re-resolves. A list that changed between the render and the click
/// (a row added above, a row removed) therefore still acts on the row that was clicked: an index
/// re-read at click time would hand the action whatever row has since moved into that slot.
/// <see cref="Index"/> and <see cref="Pointer"/> are carried for diagnostics and for binding
/// relative to the row; the identity is <see cref="Value"/> (and <see cref="Path"/> for a node
/// row).</para>
///
/// <para>🚨 It is USER INPUT, exactly like any other click payload. An action authorizes the
/// write it makes — which it does by making it as the clicking user — and never treats a field of
/// the row as proof that the user may act on it.</para>
/// </summary>
public record RowContext
{
    /// <summary>
    /// The row's data context — the JSON pointer the client bound the row's template against
    /// (e.g. <c>/data/"rows"/3</c>). Null where the row has no pointer of its own (a data grid's
    /// rows are sorted and paged on the client).
    /// </summary>
    public string? Pointer { get; init; }

    /// <summary>The row's position in the rendered collection, when it has one. Diagnostic only —
    /// never re-resolve the row by it.</summary>
    public int? Index { get; init; }

    /// <summary>
    /// The row's value as the client rendered it. Arrives as JSON (<see cref="JsonElement"/> or
    /// <see cref="JsonNode"/>) — read it with <c>ctx.RowAs&lt;T&gt;()</c>, never with a cast.
    /// </summary>
    public object? Value { get; init; }

    /// <summary>
    /// The mesh path of the row, when the row is a node. A client may set it explicitly; when it
    /// does not, <see cref="NodePath"/> reads the row value's own <c>path</c> property.
    /// </summary>
    public string? Path { get; init; }

    /// <summary>
    /// The row's stable identity within its collection, when the client knows one — a data grid sets
    /// it from <see cref="DataGrid.DataGridControl.RowKey"/>. Keys the row's click state
    /// (<see cref="ClickProgress.RowArea"/>), so one row's busy state and Cancel never land on another row.
    /// </summary>
    public string? Key { get; init; }

    /// <summary>
    /// The row's node path: <see cref="Path"/> when set, otherwise the string <c>path</c> property of
    /// <see cref="Value"/> (a serialized <c>MeshNode</c>, or any row record that carries one);
    /// null when the row is not a node.
    /// </summary>
    /// <returns>The node path, or null.</returns>
    public string? NodePath()
    {
        if (!string.IsNullOrEmpty(Path))
            return Path;
        return Value switch
        {
            JsonElement { ValueKind: JsonValueKind.Object } element => ReadPath(element),
            JsonObject node => ReadPath(node),
            null => null,
            var typed => typed.GetType().GetProperty("Path")?.GetValue(typed) as string,
        };
    }

    private static string? ReadPath(JsonElement element)
    {
        foreach (var property in element.EnumerateObject())
            if (string.Equals(property.Name, "path", StringComparison.OrdinalIgnoreCase)
                && property.Value.ValueKind == JsonValueKind.String)
                return property.Value.GetString();
        return null;
    }

    private static string? ReadPath(JsonObject node)
    {
        foreach (var (name, value) in node)
            if (string.Equals(name, "path", StringComparison.OrdinalIgnoreCase)
                && value is JsonValue v && v.TryGetValue<string>(out var path))
                return path;
        return null;
    }
}
