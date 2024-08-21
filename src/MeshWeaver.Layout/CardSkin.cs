namespace MeshWeaver.Layout;

public record CardSkin : Skin<CardSkin>
{
    public object AreaRestricted { get; init; }
    public object Height { get; init; }
    public object Width { get; init; }
    public object MinimalStyle { get; init; }

    public CardSkin WithAreaRestricted(object areaRestricted) => This with { AreaRestricted = areaRestricted };

    public CardSkin WithHeight(object height) => This with { Height = height };

    public CardSkin WithWidth(object width) => This with { Width = width };

    public CardSkin WithMinimalStyle() => This with { MinimalStyle = true };
}
