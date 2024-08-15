
using MeshWeaver.Layout.Composition;

namespace MeshWeaver.Layout.DataGrid;

public record DataGridControl(object Data)
    : UiControl<DataGridControl>(ModuleSetup.ModuleName, ModuleSetup.ApiVersion)
{
    public IReadOnlyCollection<object> Columns { get; init; } = [];
    public bool Virtualize { get; init; } = false;
    public float ItemSize { get; init; } = 50;
    public bool ResizableColumns { get; init; } = true;
    public virtual bool Equals(DataGridControl other)
    {
        if (other is null)
            return false;
        if (ReferenceEquals(other, this))
            return true;
        return base.Equals(other) &&
               Columns.SequenceEqual(other.Columns) &&
               Virtualize == other.Virtualize &&
               ItemSize == other.ItemSize &&
               ResizableColumns == other.ResizableColumns;
    }

    protected override DataGridControl PrepareRendering(RenderingContext context)
    => this with
    {
        Style = Style ?? $"min-width: {Columns.Count * 120}px"
    };

    public override int GetHashCode()
    {
        return HashCode.Combine(base.GetHashCode(), 
            Columns.Aggregate(17, (r,c) => r ^c.GetHashCode()), 
            Virtualize, 
            ItemSize, 
            ResizableColumns);
    }
}

public abstract record DataGridColumn
{
    public string Property { get; init; }
    public bool Sortable { get; init; } = true;
    public string Format { get; init; }
    public string Title { get; init; }
    public bool Tooltip { get; init; }
    public string TooltipText { get; init; }
    public abstract Type GetPropertyType();
}

public record DataGridColumn<TProperty> : DataGridColumn
{
    public override Type GetPropertyType() => typeof(TProperty);
}
