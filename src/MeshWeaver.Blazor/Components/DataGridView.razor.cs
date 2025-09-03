using MeshWeaver.Layout.DataGrid;
using MeshWeaver.Layout;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.FluentUI.AspNetCore.Components;
using System.Linq.Expressions;
using System.Text.Json;
using System.Text.Json.Nodes;
using MeshWeaver.Layout.Client;

namespace MeshWeaver.Blazor.Components;

public partial class DataGridView
{
    private bool virtualize;
    private float itemSize;
    private bool resizableColumns;
    private string generateHeader = "Sticky";
    private string? gridTemplateColumns;
    private bool loading;
    private string? ariaLabel;
    private string? role;
    private string? rowClass;
    private string? rowStyle;
    private bool showHover;
    private string? selectionMode;
    private object? selectedItems;
    private object? itemsProvider;
    private object? emptyContent;
    private object? loadingTemplate;
    private int totalItemCount;
    private bool autoFit;
    private bool autoFocus;
    private bool autoItemsPerPage;
    private int itemsPerPage;

    private PaginationState Pagination { get; set; } = new();

    private IQueryable<JsonObject> QueryableData = Enumerable.Empty<JsonObject>().AsQueryable();

    protected override void BindData()
    {
        base.BindData();
        DataBind(ViewModel.Virtualize, x => x.virtualize);
        DataBind(ViewModel.ItemSize, x => x.itemSize);
        DataBind(ViewModel.ResizableColumns, x => x.resizableColumns);
        DataBind(ViewModel.GenerateHeader, x => x.generateHeader);
        DataBind(ViewModel.GridTemplateColumns, x => x.gridTemplateColumns);
        DataBind(ViewModel.Loading, x => x.loading);
        DataBind(ViewModel.AriaLabel, x => x.ariaLabel);
        DataBind(ViewModel.Role, x => x.role);
        DataBind(ViewModel.RowClass, x => x.rowClass);
        DataBind(ViewModel.RowStyle, x => x.rowStyle);
        DataBind(ViewModel.ShowHover, x => x.showHover);
        DataBind(ViewModel.SelectionMode, x => x.selectionMode);
        DataBind(ViewModel.SelectedItems, x => x.selectedItems);
        DataBind(ViewModel.ItemsProvider, x => x.itemsProvider);
        DataBind(ViewModel.EmptyContent, x => x.emptyContent);
        DataBind(ViewModel.LoadingTemplate, x => x.loadingTemplate);
        DataBind(ViewModel.AutoFit, x => x.autoFit, defaultValue: true);
        DataBind(ViewModel.AutoFocus, x => x.autoFocus, defaultValue: false);
        DataBind(ViewModel.AutoItemsPerPage, x => x.autoItemsPerPage, defaultValue: true);
        DataBind(ViewModel.ItemsPerPage, x => x.itemsPerPage, defaultValue: 10);
        Pagination.ItemsPerPage = itemsPerPage;
        DataBind(
            ViewModel.Data,
            x => x.QueryableData,
            (o, _) =>
            { 
                if(o is null)
                    return null;
                var elements = ((JsonElement)o).Deserialize<IReadOnlyCollection<JsonObject>>();
                if(elements is null)
                    return  Enumerable.Empty<JsonObject>().AsQueryable();
                totalItemCount = elements.Count;
                return elements.AsQueryable();
            });
    }

    protected override async Task OnParametersSetAsync()
    {
        await base.OnParametersSetAsync();
        await Pagination.SetTotalItemCountAsync(totalItemCount);
    }

    protected override void OnInitialized()
    {
        base.OnInitialized();
        Pagination.TotalItemCountChanged += (_, _) => StateHasChanged();
    }

