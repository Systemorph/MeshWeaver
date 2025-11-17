using System.Text.Json;
using MeshWeaver.GridModel;
using Microsoft.AspNetCore.Components;
using Radzen;

namespace MeshWeaver.Blazor.Radzen;

public partial class RadzenPivotGridView : BlazorView<PivotGridControl, RadzenPivotGridView>
{
    [Inject] private ThemeService themeService { get; set; } = null!;

    private PivotConfiguration? Configuration { get; set; }
    private object? RawData { get; set; }
    private Type? DataItemType { get; set; }
    private bool ShowPager { get; set; }
    private int PageSize { get; set; }
    private bool isDarkMode;

    protected override async Task OnInitializedAsync()
    {
        await base.OnInitializedAsync();
        var oldTheme = themeService.Theme;
        isDarkMode = await IsDarkModeAsync();
        var newTheme = GetRadzenTheme();
        if (oldTheme != newTheme)
        {
            themeService.SetTheme(GetRadzenTheme());
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

    private global::Radzen.TextAlign MapTextAlign(GridModel.TextAlign textAlign)
    {
        return textAlign switch
        {
            GridModel.TextAlign.Left => global::Radzen.TextAlign.Left,
            GridModel.TextAlign.Center => global::Radzen.TextAlign.Center,
            GridModel.TextAlign.Right => global::Radzen.TextAlign.Right,
            GridModel.TextAlign.Justify => global::Radzen.TextAlign.Justify,
            _ => global::Radzen.TextAlign.Right
        };
    }

    private global::Radzen.SortOrder MapSortOrder(GridModel.SortOrder sortOrder)
    {
        return sortOrder switch
        {
            GridModel.SortOrder.Ascending => global::Radzen.SortOrder.Ascending,
            GridModel.SortOrder.Descending => global::Radzen.SortOrder.Descending,
            _ => global::Radzen.SortOrder.Descending
        };
    }

    private string GetRadzenTheme()
    {
        return isDarkMode ? "standard-dark" : "standard";
        //var baseTheme = themeService.Theme ?? "material";

        //// Use cached dark mode value from IsDarkModeAsync
        //if (isDarkMode && !baseTheme.EndsWith("-dark"))
        //{
        //    return baseTheme + "-dark";
        //}

        //// If not in dark mode and theme ends with "-dark", remove it
        //if (!isDarkMode && baseTheme.EndsWith("-dark"))
        //{
        //    return baseTheme.Substring(0, baseTheme.Length - 5);
        //}

        //return baseTheme;
    }
}
