using System.Collections.Immutable;
using System.Reactive.Linq;
using System.Text.Json.Nodes;
using MeshWeaver.Layout.Composition;

namespace MeshWeaver.Layout.DataGrid;

/// <summary>
/// The selected rows of a grid declared with <see cref="DataGridControl.WithRowSelection"/>, as the
/// client writes them into the data section: the KEYS of the selected rows, in selection order.
/// </summary>
public record DataGridSelectionState
{
    /// <summary>The keys (<see cref="DataGridControl.SelectionKey"/> values) of the selected rows.</summary>
    public ImmutableList<string> Keys { get; init; } = [];
}

/// <summary>What the header checkbox of a selection column shows.</summary>
public static class DataGridHeaderSelection
{
    /// <summary>No selectable row is selected (or there is none): unchecked; a click selects all.</summary>
    public const string None = "None";
    /// <summary>Some selectable rows are selected: indeterminate; a click clears the selection.</summary>
    public const string Some = "Some";
    /// <summary>Every selectable row is selected: checked; a click clears the selection.</summary>
    public const string All = "All";
}

/// <summary>
/// The selection rules of a grid with a selection column — ONE implementation the client views call
/// (Blazor, React) and the owner's bulk action re-applies, so "what select-all selects" and "which rows
/// may be acted on" cannot drift between them. Pure functions; no state.
/// </summary>
public static class DataGridRowSelection
{
    /// <summary>The <see cref="DataGridControl.SelectionMode"/> value of a multi-select grid.</summary>
    public const string Multiple = "Multiple";

    /// <summary>The key of <paramref name="row"/>, or null when it has none (such a row cannot be selected).</summary>
    /// <param name="row">The row as rendered.</param>
    /// <param name="keyProperty">The grid's <see cref="DataGridControl.SelectionKey"/>.</param>
    public static string? KeyOf(JsonObject row, string keyProperty)
        => row.TryGetPropertyValue(keyProperty, out var value) && value is JsonValue v
            ? v.ToString() is { Length: > 0 } key ? key : null
            : null;

    /// <summary>Why <paramref name="row"/> cannot be selected, or null when it can.</summary>
    /// <param name="row">The row as rendered.</param>
    /// <param name="disabledReasonProperty">The grid's <see cref="DataGridControl.SelectionDisabledReason"/>.</param>
    public static string? DisabledReasonOf(JsonObject row, string? disabledReasonProperty)
        => disabledReasonProperty is not null
           && row.TryGetPropertyValue(disabledReasonProperty, out var value)
           && value is JsonValue v && v.ToString() is { Length: > 0 } reason
            ? reason
            : null;

    /// <summary>The keys of every row of <paramref name="rows"/> that may be selected.</summary>
    public static ImmutableList<string> SelectableKeys(IEnumerable<JsonObject> rows, string keyProperty, string? disabledReasonProperty)
        => rows.Where(r => DisabledReasonOf(r, disabledReasonProperty) is null)
            .Select(r => KeyOf(r, keyProperty))
            .OfType<string>()
            .Distinct()
            .ToImmutableList();

    /// <summary>Select-all: every selectable row of <paramref name="rows"/>, in row order.</summary>
    public static ImmutableList<string> SelectAll(IEnumerable<JsonObject> rows, string keyProperty, string? disabledReasonProperty)
        => SelectableKeys(rows, keyProperty, disabledReasonProperty);

    /// <summary>
    /// Toggles one row. A non-selectable row (or one without a key) leaves the selection unchanged, so a
    /// stale click on a row that has become non-selectable can never add it.
    /// </summary>
    public static ImmutableList<string> Toggle(ImmutableList<string> selected, JsonObject row, bool on,
        string keyProperty, string? disabledReasonProperty)
    {
        if (KeyOf(row, keyProperty) is not { } key)
            return selected;
        if (!on)
            return selected.Remove(key);
        return DisabledReasonOf(row, disabledReasonProperty) is null && !selected.Contains(key)
            ? selected.Add(key)
            : selected;
    }

