namespace MeshWeaver.Layout;

public record TextBoxControl(object Data)
    : InputBaseControl<TextBoxControl>(Data), IInputControl
{
    public object IconStart { get; init; }

    public object IconEnd { get; init; }

    public object Autocomplete { get; init; }

    public TextBoxControl WithIconStart(object icon) => this with { IconStart = icon };

    public TextBoxControl WithIconEnd(object icon) => this with { IconEnd = icon };

    public TextBoxControl WithAutocomplete(object autocomplete) => this with { Autocomplete = autocomplete };
}

public static class TextBoxSkin
{
    public const string Search = nameof(Search);
}
