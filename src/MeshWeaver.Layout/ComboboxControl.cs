namespace MeshWeaver.Layout;

public record ComboboxControl(object Data) : ListControlBase<ComboboxControl>(Data), IListControl
{
    public object Autofocus { get; init; }
    public object Autocomplete { get; init; }
    public object Placeholder { get; init; }
    public object Position { get; init; }
    public object Disabled { get; init; }

    ComboboxControl WithAutofocus(object autofocus) => this with {Autofocus = autofocus};
    ComboboxControl WithAutocomplete(object autocomplete) => this with {Autocomplete = autocomplete};
    ComboboxControl WithPlaceholder(object placeholder) => this with {Placeholder = placeholder};
    ComboboxControl WithPosition(object position) => this with {Position = position};
    ComboboxControl WithDisabled(object disabled) => this with {Disabled = disabled};
}

public enum ComboboxAutocomplete
{
    Inline,
    List,
    Both
}