    /// <summary>The header checkbox state over the selectable rows of <paramref name="rows"/> (<see cref="DataGridHeaderSelection"/>).</summary>
    public static string HeaderState(IEnumerable<JsonObject> rows, IReadOnlyCollection<string> selected,
        string keyProperty, string? disabledReasonProperty)
    {
        var selectable = SelectableKeys(rows, keyProperty, disabledReasonProperty);
        var count = selectable.Count(selected.Contains);
        return count == 0 ? DataGridHeaderSelection.None
            : count == selectable.Count ? DataGridHeaderSelection.All
            : DataGridHeaderSelection.Some;
    }

    /// <summary>
    /// What the header checkbox does when clicked: from <see cref="DataGridHeaderSelection.None"/> it
    /// selects every selectable row; otherwise it clears the selection.
    /// </summary>
    public static ImmutableList<string> ToggleAll(IEnumerable<JsonObject> rows, IReadOnlyCollection<string> selected,
        string keyProperty, string? disabledReasonProperty)
    {
        var list = rows as IReadOnlyCollection<JsonObject> ?? rows.ToArray();
        return HeaderState(list, selected, keyProperty, disabledReasonProperty) == DataGridHeaderSelection.None
            ? SelectAll(list, keyProperty, disabledReasonProperty)
            : [];
    }

    /// <summary>
    /// The owner-side re-check a bulk action runs before acting: the selected keys that still name a row of
    /// <paramref name="rows"/> AND that row is still selectable. A selection is the viewer's claim about
    /// rows they saw — rows may have changed or become non-selectable since.
    /// </summary>
    /// <typeparam name="T">The row type the action works on.</typeparam>
    /// <param name="rows">The rows as the owner holds them now.</param>
    /// <param name="selected">The selected keys the client wrote.</param>
    /// <param name="keyOf">The row's key — the same value the grid's <see cref="DataGridControl.SelectionKey"/> names.</param>
    /// <param name="disabledReasonOf">Why the row is not selectable, or null; null delegate = every row is.</param>
    /// <returns>The rows to act on, in <paramref name="rows"/> order.</returns>
    public static ImmutableList<T> Prune<T>(IEnumerable<T> rows, IEnumerable<string> selected,
        Func<T, string?> keyOf, Func<T, string?>? disabledReasonOf = null)
    {
        var wanted = selected.ToImmutableHashSet();
        return rows.Where(r => keyOf(r) is { } k && wanted.Contains(k)
                               && string.IsNullOrEmpty(disabledReasonOf?.Invoke(r)))
            .ToImmutableList();
    }
}

/// <summary>Owner-side reading of a grid selection declared with <see cref="DataGridControl.WithRowSelection"/>.</summary>
public static class DataGridSelectionExtensions
{
    /// <summary>
    /// Seeds the selection data id with an empty selection — call it where the grid is RENDERED, so a
    /// bulk action's one-off read has a value even before the viewer ticks anything
    /// (<c>Doc/GUI/ButtonPendingState</c> → "The id the click reads must be SEEDED").
    /// </summary>
    /// <param name="host">The rendering host.</param>
    /// <param name="selectionDataId">The id passed to <see cref="DataGridControl.WithRowSelection"/>.</param>
    public static void SeedRowSelection(this LayoutAreaHost host, string selectionDataId)
        => host.UpdateData(selectionDataId, new DataGridSelectionState());

    /// <summary>
    /// The live selection of the grid bound to <paramref name="selectionDataId"/> — for a view that shows
    /// "3 selected" or enables its bulk button.
    /// </summary>
    public static IObservable<ImmutableList<string>> RowSelection(this LayoutAreaHost host, string selectionDataId)
        => host.GetDataStream<DataGridSelectionState>(selectionDataId).Select(s => s?.Keys ?? []);

    /// <summary>
    /// The selection as it stands at the click — a one-off read for a bulk action, completing after one
    /// value. RETURN it from <c>WithReactiveClickAction</c> (composed with the action), never subscribe it
    /// inside the handler. Requires <see cref="SeedRowSelection"/>.
    /// </summary>
    public static IObservable<ImmutableList<string>> SelectedRowKeys(this UiActionContext context, string selectionDataId)
        => context.Host.RowSelection(selectionDataId).Take(1);
}
