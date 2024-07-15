
namespace OpenSmc.Layout.DataGrid;

public record DataGridControl(object Data)
    : UiControl<DataGridControl>(ModuleSetup.ModuleName, ModuleSetup.ApiVersion, Data)
{
    public IReadOnlyCollection<object> Columns { get; init; }
    public bool Virtualize { get; init; } = false;
    public float ItemSize { get; init; } = 50;
    public bool ResizableColumns { get; init; } = true;

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
