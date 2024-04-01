using OpenSmc.Charting.Builders;
using OpenSmc.Charting.Builders.DataSetBuilders;
using OpenSmc.Charting.Enums;
using OpenSmc.Charting.Models;

namespace OpenSmc.Charting.Test;

public class ChartingSamples
{
    private readonly double[] data1 = { -1.0, 4.0, 3.0, 2.0 };
    private readonly double[] data2 = { 4.0, 5.0, 6.0, 3.0 };

    private readonly DateTime[] dates =
    {
        new(2020, 1, 1, 9, 0, 0),
        new(2020, 2, 1, 9, 0, 0),
        new(2020, 3, 1, 9, 0, 0),
        new(2020, 4, 1, 9, 0, 0)
    };

    private readonly double[] x1 = { 1.0, 4.0, 3.0, 2.0 };
    private readonly double[] x2 = { 10.0, 2.0, 7.0, 1.0 };
    private readonly double[] y = { 4.0, 1.0, 2.0, 3.0 };

    private readonly string[] labels = { "One", "two", "three", "four" };

    [Fact]
    public async Task BarChart()
    {
        var actual = ChartBuilder.Bar()
                          .WithDataSet(b => b.WithData(data1).WithLabel("First"))
                          .WithDataSet(b => b.WithData(data2).WithLabel("Second"))
                          .WithLabels(labels)
                          .WithLegend()
                          .WithTitle("Bar Chart")
                          .ToChart();

        await actual.JsonShouldMatch("Sample_BarChart.json");
    }

    [Fact]
    public async Task LineChart()
    {
        var actual = ChartBuilder.Line()
                          .WithDataSet(b => b.WithData(data1).WithLabel("First").Smoothed(0.3))
                          .WithDataSet(b => b.WithData(data2).WithLabel("Second").Smoothed())
                          .WithLabels(labels)
                          .WithLegend()
                          .WithTitle("Line Chart")
                          .ToChart();

        await actual.JsonShouldMatch("Sample_LineChart.json");
    }

    [Fact]
    public async Task MixedChart()
    {
        var actual = ChartBuilder.Bar()
                          .WithDataSet<LineDataSetBuilder, LineDataSet>(b => b.WithData(data1).WithLabel("First"))
                          .WithDataSet(b => b.WithData(data2).WithLabel("Second"))
                          .WithLabels(labels)
                          .WithLegend()
                          .WithTitle("Line Chart")
                          .ToChart();

        await actual.JsonShouldMatch("Sample_MixedChart.json");
    }

    [Fact]
    public async Task TimelineChart()
    {
        var actual = ChartBuilder.TimeLine()
                          .WithDataSet(b => b.WithData(dates, data1).WithLabel("First")
                                             .WithLineWidth(3)
                                             .WithoutPoint()
                                             .Smoothed())
                          .WithOptions(o => o.SetTimeUnit(TimeIntervals.Month)
                                             .ShortenYAxisNumbers()
                                             .SetTimeFormat("D MMM YYYY"))
                          .WithTitle("TimeLine Chart", o => o.WithFontSize(20))
                          .ToChart();

        await actual.JsonShouldMatch("Sample_TimeLineChart.json");
    }

    [Fact]
    public async Task FloatingBarChart()
    {
        var actual = ChartBuilder.FloatingBar()
                          .WithDataSet(b => b.WithDataRange(data1, data2)
                                             .WithLabel("First"))
                          .WithLabels(labels)
                          .WithTitle("FloatingBar Chart")
                          .ToChart();

        await actual.JsonShouldMatch("Sample_FloatingBarChart.json");
    }


    [Fact]
    public async Task BubbleChart()
    {
        double[] radius = { 8.0, 11.0, 20.0, 18.0 };
        var actual = ChartBuilder.Bubble()
                          .WithDataSet(b => b.WithData(x1, y, radius).WithLabel("First"))
                          .WithDataSet(b => b.WithData(x2, y, radius).WithLabel("Second"))
                          .WithLegend()
                          .WithTitle("Bubble Chart")
                          .WithColorPalette(Palettes.Brewer.DarkTwo8)
                          .ToChart();

        await actual.JsonShouldMatch("Sample_BubbleChart.json");
    }

    [Fact]
    public async Task PieChart()
    {
        var actual = ChartBuilder.Pie()
                          .WithData(data1)
                          .WithLabels(labels)
                          .WithLegend()
                          .WithTitle("Pie Chart")
                          .ToChart();

        await actual.JsonShouldMatch("Sample_PieChart.json");
    }

    [Fact]
    public async Task Doughnut()
    {
        var actual = ChartBuilder.Doughnut()
                          .WithData(data1)
                          .WithLabels(labels)
                          .WithLegend()
                          .WithTitle("Doughnut Chart")
                          .ToChart();

        await actual.JsonShouldMatch("Sample_Doughnut.json");
    }

    [Fact]
    public async Task Polar()
    {
        var actual = ChartBuilder.PolarArea()
                          .WithData(data1)
                          .WithLabels(labels)
                          .WithLegend()
                          .WithTitle("Polar Chart")
                          .ToChart();

        await actual.JsonShouldMatch("Sample_Polar.json");
    }

    [Fact]
    public async Task Radar()
    {
        var actual = ChartBuilder.Radar()
                          .WithDataSet(b => b.WithData(data1).WithLabel("First"))
                          .WithDataSet(b => b.WithData(data2).WithLabel("Second"))
                          .WithLabels(labels)
                          .WithLegend()
                          .WithTitle("Radar Chart")
                          .ToChart();

        await actual.JsonShouldMatch("Sample_Radar.json");
    }

    [Fact]
    public async Task Scatter()
    {
        var actual =
            ChartBuilder.Scatter()
                 .WithDataSet(b => b.WithDataPoint(x1, y).WithLabel("First"))
                 .WithDataSet(b => b.WithDataPoint(x2, y).WithLabel("Second"))
                 .WithLegend()
                 .WithTitle("Scatter Chart")
                 .WithColorPalette(Palettes.Brewer.DarkTwo8)
                 .ToChart();

        await actual.JsonShouldMatch("Sample_Scatter.json");
    }

    [Fact]
    public async Task QuickDraw()
    {
        var actual = ChartBuilder.Bar()
                          .WithData(data1)
                          .WithData(data2)
                          .WithData(x1)
                          .ToChart();

        await actual.JsonShouldMatch("Sample_QuickDraw.json");
    }

    [Fact]
    public async Task AreaChart()
    {
        var actual = ChartBuilder.Line()
                          .WithDataSet(b => b.WithData(data1).WithArea())
                          .WithDataSet(b => b.WithData(data2).WithArea())
                          .ToChart();

        await actual.JsonShouldMatch("Sample_AreaChart.json");
    }
}