namespace MeshWeaver.Layout;

public record SearchControl(object Data)
    : InputBaseControl<SearchControl>(Data), IInputControl
{
    public object Appearance { get; init; }
    public object MaxLength { get; init; }
    public object MinLength { get; init; }
    public object Size { get; init; }

    public SearchControl WithAppearance(object appearance) => this with { Appearance = appearance };
    public SearchControl WithMaxLength(object maxLength) => this with { MaxLength = maxLength };
    public SearchControl WithMinLength(object minLength) => this with { MinLength = minLength };
    public SearchControl WithSize(object size) => this with { Size = size };
}

