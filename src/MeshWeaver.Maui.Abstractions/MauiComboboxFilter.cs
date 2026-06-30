namespace MeshWeaver.Maui.Abstractions;

/// <summary>
/// Pure filtering + value-resolution logic behind the native combobox (the editable-combobox parity with the
/// Blazor view). Kept MAUI-free so it is unit-testable from a plain net10.0 project — the native
/// <c>ComboboxView</c> only builds the UI (Entry + tappable suggestion labels) and delegates every decision
/// here. Options are the <c>(Text, Value)</c> pairs produced by <see cref="MauiOptionCoercion"/>.
/// </summary>
public static class MauiComboboxFilter
{
    /// <summary>
    /// Filters the options whose display <c>Text</c> contains <paramref name="query"/> (case-insensitive),
    /// capped at <paramref name="max"/>. Returns the matches and whether the suggestion list should show:
    /// hidden when there are no matches, or when the sole match equals the query exactly (nothing left to
    /// pick). An empty/whitespace query returns the first <paramref name="max"/> options.
    /// </summary>
    public static (IReadOnlyList<(string Text, string? Value)> Matches, bool ShowList) Filter(
        IReadOnlyList<(string Text, string? Value)> options, string? query, int max = 8)
    {
        var q = query?.Trim() ?? "";
        var matches = (string.IsNullOrEmpty(q)
                ? options
                : options.Where(o => o.Text.Contains(q, StringComparison.OrdinalIgnoreCase)))
            .Take(max).ToList();
        var showList = matches.Count > 0 &&
            !(matches.Count == 1 && string.Equals(matches[0].Text, q, StringComparison.OrdinalIgnoreCase));
        return (matches, showList);
    }

    /// <summary>
    /// The value to write back for free-typed text (editable-combobox semantics): the matching option's
    /// <c>Value</c> when the text equals an option's display (case-insensitive), otherwise the raw trimmed
    /// text. Null/empty text writes <c>null</c>.
    /// </summary>
    public static string? ResolveFreeText(IReadOnlyList<(string Text, string? Value)> options, string? text)
    {
        var t = text?.Trim();
        if (string.IsNullOrEmpty(t)) return null;
        foreach (var o in options)
            if (string.Equals(o.Text, t, StringComparison.OrdinalIgnoreCase))
                return o.Value;
        return t;
    }
}
