using System.Text.Json;
using MeshWeaver.Layout.Pivot;
using Microsoft.AspNetCore.Components;

namespace MeshWeaver.Blazor.Radzen;

public partial class RadzenPivotGridView : RadzenViewBase<PivotGridControl, RadzenPivotGridView>
{
    private PivotConfiguration? Configuration { get; set; }
    private object? RawData { get; set; }
    private Type? DataItemType { get; set; }
    private bool ShowPager { get; set; }
    private int PageSize { get; set; }

    protected override async Task OnInitializedAsync()
    {
        var oldTheme = themeService.Theme;
        await base.OnInitializedAsync();
        var newTheme = GetRadzenTheme();
        if (oldTheme != newTheme)
        {
            StateHasChanged();
        }
    }


    protected override void BindData()
    {
        base.BindData();

        DataBind(ViewModel.Configuration, x => x.Configuration);
        DataBind(ViewModel.Data, x => x.RawData, (data, _) =>
        {
            if (data == null || Configuration == null)
                return null;

            var jsonElement = (JsonElement)data;

            // Generate a type based on the configuration using DynamicTypeGenerator
            var properties = GetPropertiesFromConfiguration(Configuration);
            DataItemType = DynamicTypeGenerator.GenerateType(properties);

            // Deserialize to the generated type
            var listType = typeof(List<>).MakeGenericType(DataItemType);
            var deserializeMethod = typeof(JsonSerializer).GetMethod(nameof(JsonSerializer.Deserialize),
                new[] { typeof(JsonElement), typeof(JsonSerializerOptions) })!
                .MakeGenericMethod(listType);

            var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            var deserializedList = deserializeMethod.Invoke(null, [jsonElement, options]);

            // Convert to IQueryable for Radzen PivotDataGrid
            if (deserializedList != null)
            {
                var asQueryableMethod = typeof(Queryable).GetMethods()
                    .Where(m => m.Name == nameof(Queryable.AsQueryable) && m.IsGenericMethodDefinition)
                    .First()
                    .MakeGenericMethod(DataItemType);
                return asQueryableMethod.Invoke(null, new[] { deserializedList });
            }

            return null;
        });
        DataBind(ViewModel.ShowPager, x => x.ShowPager, defaultValue: true);
        DataBind(ViewModel.PageSize, x => x.PageSize, defaultValue: 50);
    }

    private static List<(string Name, string TypeName)> GetPropertiesFromConfiguration(PivotConfiguration config)
    {
        var properties = new List<(string Name, string TypeName)>();

        // Include all available dimensions and aggregates for field picking
        foreach (var dim in config.AvailableDimensions)
            properties.Add((dim.Field, dim.TypeName));
        foreach (var agg in config.AvailableAggregates)
            properties.Add((agg.Field, agg.TypeName));

        // Also ensure selected fields are included (in case they're not in available collections)
        foreach (var dim in config.RowDimensions)
            properties.Add((dim.Field, dim.TypeName));
        foreach (var dim in config.ColumnDimensions)
            properties.Add((dim.Field, dim.TypeName));
        foreach (var agg in config.Aggregates)
            properties.Add((agg.Field, agg.TypeName));

        return properties.DistinctBy(p => p.Name).ToList();
    }

    private global::Radzen.AggregateFunction GetAggregateFunction(AggregateFunction function)
    {
        return function switch
        {
            AggregateFunction.Sum => global::Radzen.AggregateFunction.Sum,
            AggregateFunction.Average => global::Radzen.AggregateFunction.Average,
            AggregateFunction.Count => global::Radzen.AggregateFunction.Count,
            AggregateFunction.Min => global::Radzen.AggregateFunction.Min,
            AggregateFunction.Max => global::Radzen.AggregateFunction.Max,
            _ => global::Radzen.AggregateFunction.Sum
        };
    }

    private global::Radzen.TextAlign MapTextAlign(TextAlign textAlign)
    {
        return textAlign switch
        {
            TextAlign.Left => global::Radzen.TextAlign.Left,
            TextAlign.Center => global::Radzen.TextAlign.Center,
            TextAlign.Right => global::Radzen.TextAlign.Right,
            TextAlign.Justify => global::Radzen.TextAlign.Justify,
            _ => global::Radzen.TextAlign.Right
        };
    }

    private global::Radzen.SortOrder MapSortOrder(SortOrder sortOrder)
    {
        return sortOrder switch
        {
            SortOrder.Ascending => global::Radzen.SortOrder.Ascending,
            SortOrder.Descending => global::Radzen.SortOrder.Descending,
            _ => global::Radzen.SortOrder.Descending
        };
    }
}
