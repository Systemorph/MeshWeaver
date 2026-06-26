namespace MeshWeaver.Layout;

/// <summary>
/// A control that provides a search input with autocomplete support.
/// Rendered as FluentAutocomplete in Blazor for node search functionality.
/// </summary>
public record SearchBoxControl()
    : UiControl<SearchBoxControl>(ModuleSetup.ModuleName, ModuleSetup.ApiVersion)
{
    /// <summary>
    /// The current search value.
    /// </summary>
    public object? Value { get; init; }

    /// <summary>
    /// Placeholder text when the search box is empty.
    /// </summary>
    public object? Placeholder { get; init; }

    /// <summary>
    /// The namespace to scope the search to.
    /// </summary>
    public object? Namespace { get; init; }

    /// <summary>
    /// Whether to navigate on selection or stay on the current page.
    /// When false, selecting an item updates the URL with ?q= parameter.
    /// </summary>
    public object? NavigateOnSelect { get; init; }

    /// <summary>
    /// Maximum number of autocomplete suggestions to show.
    /// </summary>
    public object? MaxSuggestions { get; init; }

    /// <summary>Returns a copy with <paramref name="value"/> as the pre-filled search text.</summary>
    /// <param name="value">The search string to pre-fill.</param>
    /// <returns>A new <see cref="SearchBoxControl"/> with the updated value.</returns>
    public SearchBoxControl WithValue(string value) => this with { Value = value };
    /// <summary>Returns a copy with <paramref name="placeholder"/> as the empty-state hint text.</summary>
    /// <param name="placeholder">The placeholder text to display.</param>
    /// <returns>A new <see cref="SearchBoxControl"/> with the updated placeholder.</returns>
    public SearchBoxControl WithPlaceholder(string placeholder) => this with { Placeholder = placeholder };
    /// <summary>Returns a copy with <paramref name="ns"/> as the search scope namespace.</summary>
    /// <param name="ns">The namespace path to restrict search results to.</param>
    /// <returns>A new <see cref="SearchBoxControl"/> with the updated namespace.</returns>
    public SearchBoxControl WithNamespace(string ns) => this with { Namespace = ns };
    /// <summary>Returns a copy with <paramref name="navigate"/> controlling whether selecting a result navigates.</summary>
    /// <param name="navigate">True to navigate on selection; false to update the query parameter only.</param>
    /// <returns>A new <see cref="SearchBoxControl"/> with the updated navigate-on-select flag.</returns>
    public SearchBoxControl WithNavigateOnSelect(bool navigate) => this with { NavigateOnSelect = navigate };
    /// <summary>Returns a copy with <paramref name="max"/> as the maximum number of autocomplete suggestions shown.</summary>
    /// <param name="max">The maximum suggestion count.</param>
    /// <returns>A new <see cref="SearchBoxControl"/> with the updated suggestion limit.</returns>
    public SearchBoxControl WithMaxSuggestions(int max) => this with { MaxSuggestions = max };
}
