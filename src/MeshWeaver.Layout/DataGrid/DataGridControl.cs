using System.Collections.Immutable;
using MeshWeaver.Layout.Composition;

namespace MeshWeaver.Layout.DataGrid;

public record DataGridControl(object Data)
    : UiControl<DataGridControl>(ModuleSetup.ModuleName, ModuleSetup.ApiVersion)
{
    public ImmutableList<object> Columns { get; init; } = [];

    protected override DataGridControl PrepareRendering(RenderingContext context)
    => base.PrepareRendering(context) with
    {
        Style = Style ?? $"min-width: {Columns.Count * 120}px"
    };

    public DataGridControl WithColumn<TColumn>(params DataColumnControl<TColumn>[] columns)
        where TColumn : DataColumnControl<TColumn> => This with { Columns = Columns.AddRange(columns) };

    public object Virtualize { get; init; }
    public object ItemSize { get; init; } = 50;
    public object ResizableColumns { get; init; } = true;
    public DataGridControl WithVirtualize(object virtualize) => This with { Virtualize = virtualize };
    public DataGridControl WithItemSize(object itemSize) => This with { ItemSize = itemSize };
    public DataGridControl Resizable(object resizable = null) => This with { ResizableColumns = resizable ?? true };
}


public abstract record DataColumnControl<TColumn>() : UiControl<TColumn>(ModuleSetup.ModuleName, ModuleSetup.ApiVersion)
    where TColumn : DataColumnControl<TColumn>
{
    public object Title { get; init; }
    public object TooltipText { get; init; }
    public TColumn WithTitle(object title) => This with { Title = title };
    public TColumn WithTooltip(object tooltip) => This with { Tooltip = tooltip };
    public TColumn WithTooltipText(object tooltipText) => This with { TooltipText = tooltipText };

}
public abstract record PropertyColumnControl() : DataColumnControl<PropertyColumnControl> 
{
    public string Property { get; init; }
    public object Sortable { get; init; } = true;
    public object Format { get; init; }

    public PropertyColumnControl WithFormat(object format) => this with { Format = format };
    public abstract Type GetPropertyType();
}

public record PropertyColumnControl<TProperty> : PropertyColumnControl
{
    public override Type GetPropertyType() => typeof(TProperty);
}


public record TemplateColumnControl(params UiControl[] Data)
    : DataColumnControl<TemplateColumnControl>
{
    public object Align { get; set; }
}

public record InfoButtonControl(object EntityType, object EntityId) : UiControl<InfoButtonControl>(ModuleSetup.ModuleName, ModuleSetup.ApiVersion);
public record EditButtonControl(object EntityType, object EntityId) : UiControl<InfoButtonControl>(ModuleSetup.ModuleName, ModuleSetup.ApiVersion);
public record DeleteButtonControl(object EntityType, object EntityId) : UiControl<InfoButtonControl>(ModuleSetup.ModuleName, ModuleSetup.ApiVersion);
