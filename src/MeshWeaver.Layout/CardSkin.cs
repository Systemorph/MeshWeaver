namespace MeshWeaver.Layout;

public record CardSkin : Skin<CardSkin>
{
    public object AreaRestricted { get; init; }
    public object Height { get; init; }
    public object Width { get; init; }
    public object MinimalStyle { get; init; }
}
