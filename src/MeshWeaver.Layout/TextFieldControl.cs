namespace MeshWeaver.Layout;

public record TextFieldControl(object Data)
    : InputBaseControl<TextFieldControl>(Data), IInputControl
{
    public object IconStart { get; init; }

    public object IconEnd { get; init; }

    public object Autocomplete { get; init; }

    public TextFieldControl WithIconStart(object icon) => this with { IconStart = icon };

    public TextFieldControl WithIconEnd(object icon) => this with { IconEnd = icon };

    public TextFieldControl WithAutocomplete(object autocomplete) => this with { Autocomplete = autocomplete };
}

