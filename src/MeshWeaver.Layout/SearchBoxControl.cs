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

    public SearchBoxControl WithValue(string value) => this with { Value = value };
    public SearchBoxControl WithPlaceholder(string placeholder) => this with { Placeholder = placeholder };
    public SearchBoxControl WithNamespace(string ns) => this with { Namespace = ns };
    public SearchBoxControl WithNavigateOnSelect(bool navigate) => this with { NavigateOnSelect = navigate };
    public SearchBoxControl WithMaxSuggestions(int max) => this with { MaxSuggestions = max };
}
