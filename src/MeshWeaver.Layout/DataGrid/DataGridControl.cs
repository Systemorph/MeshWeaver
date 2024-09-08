
using MeshWeaver.Data;
using System.Reflection;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Layout.Domain;

namespace MeshWeaver.Layout.DataGrid;

public record DataGridControl(object Data)
    : ContainerControl<DataGridControl, DataGridSkin>(ModuleSetup.ModuleName, ModuleSetup.ApiVersion, new())
{
    protected override DataGridControl PrepareRendering(RenderingContext context)
    => base.PrepareRendering(context) with
    {
        Style = Style ?? $"min-width: {Areas.Count * 120}px"
    };

}
public record DataGridSkin : Skin<DataGridSkin>
{
    public object Virtualize { get; init; }
    public object ItemSize { get; init; } = 50;
    public object ResizableColumns { get; init; } = true;
    public DataGridSkin WithVirtualize(object virtualize) => This with { Virtualize = virtualize };
    public DataGridSkin WithItemSize(object itemSize) => This with { ItemSize = itemSize };
    public DataGridSkin Resizable(object resizable = null) => This with { ResizableColumns = resizable ?? true };
}
public abstract record PropertyColumnControl() : UiControl<PropertyColumnControl>(ModuleSetup.ModuleName, ModuleSetup.ApiVersion)
{
    public object Property { get; init; }
    public object Sortable { get; init; } = true;
    public object Format { get; init; }

    public PropertyColumnControl WithFormat(object format) => this with { Format = format };
    public object Title { get; init; }
    public PropertyColumnControl WithTitle(object title) => this with { Title = title };
    public PropertyColumnControl WithTooltip(object tooltip) => this with { Tooltip = tooltip };
    public object TooltipText { get; init; }
    public PropertyColumnControl WithTooltipText(object tooltipText) => this with { TooltipText = tooltipText };
    public abstract Type GetPropertyType();
}

public record PropertyColumnControl<TProperty> : PropertyColumnControl
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
