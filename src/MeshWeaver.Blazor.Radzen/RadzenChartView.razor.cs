using System.Collections.Immutable;
using System.Text.Json;
using MeshWeaver.Layout.Chart;
using Microsoft.AspNetCore.Components;

namespace MeshWeaver.Blazor.Radzen;

public partial class RadzenChartView : RadzenViewBase<ChartControl, RadzenChartView>
{
    private object? Series { get; set; }
    private object? Labels { get; set; }
    private object? Title { get; set; }
    private object? Subtitle { get; set; }
    private object? ShowLegend { get; set; }
    private object? LegendPosition { get; set; }
    private object? IsStacked { get; set; }
    private object? DisableAnimation { get; set; }
    private object? Width { get; set; }
    private object? Height { get; set; }
    private object? CategoryAxisLabelAngle { get; set; }

    protected override void BindData()
    {
        base.BindData();

        // Bind all chart properties - use Stream's Hub.JsonSerializerOptions for proper type handling
        DataBind(ViewModel.Series, x => x.Series, (data, _) =>
        {
            if (data == null)
                return null;

            // If it's already the correct type, return as-is
            if (data is ImmutableList<ChartSeries>)
                return data;

            // If it's a JsonElement, deserialize it using Hub's JsonSerializerOptions
            if (data is JsonElement jsonElement)
            {
                var json = jsonElement.GetRawText();
                return JsonSerializer.Deserialize<ImmutableList<ChartSeries>>(json, Stream!.Hub.JsonSerializerOptions);
            }

            return data;
        });

        DataBind(ViewModel.Labels, x => x.Labels, (data, _) =>
        {
            if (data == null)
                return null;

            // If it's already the correct type, return as-is
            if (data is ImmutableList<string>)
                return data;

            // If it's a JsonElement, deserialize it using Hub's JsonSerializerOptions
            if (data is JsonElement jsonElement)
            {
                var json = jsonElement.GetRawText();
                return JsonSerializer.Deserialize<ImmutableList<string>>(json, Stream!.Hub.JsonSerializerOptions);
            }

            return data;
        });

        DataBind(ViewModel.Title, x => x.Title);
        DataBind(ViewModel.Subtitle, x => x.Subtitle);
        DataBind(ViewModel.ShowLegend, x => x.ShowLegend);
        DataBind(ViewModel.LegendPosition, x => x.LegendPosition);
        DataBind(ViewModel.IsStacked, x => x.IsStacked);
        DataBind(ViewModel.DisableAnimation, x => x.DisableAnimation);
        DataBind(ViewModel.Width, x => x.Width, defaultValue: "100%");
        DataBind(ViewModel.Height, x => x.Height, defaultValue: "400px");
        DataBind(ViewModel.CategoryAxisLabelAngle, x => x.CategoryAxisLabelAngle, defaultValue: -45);
    }
}
