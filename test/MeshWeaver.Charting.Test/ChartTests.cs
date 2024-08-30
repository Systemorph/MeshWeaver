using System.Text.Json;
using System.Text.Json.Serialization;
using MeshWeaver.Charting.Builders;
using MeshWeaver.Charting.Builders.Chart;
using MeshWeaver.Charting.Builders.DataSetBuilders;
using MeshWeaver.Charting.Enums;
using MeshWeaver.Charting.Helpers;
using MeshWeaver.Charting.Models;
using MeshWeaver.Charting.Models.Options;
using MeshWeaver.Charting.Models.Options.Scales;
using MeshWeaver.Json.Assertions;

namespace MeshWeaver.Charting.Test;

public class ChartTests
{
    private JsonSerializerOptions Options =>
        new()
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault
        };

    [Fact]
    public async Task Bar_Chart_Basic()
    {
        double[] data = { 1, 2, 3, 4 };
        string[] labels = { "One", "Two", "Three", "Four" };

        var dataSet = (BarDataSet)new BarDataSetBuilder()
            .WithData(data)
            .Build();

        var chart3 = Charts
            .Bar([dataSet])
            .WithLabels(labels)
            .WithTitle("My First Chart", o => o.AtPosition(Positions.Left))
            .ToChart();

        await chart3.JsonShouldMatch(Options, "Bar_Chart_Basic.json");
    }

    [Fact]
    public async Task Line_Chart_Basic()
    {
        double[] data = { 1.0, 2.0, 3.0, 4.0 };
        string[] labels = { "One", "Two", "Three", "Four" };
        var dataSet = (LineDataSet)new LineDataSetBuilder()
            .WithData(data)
            .Smoothed()
            .Build();

        var chart3 = Charts
            .Line([dataSet])
            .WithTitle("My First Chart", o => o.AtPosition(Positions.Left))
            .WithLabels(labels)
            .ToChart();

        await chart3.JsonShouldMatch(Options, "Line_Chart_Basic.json");
    }

    [Fact]
    public async Task Line_Chart_Times()
    {
        string[] times = { "18-Sep-2019", "9-Oct-2019", "23-Sep-2019", "10-Nov-2019" };
        double[] data = { 1.0, 2.0, 3.0, 4.0 };

        var dataSet = (TimeLineDataSet)new TimeLineDataSetBuilder()
            .WithData(times, data)
            .Smoothed()
            .Build();

        var myChart = Charts
            .TimeLine([dataSet])
            .WithOptions(o => o.SetTimeUnit(TimeIntervals.Day).SetTimeFormat("D MMM YYYY"))
            .WithTitle("Timed Chart", o => o.AtPosition(Positions.Bottom).WithFontSize(20))
            .ToChart();

        await myChart.JsonShouldMatch(Options, "Line_Chart_Times.json");
    }

    [Fact]
    public async Task Bar_Chart_Stacked()
    {
        double[] data1 = { 2.0, 2.0, 3.0, 3.0 };
        double[] data2 = { 1.0, 4.0, 1.0, 2.0 };
        string[] labels = { "Jan", "Feb", "Mar", "Apr" };

        var dataSet1 = (BarDataSet)new BarDataSetBuilder()
            .WithData(data1)
            .WithLabel("One")
            .Build();
        var dataSet2 = (BarDataSet)new BarDataSetBuilder()
            .WithData(data2)
            .WithLabel("Two")
            .Build();

        var myChart = Charts
            .Bar([dataSet1, dataSet2])
            .Stacked()
            .WithLabels(labels)
            .WithTitle("Stacked", o => o.AtPosition(Positions.Top))
            .ToChart();

        await myChart.JsonShouldMatch(Options, "Bar_Chart_Stacked.json");
    }

    [Fact]
    public async Task Bar_Chart_NoAnimation()
    {
        double[] data1 = { 2.0, 2.0, 3.0, 3.0 };
        double[] data2 = { 1.0, 4.0, 1.0, 2.0 };
        string[] labels = { "Jan", "Feb", "Mar", "Apr" };

        var dataSet1 = (BarDataSet)new BarDataSetBuilder()
            .WithData(data1)
            .WithLabel("One")
            .Build();
        var dataSet2 = (BarDataSet)new BarDataSetBuilder()
            .WithData(data2)
            .WithLabel("Two")
            .Build();

        var myChart = Charts
            .Bar([dataSet1, dataSet2])
            .WithOptions(o => o.WithoutAnimation())
            .WithLabels(labels)
            .WithTitle("No animation", o => o.AtPosition(Positions.Top))
            .ToChart();

        await myChart.JsonShouldMatch(Options, "Bar_Chart_NoAnimation.json");
    }

    [Fact]
    public async Task EmptyBarChart()
    {
        var myChart = Charts
            .Bar([])
            .WithTitle("Empty Chart", o => o.AtPosition(Positions.Top))
            .ToChart();
        await myChart.JsonShouldMatch(Options, "Empty_Bar_Chart.json");
    }

    [Fact]
    public async Task Bar_Chart_Floating()
    {
        double[] low = { 2.0, 2.0, 3.0, 3.0 };
        double[] high = { 1.0, 4.0, 1.0, 2.0 };
        string[] labels = { "Jan", "Feb", "Mar", "Apr" };

        var dataSet = (FloatingBarDataSet)new FloatingBarDataSetBuilder()
            .WithDataRange(low, high)
            .Build();

        var chart3 = Charts
            .FloatingBar([dataSet])
            .WithLabels(labels)
            .WithTitle("Floating", o => o.AtPosition(Positions.Top))
            .ToChart();

        await chart3.JsonShouldMatch(Options, "Bar_Chart_Floating.json");
    }

    [Fact]
    public async Task Bar_Chart_Floating_Stacked()
    {
        var first = new List<int[]> { new[] { -2, 96 }, new[] { 30, 96 }, null, null };
        var second = new List<int[]> { null, new[] { -5, 20 }, new[] { 6, 12 }, new[] { 3, 6 } };

        string[] labels = { "Jan", "Feb", "Mar", "Apr" };

        var dataSet1 = (FloatingBarDataSet)new FloatingBarDataSetBuilder()
            .WithDataRange(first, "first")
            .Build();
        var dataSet2 = (FloatingBarDataSet)new FloatingBarDataSetBuilder()
            .WithDataRange(second, "second")
            .Build();

        var chart3 = Charts
            .FloatingBar([dataSet1, dataSet2])
            .Stacked()
            .WithLabels(labels)
            .WithTitle("Floating stacked", o => o.AtPosition(Positions.Top))
            .ToChart();

        await chart3.JsonShouldMatch(Options, "Bar_Chart_Floating_Stacked.json");
    }

    [Fact]
    public async Task Bar_Chart_Waterfall()
    {
        var deltas = new List<double> { 1, 4, -3, -2, 8, 11 };

        string[] labels = { "Jan", "Feb", "Mar", "Apr", "May", "June" };

        var chart3 = Charts
            .Bar([])
            .WithLabels(labels)
            .ToWaterfallChart(deltas, o => o
                .WithLegendItems("Increments", "Decrements", "Total")
                .WithConnectors()
            )
            .WithTitle("Waterfall", o => o.AtPosition(Positions.Top))
            .ToChart();

        await chart3.JsonShouldMatch(Options, "Bar_Chart_Waterfall.json");
    }

    [Fact]
    public async Task Bar_Chart_HorizontalWaterfall()
    {
        var deltas = new List<double> { 1, 4, -3, -2, 8, 11 };

        string[] labels = { "Jan", "Feb", "Mar", "Apr", "May", "June" };

        var chart3 = Charts
            .Bar([])
            .WithLabels(labels)
            .ToHorizontalWaterfallChart(deltas, o => o
                .WithLegendItems("Increments", "Decrements", "Total")
                .WithConnectors()
            )
            .WithTitle("Waterfall", o => o.AtPosition(Positions.Top))
            .ToChart();

        await chart3.JsonShouldMatch(Options, "Bar_Chart_HorizontalWaterfall.json");
    }

    [Fact]
    public async Task Bar_Chart_Waterfall_AutoTotal()
    {
        var deltas = new List<double> { 1, 4, -3, -2, 8, 11 };

        string[] labels = { "Jan", "Feb", "Mar", "Apr", "May", "June", "Total" };

        var chart3 = Charts
            .Bar([])
            .WithLabels(labels)
            .ToWaterfallChart(deltas, o => o
                .WithLegendItems("Increments", "Decrements", "Total")
                .WithConnectors()
                .WithLastAsTotal()
            )
            .WithTitle("Waterfall", o => o.AtPosition(Positions.Top))
            .ToChart();

        await chart3.JsonShouldMatch(Options, "Bar_Chart_Waterfall_AutoTotal.json");
    }

    [Fact]
    public async Task Bar_Chart_Waterfall_MarkedTotals()
    {
        var deltas = new List<double> { 50, 40, 30, 120, 10, 20, -50, 100 };

        string[] labels =
        {
            "Main1",
            "Delta1",
            "Delta2",
            "Middle",
            "Delta3",
            "Delta4",
            "Delta5",
            "Total"
        };

        var chart3 = Charts
            .Bar([])
            .WithLabels(labels)
            .ToWaterfallChart(deltas, o => o
                .WithTotalsAtPositions(0, 3, 7)
                .WithLegendItems("Increments", "Decrements", "Total")
                .WithConnectors(b => b.ThinLine())
            )
            .WithTitle("Waterfall", o => o.AtPosition(Positions.Top))
            .ToChart();

        await chart3.JsonShouldMatch(Options, "Bar_Chart_Waterfall_MarkedTotals.json");
    }

    [Fact]
    public async Task Bar_Chart_HorizontalWaterfall_MarkedTotals()
    {
        var deltas = new List<double> { 50, 40, 30, 120, 10, 20, -50, 100 };

        string[] labels =
        {
            "Main1",
            "Delta1",
            "Delta2",
            "Middle",
            "Delta3",
            "Delta4",
            "Delta5",
            "Total"
        };

        var chart3 = Charts
            .Bar([])
            .WithLabels(labels)
            .ToHorizontalWaterfallChart(deltas, o => o
                .WithTotalsAtPositions(0, 3, 7)
                .WithLegendItems("Increments", "Decrements", "Total")
                .WithConnectors()
            )
            .WithTitle("Waterfall", o => o.AtPosition(Positions.Top))
            .ToChart();

        await chart3.JsonShouldMatch(Options, "Bar_Chart_HorizontalWaterfall_MarkedTotals.json");
    }

    [Fact]
    public async Task Bar_Chart_Waterfall_StyledBars()
    {
        var deltas = new List<double> { 50, 40, 30, 120, 10, 20, -50, 100 };

        string[] labels =
        {
            "Main1",
            "Delta1",
            "Delta2",
            "Middle",
            "Delta3",
            "Delta4",
            "Delta5",
            "Total"
        };

        var chart3 = Charts
            .Bar([])
            .WithLabels(labels)
            .ToWaterfallChart(deltas, o => o
                .WithBarDataSetOptions(b => b.WithBarPercentage(1).WithCategoryPercentage(1))
                .WithTotalsAtPositions(0, 3, 7)
                .WithLegendItems("Increments", "Decrements", "Total")
                .WithConnectors(b => b.WithLineWidth(1).WithoutPoint())
            )
            .WithTitle("Waterfall", o => o.AtPosition(Positions.Top))
            .ToChart();

        await chart3.JsonShouldMatch(Options, "Bar_Chart_Waterfall_StyledBars.json");
    }

    [Fact]
    public async Task Bar_Chart_HorizontalWaterfall_AutoTotal()
    {
        var deltas = new List<double> { 1, 4, -3, -2, 8, 11 };

        string[] labels = { "Jan", "Feb", "Mar", "Apr", "May", "June", "Total" };

        var chart3 = Charts
            .Bar([])
            .WithLabels(labels)
            .ToHorizontalWaterfallChart(deltas, o => o
                .WithLastAsTotal()
                .WithLegendItems("Increments", "Decrements", "Total")
                .WithConnectors()
            )
            .WithTitle("Waterfall", o => o.AtPosition(Positions.Top))
            .ToChart();

        await chart3.JsonShouldMatch(Options, "Bar_Chart_HorizontalWaterfall_AutoTotal.json");
    }

    [Fact]
    public async Task Bar_Chart_Horizontal_Floating()
    {
        var first = new List<int[]> { new[] { -2, 96 }, new[] { 30, 96 }, null, null };
        var second = new List<int[]> { null, new[] { -5, 20 }, new[] { 6, 12 }, new[] { 3, 6 } };

        string[] labels = { "Jan", "Feb", "Mar", "Apr" };

        var dataSet1 = (HorizontalFloatingBarDataSet)new HorizontalFloatingBarDataSetBuilder()
            .WithDataRange(first, "first")
            .Build();
        var dataSet2 = (HorizontalFloatingBarDataSet)new HorizontalFloatingBarDataSetBuilder()
            .WithDataRange(second, "second")
            .Build();

        var chart3 = Charts
            .HorizontalFloatingBar([dataSet1,dataSet2])
            .WithLabels(labels)
            .WithTitle("Horizontal floating", o => o.AtPosition(Positions.Top))
            .ToChart();

        await chart3.JsonShouldMatch(Options, "Bar_Chart_Horizontal_Floating.json");
    }

    [Fact]
    public async Task Bar_Chart_Palette()
    {
        double[] data = { 1, 2, 3, 4 };
        string[] labels = { "One", "Two", "Three", "Four" };

        var dataSet = (BarDataSet)new BarDataSetBuilder()
            .WithData(data)
            .Build();

        var myChart = Charts
            .Bar([dataSet])
            .WithLabels(labels)
            .WithColorPalette(Palettes.Brewer.Paired10)
            .WithTitle("My First Chart", o => o.AtPosition(Positions.Left))
            .ToChart();

        await myChart.JsonShouldMatch(Options, "Bar_Chart_Palette.json");
    }

    [Fact]
    public async Task Bar_Chart_AutoLegend()
    {
        double[] data1 = { 1, 2, 3, 4 };
        double[] data2 = { 5, 6, 7, 8 };
        string[] labels = { "One", "Two", "Three", "Four" };

        var dataSet1 = (BarDataSet)new BarDataSetBuilder()
            .WithData(data1)
            .WithLabel("First")
            .Build();
        var dataSet2 = (BarDataSet)new BarDataSetBuilder()
            .WithData(data2)
            .WithLabel("Second")
            .Build();

        var myChart = Charts
            .Bar([dataSet1, dataSet2])
            .WithLabels(labels)
            .ToChart();

        await myChart.JsonShouldMatch(Options, "Bar_Chart_AutoLegend.json");

        var myChart2 = Charts
            .Bar([dataSet1, dataSet2])
            .WithLabels(labels)
            .WithTitle("My First Chart", o => o.AtPosition(Positions.Left))
            .ToChart();

        await myChart2.JsonShouldMatch(Options, "Bar_Chart_AutoLegend_WithTitle.json");
    }

    [Fact]
    public async Task Bar_Chart_SetMinimum()
    {
        int[] data = { 1, 2, 3, 4 };
        string[] labels = { "One", "Two", "Three", "Four" };

        var dataSet = (BarDataSet)new BarDataSetBuilder()
            .WithData(data)
            .WithLabel("First")
            .Build();

        var myChart = Charts
            .Bar([dataSet])
            .WithLabels(labels)
            .WithOptions(o => o.WithYAxisMin(-1))
            .ToChart();

        await myChart.JsonShouldMatch(Options, "Bar_Chart_SetMin.json");

        var myChart2 = Charts
            .Bar([dataSet])
            .WithLabels(labels)
            .WithOptions(o => o.WithYAxisMin(-1).WithYAxisMax(10).ShortenYAxisNumbers())
            .ToChart();

        await myChart2.JsonShouldMatch(Options, "Bar_Chart_SetMinMax.json");
    }

    [Fact]
    public async Task CustomPalette()
    {
        int[] data = { 1, 2, 3, 4 };
        string[] labels = { "One", "Two", "Three", "Four" };
        string[] myPalette = { "#00ff00", "#0000ff", "#ffff00", "#ff0000" };

        var dataSet = (PieDataSet)new PieDataSetBuilder()
            .WithData(data)
            .Build();

        var myChart = Charts
            .Pie([dataSet])
            .WithLabels(labels)
            .WithLegend()
            .WithColorPalette(myPalette);

        await myChart.ToChart().JsonShouldMatch(Options, "CustomPalette1.json");

        var myChartWithStringColor = myChart.WithColorPalette(Palettes.Brewer.YlGn8).ToChart();
        await myChartWithStringColor.JsonShouldMatch(Options, "CustomPalette2.json");
    }

    [Fact]
    public async Task QuickDraw()
    {
        double[] data1 = { -1.0, 4.0, 3.0, 2.0 };
        double[] data2 = { 4.0, 5.0, 6.0, 3.0 };
        double[] x1 = { 1.0, 4.0, 3.0, 2.0 };

        var dataSet1 = (BarDataSet)new BarDataSetBuilder()
            .WithData(data1)
            .Build();
        var dataSet2 = (BarDataSet)new BarDataSetBuilder()
            .WithData(data2)
            .Build();
        var dataSet3 = (BarDataSet)new BarDataSetBuilder()
            .WithData(x1)
            .Build();

        var actual = Charts.Bar([dataSet1, dataSet2, dataSet3]).ToChart();

        await actual.JsonShouldMatch(Options, "QuickDraw.json");
    }

    [Fact]
    public async Task AreaChart()
    {
        double[] data1 = { -1.0, 4.0, 3.0, 2.0 };
        double[] data2 = { 4.0, 5.0, 6.0, 3.0 };
        var dataSet1 = (LineDataSet)new LineDataSetBuilder()
            .WithData(data1)
            .WithArea()
            .Build();
        var dataSet2 = (LineDataSet)new LineDataSetBuilder()
            .WithData(data2)
            .WithArea()
            .Build();

        var actual = Charts
            .Line([dataSet1, dataSet2])
            .ToChart();

        await actual.JsonShouldMatch(Options, "AreaChart.json");
    }

    [Fact]
    public async Task Generate_BubbleChart_Generates_Valid_Chart()
    {
        var dataSet = (BubbleDataSet)new BubbleDataSetBuilder()
            .WithData([(20, 30, 15), (40, 10, 10)])
            .WithLabel("Bubble Dataset")
            .WithBackgroundColor(ChartColor.FromRgb(255, 99, 132))
            .WithHoverBackgroundColor(ChartColor.FromRgb(255, 99, 132))
            .Build();

        var actual = Charts
            .Bubble([dataSet])
            .ToChart();

        await actual.JsonShouldMatch(Options, "BubbleChart.json");
    }

    [Fact]
    public async Task Generate_LineChartScatter_Generates_Valid_Chart()
    {
        var dataSet = (LineScatterDataSet)new LineScatterDataSetBuilder()
            .WithDataPoint(new[] { (-10, 0), (0, 10), (10, 5) })
            .Build();

        var actual = Charts
            .Scatter([dataSet])
            .WithOptions(o =>
                o.WithScales(
                    (
                        "x",
                        new CartesianLinearScale
                        {
                            Type = "linear",
                            Position = "bottom",
                            BeginAtZero = true
                        }
                    )
                )
            )
            .ToChart();

        await actual.JsonShouldMatch(Options, "LineScatter.json");
    }

    [Fact]
    public async Task GenerateScatterWithLabels()
    {
        const int testSize = 15;
        double[] whys = new double[testSize];
        double[] exes = new double[testSize];
        for (int i = 0; i < testSize; i++)
        {
            whys[i] = Math.Floor((double)i / 5);
            exes[i] = i - 5 * Math.Floor((double)i / 5);
        }

        var dataSet = (LineScatterDataSet)new LineScatterDataSetBuilder()
            .WithDataPoint(exes, whys)
            .WithLabel("pp")
            .Build();

        var plot = Charts
            .Scatter([dataSet])
            .WithLegend(l =>
                l with
                {
                    Position = Positions.Bottom,
                    Labels = new LegendLabel() { BoxWidth = 3, BoxHeight = 3 }
                }
            )
            .ToChart();
        await plot.JsonShouldMatch(Options, $"{nameof(GenerateScatterWithLabels)}.json");
    }
}
