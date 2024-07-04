using System.Collections.Immutable;

namespace OpenSmc.Layout.DataGrid;

public record DataGridControl(object Data)
    : UiControl<DataGridControl>(ModuleSetup.ModuleName, ModuleSetup.ApiVersion, Data)
{
    public IReadOnlyCollection<object> Columns { get; init; }
}

public abstract record DataGridColumn
{
    public string Property { get; init; }
    public bool Sortable { get; init; } = true;
    public string Format { get; init; }
    public string Title { get; init; }

    public abstract Type GetPropertyType();
}

public record DataGridColumn<TProperty> : DataGridColumn
{
    public override Type GetPropertyType() => typeof(TProperty);
}