    public void RenderPropertyColumn(RenderTreeBuilder builder, PropertyColumnControl column)
    {
        builder.OpenComponent(0,
            typeof(PropertyColumn<,>).MakeGenericType(typeof(JsonObject), column.GetPropertyType()));
        builder.AddComponentParameter(1, nameof(PropertyColumn<object, object>.Property),
            GetPropertyExpression((dynamic)column));
        if(Stream is null)
            throw new InvalidOperationException("Stream must be set before rendering the DataGridView.");
            
        // Only use properties that actually exist in FluentUI PropertyColumn
        if (column.Title is not null)
            builder.AddAttribute(2, "Title", Stream.GetDataBoundValue<string>(column.Title, ViewModel.DataContext ?? "/"));
        if (column.Format is not null)
            builder.AddAttribute(3, nameof(PropertyColumn<object, object>.Format), Stream.GetDataBoundValue<string>(column.Format, ViewModel.DataContext));
        if (column.Sortable is not null)
            builder.AddAttribute(4, nameof(PropertyColumn<object, object>.Sortable), Stream.GetDataBoundValue<bool>(column.Sortable, ViewModel.DataContext));
        if (column.Tooltip is not null)
            builder.AddAttribute(5, nameof(PropertyColumn<object, object>.TooltipText),
                (Func<JsonObject, string>)(_ => Stream.GetDataBoundValue<string>(column.Tooltip, ViewModel.DataContext ?? "/") ?? string.Empty));
        if (column.IsDefaultSortColumn is not null)
            builder.AddAttribute(6, nameof(PropertyColumn<object, object>.IsDefaultSortColumn), Stream.GetDataBoundValue<bool>(column.IsDefaultSortColumn, ViewModel.DataContext));
        if (column.InitialSortDirection is not null)
            builder.AddAttribute(7, nameof(PropertyColumn<object, object>.InitialSortDirection), Stream.GetDataBoundValue<SortDirection>(column.InitialSortDirection, ViewModel.DataContext));
        if (column.Align is not null)
            builder.AddAttribute(8, nameof(PropertyColumn<object, object>.Align), Stream.GetDataBoundValue<Align>(column.Align, ViewModel.DataContext));
        if (column.Width is not null)
            builder.AddAttribute(9, nameof(PropertyColumn<object, object>.Width), Stream.GetDataBoundValue<string>(column.Width, ViewModel.DataContext));

        // Skip rendering if not visible
        if (column.Visible is not null && !Stream.GetDataBoundValue<bool>(column.Visible, ViewModel.DataContext))
            return;

        builder.CloseComponent();
    }

    private Expression<Func<JsonObject, T?>> GetPropertyExpression<T>(PropertyColumnControl<T> propertyColumn)
    {
        return e => e.ContainsKey(propertyColumn.Property ?? "") ? e[propertyColumn.Property!].Deserialize<T>(Stream!.Hub.JsonSerializerOptions) : default!;
    }

    // FluentDataGrid helper methods
    private GenerateHeaderOption GetGenerateHeaderOption()
    {
        return generateHeader switch
        {
            "None" => GenerateHeaderOption.None,
            "Default" => GenerateHeaderOption.Default,
            "Sticky" => GenerateHeaderOption.Sticky,
            _ => GenerateHeaderOption.Sticky
        };
    }

    private DataGridSelectMode? GetSelectionMode()
    {
        return selectionMode switch
        {
            "Single" => DataGridSelectMode.Single,
            "Multiple" => DataGridSelectMode.Multiple,
            _ => null
        };
    }

    private GridItemsProvider<JsonObject>? GetItemsProvider()
    {
        return itemsProvider as GridItemsProvider<JsonObject>;
    }

    private RenderFragment? GetEmptyContent()
    {
        if (emptyContent is UiControl control)
            return builder => 
            {
                builder.OpenComponent<DispatchView>(0);
                builder.AddComponentParameter(1, nameof(DispatchView.ViewModel), control);
                builder.AddComponentParameter(2, nameof(DispatchView.Stream), Stream);
                builder.AddComponentParameter(3, nameof(DispatchView.Area), Area);
                builder.CloseComponent();
            };
        if (emptyContent is string text)
            return builder => builder.AddContent(0, text);
        return null;
    }

    private RenderFragment? GetLoadingTemplate()
    {
        if (loadingTemplate is UiControl control)
            return builder => 
            {
                builder.OpenComponent<DispatchView>(0);
                builder.AddComponentParameter(1, nameof(DispatchView.ViewModel), control);
                builder.AddComponentParameter(2, nameof(DispatchView.Stream), Stream);
                builder.AddComponentParameter(3, nameof(DispatchView.Area), Area);
                builder.CloseComponent();
            };
        if (loadingTemplate is string text)
            return builder => builder.AddContent(0, text);
        return null;
    }



    // TemplateColumn helper methods - only using supported properties
    private string? GetTemplateColumnTitle(TemplateColumnControl column)
    {
        return Stream?.GetDataBoundValue<string>(column.Title, ViewModel.DataContext ?? "/");
    }

    private string? GetTemplateColumnWidth(TemplateColumnControl column)
    {
        return Stream?.GetDataBoundValue<string>(column.Width, ViewModel.DataContext ?? "/");
    }

    private Align GetTemplateColumnAlign(TemplateColumnControl column)
    {
        return Stream?.GetDataBoundValue<Align>(column.Align, ViewModel.DataContext ?? "/") ?? Align.Start;
    }


    public EventCallback<FluentDataGridCell<JsonObject>> OnCellClick => EventCallback.Factory.Create<FluentDataGridCell<JsonObject>>(this, HandleCellClick);
    private Task HandleCellClick(FluentDataGridCell<JsonObject> obj)
    {
        Hub.Post(new ClickedEvent(Area, Stream!.StreamId){Payload = new DataGridCellClick(obj.Item, obj.GridColumn)}, o => o.WithTarget(Stream.Owner));
        return Task.CompletedTask;
    }
}
