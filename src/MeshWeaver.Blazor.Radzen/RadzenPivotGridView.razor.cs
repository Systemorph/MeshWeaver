using System.Text.Json;
using MeshWeaver.GridModel;
using Microsoft.AspNetCore.Components;
using Radzen;

namespace MeshWeaver.Blazor.Radzen;

public partial class RadzenPivotGridView : BlazorView<PivotGridControl, RadzenPivotGridView>
{
    [Inject] private ThemeService? themeService { get; set; }

    private PivotConfiguration? Configuration { get; set; }
    private IEnumerable<IDictionary<string, object>>? RawData { get; set; }
    private bool ShowPager { get; set; } = true;
    private int PageSize { get; set; } = 50;

    protected override void OnParametersSet()
    {
        base.OnParametersSet();
        StateHasChanged();
    }

    protected override void BindData()
    {
        base.BindData();

        DataBind(ViewModel.Configuration, x => x.Configuration);
        DataBind(ViewModel.Data, x => x.RawData, (data, _) =>
        {
            if (data == null)
                return null;

            // Data is already IEnumerable<Dictionary<string, object>> - just cast it
            if (data is IEnumerable<Dictionary<string, object>> dictionaries)
                return dictionaries.Cast<IDictionary<string, object>>();

            return Enumerable.Empty<IDictionary<string, object>>();
        });
        DataBind(ViewModel.ShowPager, x => x.ShowPager, defaultValue: true);
        DataBind(ViewModel.PageSize, x => x.PageSize, defaultValue: 50);
    }

    private global::Radzen.AggregateFunction GetAggregateFunction(GridModel.AggregateFunction function)
    {
        return function switch
        {
            GridModel.AggregateFunction.Sum => global::Radzen.AggregateFunction.Sum,
            GridModel.AggregateFunction.Average => global::Radzen.AggregateFunction.Average,
            GridModel.AggregateFunction.Count => global::Radzen.AggregateFunction.Count,
            GridModel.AggregateFunction.Min => global::Radzen.AggregateFunction.Min,
            GridModel.AggregateFunction.Max => global::Radzen.AggregateFunction.Max,
            _ => global::Radzen.AggregateFunction.Sum
        };
    }
}
