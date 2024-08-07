namespace MeshWeaver.Layout;

public record SelectControl(object Data) : ListControlBase<SelectControl>(Data), IListControl
{
    public SelectPosition? Position { get; init; }

    SelectControl WithPosition(SelectPosition position) => this with { Position = position };
}
