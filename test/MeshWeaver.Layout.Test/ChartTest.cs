using System.Collections.Immutable;
using MeshWeaver.Layout.Chart;
using Xunit;

namespace MeshWeaver.Layout.Test;

/// <summary>
/// Tests demonstrating the improved Chart API.
/// </summary>
public class ChartTest
{
    private readonly double[] data1 = [-1.0, 4.0, 3.0, 2.0];
    private readonly double[] data2 = [4.0, 5.0, 6.0, 3.0];
    private readonly string[] labels = ["One", "Two", "Three", "Four"];
    private readonly double[] x1 = [1.0, 4.0, 3.0, 2.0];
    private readonly double[] x2 = [10.0, 2.0, 7.0, 1.0];
    private readonly double[] y = [4.0, 1.0, 2.0, 3.0];

    [Fact]
    public void BarChart_Simple()
    {
        // Super intuitive - data and labels in one call!
        var chart = Charts.Bar(data1, labels)
            .WithTitle("Bar Chart");

        Assert.NotNull(chart);
        var seriesList = chart.Series as ImmutableList<ChartSeries>;
        Assert.NotNull(seriesList);
        Assert.Single(seriesList);
        Assert.Equal("Bar Chart", chart.Title);
        var labelsList = chart.Labels as ImmutableList<string>;
        Assert.Equal(labels.Length, labelsList?.Count);
    }

    [Fact]
    public void BarChart_MultipleSeries()
    {
        // Clear how to add multiple series - no need for Chart.Create()
        var chart = Charts.Create()
            .WithSeries(new BarSeries(data1, "First"))
            .WithSeries(new BarSeries(data2, "Second"))
            .WithLabels(labels)
            .WithTitle("Bar Chart with Multiple Series");

        var seriesList = chart.Series as ImmutableList<ChartSeries>;
        Assert.NotNull(seriesList);
        Assert.Equal(2, seriesList.Count);
        // Legend should be auto-enabled for multiple series with labels
        Assert.True((bool?)chart.ShowLegend ?? false);
    }

    [Fact]
    public void LineChart_Simple()
    {
        // Clean single-line creation with labels
        var chart = Charts.Line(data1, labels)
            .WithTitle("Line Chart");

        Assert.NotNull(chart);
        var seriesList = chart.Series as ImmutableList<ChartSeries>;
        Assert.NotNull(seriesList);
        Assert.Single(seriesList);
        var labelsList = chart.Labels as ImmutableList<string>;
        Assert.Equal(labels.Length, labelsList?.Count);
    }

    [Fact]
    public void LineChart_MultipleSeries()
    {
        // Very clear and intuitive
        var chart = Charts.Create()
            .WithSeries(new LineSeries(data1, "First"))
            .WithSeries(new LineSeries(data2, "Second"))
            .WithLabels(labels)
            .WithTitle("Line Chart");

        var seriesList = chart.Series as ImmutableList<ChartSeries>;
        Assert.NotNull(seriesList);
        Assert.Equal(2, seriesList.Count);
        Assert.True((bool?)chart.ShowLegend ?? false); // Auto-enabled
    }

    [Fact]
    public void MixedChart()
    {
        // Easy to mix chart types
        var chart = Charts.Create()
            .WithSeries(new LineSeries(data1, "Line"))
            .WithSeries(new BarSeries(data2, "Bar"))
            .WithLabels(labels)
            .WithTitle("Mixed Chart");

        var seriesList = chart.Series as ImmutableList<ChartSeries>;
        Assert.NotNull(seriesList);
        Assert.Equal(2, seriesList.Count);
        Assert.IsType<LineSeries>(seriesList[0]);
        Assert.IsType<BarSeries>(seriesList[1]);
    }

    [Fact]
    public void PieChart()
    {
        // Pie charts almost always need labels - now in one call
        var chart = Charts.Pie(data1, labels)
            .WithTitle("Pie Chart");

        Assert.NotNull(chart);
        var seriesList = chart.Series as ImmutableList<ChartSeries>;
        Assert.NotNull(seriesList);
        Assert.IsType<PieSeries>(seriesList[0]);
        var labelsList = chart.Labels as ImmutableList<string>;
        Assert.Equal(labels.Length, labelsList?.Count);
    }

    [Fact]
    public void DoughnutChart()
    {
        // Same clean API for doughnut charts
        var chart = Charts.Doughnut(data1, labels)
            .WithTitle("Doughnut Chart");

        Assert.NotNull(chart);
        var seriesList = chart.Series as ImmutableList<ChartSeries>;
        Assert.NotNull(seriesList);
        Assert.IsType<DoughnutSeries>(seriesList[0]);
        var labelsList = chart.Labels as ImmutableList<string>;
        Assert.Equal(labels.Length, labelsList?.Count);
    }

    [Fact]
    public void RadarChart()
    {
        var chart = Charts.Create()
            .WithSeries(new RadarSeries(data1, "First"))
            .WithSeries(new RadarSeries(data2, "Second"))
            .WithLabels(labels)
            .WithTitle("Radar Chart");

        var seriesList = chart.Series as ImmutableList<ChartSeries>;
        Assert.NotNull(seriesList);
        Assert.Equal(2, seriesList.Count);
        Assert.True((bool?)chart.ShowLegend ?? false);
    }

    [Fact]
    public void PolarChart()
    {
        // Polar charts with labels in one call
        var chart = Charts.Polar(data1, labels)
            .WithTitle("Polar Chart");

        Assert.NotNull(chart);
        var seriesList = chart.Series as ImmutableList<ChartSeries>;
        Assert.NotNull(seriesList);
        Assert.IsType<PolarSeries>(seriesList[0]);
        var labelsList = chart.Labels as ImmutableList<string>;
        Assert.Equal(labels.Length, labelsList?.Count);
    }

