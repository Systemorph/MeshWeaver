namespace OpenSmc.Layout;

public record ComboboxControl(object Data) : ListControlBase<ComboboxControl>(Data), IListControl
{
    public bool Autofocus { get; init; }
    public ComboboxAutocomplete Autocomplete { get; init; }
    public string Placeholder { get; init; }
    public SelectPosition? Position { get; init; }
    public bool Disabled { get; init; }

    ComboboxControl WithAutofocus(bool autofocus) => this with {Autofocus = autofocus};
    ComboboxControl WithAutocomplete(ComboboxAutocomplete autocomplete) => this with {Autocomplete = autocomplete};
    ComboboxControl WithPlaceholder(string placeholder) => this with {Placeholder = placeholder};
    ComboboxControl WithPosition(SelectPosition position) => this with {Position = position};
    ComboboxControl WithDisabled(bool disabled) => this with {Disabled = disabled};
}

public enum ComboboxAutocomplete
{
    Inline,
    List,
    Both
}
