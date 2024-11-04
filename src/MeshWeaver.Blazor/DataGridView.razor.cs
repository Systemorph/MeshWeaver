using MeshWeaver.Layout;
using MeshWeaver.Layout.DataGrid;
using MeshWeaver.Messaging.Serialization;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.FluentUI.AspNetCore.Components;
using System.Linq.Expressions;
using System.Text.Json;
using System.Text.Json.Nodes;
using MeshWeaver.Layout.Client;

namespace MeshWeaver.Blazor;

public partial class DataGridView
{
    private bool Virtualize { get; set; } 
    private float ItemSize { get; set; }
    private bool ResizableColumns { get; set; }

    private PaginationState Pagination { get; } = new()
    {
        ItemsPerPage = 10
    };

    private IQueryable<JsonObject> QueryableData { get; set; }

    protected override void BindData()
    {
        base.BindData();
        DataBind(ViewModel.Virtualize, x => x.Virtualize);
        DataBind(ViewModel.ItemSize, x => x.ItemSize);
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
        var index = 0;
        builder.AddComponentParameter(++index, nameof(PropertyColumn<object, object>.Property),
            GetPropertyExpression((dynamic)column));
        builder.AddAttribute(++index, "Title", Stream.GetDataBoundValue<string>(column.Title));
        if (column.Format is not null)
            builder.AddAttribute(++index, nameof(PropertyColumn<object, object>.Format), Stream.GetDataBoundValue<string>(column.Format));
        if (column.Sortable is not null)
            builder.AddAttribute(++index, nameof(PropertyColumn<object, object>.Sortable), Stream.GetDataBoundValue<bool>(column.Sortable));
        if (column.Tooltip is not null)
            builder.AddAttribute(++index, nameof(PropertyColumn<object, object>.Tooltip), Stream.GetDataBoundValue<bool>(column.Tooltip));
        if (column.TooltipText is not null)
            builder.AddAttribute(++index, nameof(PropertyColumn<object, object>.TooltipText),
                (Func<JsonObject, string>)(_ => Stream.GetDataBoundValue<string>(column.TooltipText)));

        builder.CloseComponent();

    }

    private Expression<Func<JsonObject, T>> GetPropertyExpression<T>(PropertyColumnControl<T> propertyColumn)
    {
        return e => e.ContainsKey(propertyColumn.Property) ? e[propertyColumn.Property].Deserialize<T>(Stream.Hub.JsonSerializerOptions) : default;
    }

    private const string Details = nameof(Details);
    private const string Edit = nameof(Edit);
    private const string Delete = nameof(Delete);
    private void NavigateToUrl(JsonObject obj, string area)
    {
        var reference = new LayoutAreaReference(area) { Id = $"{obj[EntitySerializationExtensions.TypeProperty]}/{obj[EntitySerializationExtensions.IdProperty]}" };
        NavigationManager.NavigateTo(reference.ToAppHref(Stream.Owner));
    }

}
