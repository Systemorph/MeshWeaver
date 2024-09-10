using Microsoft.FluentUI.AspNetCore.Components;
using System.Text.Json.Nodes;

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


}
