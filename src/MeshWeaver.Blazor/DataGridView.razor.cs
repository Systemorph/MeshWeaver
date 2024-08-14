using MeshWeaver.Layout.DataGrid;
using Microsoft.FluentUI.AspNetCore.Components;
using System.Linq.Expressions;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MeshWeaver.Blazor;

public partial class DataGridView
{
    private PaginationState Pagination { get; } = new()
    {
        ItemsPerPage = 10
    };

    private IQueryable<JsonObject> QueryableData { get; set; }

    private Expression<Func<JsonObject, T>> GetPropertyExpression<T>(DataGridColumn<T> column)
    {
        return e => e.ContainsKey(column.Property) ? e[column.Property].Deserialize<T>(Stream.Hub.JsonSerializerOptions) : default;
    }
    protected override void BindData()
    {
        base.BindData();
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

}
