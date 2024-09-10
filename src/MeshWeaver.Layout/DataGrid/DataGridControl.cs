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

    public DataGridControl WithColumn<TColumn>(params DataGridColumn<TColumn>[] columns)
        where TColumn : DataGridColumn<TColumn> => This with { Columns = Columns.AddRange(columns) };

    public object Virtualize { get; init; }
    public object ItemSize { get; init; } = 50;
    public object ResizableColumns { get; init; } = true;
    public DataGridControl WithVirtualize(object virtualize) => This with { Virtualize = virtualize };
    public DataGridControl WithItemSize(object itemSize) => This with { ItemSize = itemSize };
    public DataGridControl Resizable(object resizable = null) => This with { ResizableColumns = resizable ?? true };
}


public abstract record DataGridColumn<TColumn>() : UiControl<TColumn>(ModuleSetup.ModuleName, ModuleSetup.ApiVersion)
    where TColumn : DataGridColumn<TColumn>
{
    public object Title { get; init; }
    public object TooltipText { get; init; }
    public TColumn WithTitle(object title) => This with { Title = title };
    public TColumn WithTooltip(object tooltip) => This with { Tooltip = tooltip };
    public TColumn WithTooltipText(object tooltipText) => This with { TooltipText = tooltipText };

}
public abstract record PropertyControl() : DataGridColumn<PropertyControl> 
{
    public object Property { get; init; }
    public object Sortable { get; init; } = true;
    public object Format { get; init; }

    public PropertyControl WithFormat(object format) => this with { Format = format };
    public abstract Type GetPropertyType();
}

public record PropertyControl<TProperty> : PropertyControl
{
    public override Type GetPropertyType() => typeof(TProperty);
}


public record TemplateColumnControl()
    : ContainerControl<TemplateColumnControl, TemplateColumnSkin>(ModuleSetup.ModuleName, ModuleSetup.ApiVersion, new())
{
}

public record TemplateColumnSkin : Skin<TemplateColumnSkin>
{
    public object Title { get; set; }
    public object Align { get; set; }
}
