using MeshWeaver.Layout.DataGrid;
using MeshWeaver.Messaging.Serialization;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.FluentUI.AspNetCore.Components;
using System.Linq.Expressions;
using System.Text.Json;
using System.Text.Json.Nodes;
using MeshWeaver.Data;
using MeshWeaver.Layout.Client;

namespace MeshWeaver.Blazor.Components;

public partial class DataGridView
{
    private bool virtualize;
    private float itemSize;
    private bool resizableColumns;

    private PaginationState Pagination { get; } = new()
    {
        ItemsPerPage = 10
    };

    private IQueryable<JsonObject> QueryableData;

    protected override void BindData()
    {
        base.BindData();
        DataBind(ViewModel.Virtualize, x => x.virtualize);
        DataBind(ViewModel.ItemSize, x => x.itemSize);
        DataBind(
            ViewModel.Data,
            x => x.QueryableData,
            o => ((IEnumerable<object>)o)?.Cast<JsonObject>().AsQueryable()
        );
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
        builder.AddAttribute(2, "Title", Stream.GetDataBoundValue<string>(column.Title, ViewModel.DataContext));
        if (column.Format is not null)
            builder.AddAttribute(3, nameof(PropertyColumn<object, object>.Format), Stream.GetDataBoundValue<string>(column.Format, ViewModel.DataContext));
        if (column.Sortable is not null)
            builder.AddAttribute(4, nameof(PropertyColumn<object, object>.Sortable), Stream.GetDataBoundValue<bool>(column.Sortable, ViewModel.DataContext));
        if (column.Tooltip is not null)
            builder.AddAttribute(5, nameof(PropertyColumn<object, object>.TooltipText),
                (Func<JsonObject, string>)(_ => Stream.GetDataBoundValue<string>(column.Tooltip, ViewModel.DataContext)));

        builder.CloseComponent();

    }

    private Expression<Func<JsonObject, T>> GetPropertyExpression<T>(PropertyColumnControl<T> propertyColumn)
    {
        return e => e.ContainsKey(propertyColumn.Property) ? e[propertyColumn.Property].Deserialize<T>(Stream.Hub.JsonSerializerOptions) : default;
    }

    private const string Details = nameof(Details);

}