    [Fact]
    public void ScatterChart()
    {
        var chart = Charts.Create()
            .WithSeries(new ScatterSeries(x1, y, "First"))
            .WithSeries(new ScatterSeries(x2, y, "Second"))
            .WithTitle("Scatter Chart");

        var seriesList = chart.Series as ImmutableList<ChartSeries>;
        Assert.NotNull(seriesList);
        Assert.Equal(2, seriesList.Count);
        Assert.True((bool?)chart.ShowLegend ?? false);
    }

    [Fact]
    public void BubbleChart()
    {
        double[] radius = [8.0, 11.0, 20.0, 18.0];

        var chart = Charts.Create()
            .WithSeries(new BubbleSeries(x1, y, radius, "First"))
            .WithSeries(new BubbleSeries(x2, y, radius, "Second"))
            .WithTitle("Bubble Chart");

        var seriesList = chart.Series as ImmutableList<ChartSeries>;
        Assert.NotNull(seriesList);
        Assert.Equal(2, seriesList.Count);
        Assert.True((bool?)chart.ShowLegend ?? false);
    }

    [Fact]
    public void StackedBarChart()
    {
        // Very clear that this is a stacked chart
        var chart = Charts.Create()
            .WithSeries(new BarSeries(data1, "One"))
            .WithSeries(new BarSeries(data2, "Two"))
            .WithLabels(labels)
            .WithTitle("Stacked Chart")
            .Stacked(); // Clear and explicit

        Assert.True((bool?)chart.IsStacked ?? false);
    }

    [Fact]
    public void AreaChart()
    {
        // Much clearer than the old API
        var chart = Charts.Create()
            .WithSeries(new LineSeries(data1, "First").WithFill())
            .WithSeries(new LineSeries(data2, "Second").WithFill())
            .WithLabels(labels)
            .WithTitle("Area Chart");

        var seriesList = chart.Series as ImmutableList<ChartSeries>;
        Assert.NotNull(seriesList);
        var series1 = (LineSeries)seriesList[0];
        var series2 = (LineSeries)seriesList[1];

        Assert.True((bool?)series1.Fill ?? false);
        Assert.True((bool?)series2.Fill ?? false);
    }

    [Fact]
    public void ChartWithoutAnimation()
    {
        var chart = Charts.Bar(data1)
            .WithLabels(labels)
            .WithTitle("No Animation")
            .WithoutAnimation();

        Assert.True((bool?)chart.DisableAnimation ?? false);
    }

    [Fact]
    public void ChartWithCustomStyling()
    {
        // Easy to customize individual series
        var chart = Charts.Create()
            .WithSeries(
                new BarSeries(data1, "First")
                    .WithBackgroundColor("#FF6384")
                    .WithBorderColor("#FF0000")
                    .WithBorderWidth(2)
            )
            .WithLabels(labels)
            .WithTitle("Styled Chart");

        var seriesList = chart.Series as ImmutableList<ChartSeries>;
        Assert.NotNull(seriesList);
        var series = (BarSeries)seriesList[0];
        Assert.Equal("#FF6384", series.BackgroundColor);
    }

    [Fact]
    public void ChartWithSize()
    {
        var chart = Charts.Bar(data1)
            .WithLabels(labels)
            .WithWidth("800px")
            .WithHeight("400px");

        Assert.NotNull(chart.Width);
        Assert.NotNull(chart.Height);
        Assert.Equal("800px", chart.Width);
        Assert.Equal("400px", chart.Height);
    }

    [Fact]
    public void ManualLegendControl()
    {
        // Can still manually control legend
        var chart1 = Charts.Bar(data1)
            .WithLabels(labels)
            .WithLegend(false); // Explicitly disable

        Assert.False((bool?)chart1.ShowLegend ?? true);

        var chart2 = Charts.Bar(data1)
            .WithLabels(labels)
            .WithLegend(true); // Explicitly enable

        Assert.True((bool?)chart2.ShowLegend ?? false);
    }

    [Fact]
    public void QuickDraw()
    {
        // Simple case is still simple
        var chart = Charts.Create()
            .WithSeries(new BarSeries(data1))
            .WithSeries(new BarSeries(data2))
            .WithSeries(new BarSeries(x1));

        var seriesList = chart.Series as ImmutableList<ChartSeries>;
        Assert.NotNull(seriesList);
        Assert.Equal(3, seriesList.Count);
        // No legend auto-enabled since series have no labels
        Assert.Null(chart.ShowLegend);
    }

    [Fact]
    public void LineSeries_WithCurve()
    {
        var chart = Charts.Create()
            .WithSeries(
                new LineSeries(data1, "Curved")
                    .WithTension(0.4) // Smooth curves
                    .WithPointRadius(5)
            )
            .WithLabels(labels);

        var seriesList = chart.Series as ImmutableList<ChartSeries>;
        Assert.NotNull(seriesList);
        var series = (LineSeries)seriesList[0];
        Assert.Equal(0.4, series.Tension);
        Assert.Equal(5, series.PointRadius);
    }

    [Fact]
    public void BarSeries_WithCustomBarSizing()
    {
        var chart = Charts.Create()
            .WithSeries(
                new BarSeries(data1)
                    .WithBarPercentage(0.8)
                    .WithCategoryPercentage(0.9)
            )
            .WithLabels(labels);

        var seriesList = chart.Series as ImmutableList<ChartSeries>;
        Assert.NotNull(seriesList);
        var series = (BarSeries)seriesList[0];
        Assert.Equal(0.8, series.BarPercentage);
        Assert.Equal(0.9, series.CategoryPercentage);
    }
}
