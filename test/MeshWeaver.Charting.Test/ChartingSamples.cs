using System.Text.Json;
using System.Text.Json.Serialization;
using MeshWeaver.Charting.Enums;
using MeshWeaver.Charting.Models;
using MeshWeaver.Charting.Models.Bar;
using MeshWeaver.Charting.Models.Line;
using MeshWeaver.Json.Assertions;

namespace MeshWeaver.Charting.Test;

public class ChartingSamples
{
    private readonly double[] data1 = { -1.0, 4.0, 3.0, 2.0 };
    private readonly double[] data2 = { 4.0, 5.0, 6.0, 3.0 };

    private JsonSerializerOptions Options =>
        new()
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault
        };

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
        var actual = Chart
            .Create(DataSet.Bar(data1, "First"), DataSet.Bar(data2, "Second"))
            .WithLabels(labels)
            .WithLegend()
            .WithTitle("Bar Chart");

        await actual.JsonShouldMatch(Options, "Sample_BarChart.json");
    }

    [Fact]
    public async Task LineChart()
    {
        var actual = Chart
            .Create(DataSet.Line(data1, "First"), DataSet.Line(data2, "Second"))
            .WithLabels(labels)
            .WithLegend()
            .WithTitle("Line Chart");

        await actual.JsonShouldMatch(Options, "Sample_LineChart.json");
    }

    [Fact]
    public async Task MixedChart()
    {

        var actual = Chart.Create(DataSet.Line(data1, "First"), DataSet.Bar(data2, "Second"))
            .WithLabels(labels)
            .WithLegend()
            .WithTitle("Line Chart");

        await actual.JsonShouldMatch(Options, "Sample_MixedChart.json");
    }

    [Fact]
    public async Task TimelineChart()
    {
        var actual = Chart
            .TimeLine(dates, data1)
            .WithOptions(o =>
                o.SetTimeUnit(TimeIntervals.Month).ShortenYAxisNumbers().SetTimeFormat("D MMM YYYY")
            )
            .WithTitle("TimeLine Chart", o => o.WithFontSize(20));

        await actual.JsonShouldMatch(Options, "Sample_TimeLineChart.json");
    }

    [Fact]
    public async Task FloatingBarChart()
    {
        var actual = Chart
            .FloatingBar(data1, data2, "First")
            .WithLabels(labels)
            .WithTitle("FloatingBar Chart");

        await actual.JsonShouldMatch(Options, "Sample_FloatingBarChart.json");
    }

    [Fact]
    public async Task BubbleChart()
    {
        double[] radius = { 8.0, 11.0, 20.0, 18.0 };

        var actual = Chart
            .Create(DataSet.Bubble(x1, y, radius, "First"), DataSet.Bubble(x2,y,radius, "Second"))
            .WithLegend()
            .WithTitle("Bubble Chart")
            .WithColorPalette(Palettes.Brewer.DarkTwo8);

        await actual.JsonShouldMatch(Options, "Sample_BubbleChart.json");
    }

    [Fact]
    public async Task PieChart()
    {
        var actual = Chart
            .Pie(data1)
            .WithLabels(labels)
            .WithLegend()
            .WithTitle("Pie Chart");

        await actual.JsonShouldMatch(Options, "Sample_PieChart.json");
    }

    [Fact]
    public async Task Doughnut()
    {
        var actual = Chart
            .Doughnut(data1)
            .WithLabels(labels)
            .WithLegend()
            .WithTitle("Doughnut Chart");

        await actual.JsonShouldMatch(Options, "Sample_Doughnut.json");
    }

    [Fact]
    public async Task Polar()
    {
        var actual = Chart
            .Polar(data1)
            .WithLabels(labels)
            .WithLegend()
            .WithTitle("Polar Chart");

        await actual.JsonShouldMatch(Options, "Sample_Polar.json");
    }

    [Fact]
    public async Task Radar()
    {
        var actual = Chart
            .Create(DataSet.Radar(data1, "First"), DataSet.Radar(data2, "Second"))
            .WithLabels(labels)
            .WithLegend()
            .WithTitle("Radar Chart");

        await actual.JsonShouldMatch(Options, "Sample_Radar.json");
    }

    [Fact]
    public async Task Scatter()
    {
        var actual = Chart
            .Create(
                DataSet.Scatter(x1, y)
                    .WithLabel("First"),
                DataSet.Scatter(x2, y)
                    .WithLabel("Second")
            )
            .WithLegend()
            .WithTitle("Scatter Chart")
            .WithColorPalette(Palettes.Brewer.DarkTwo8);

        await actual.JsonShouldMatch(Options, "Sample_Scatter.json");
    }

    [Fact]
    public async Task QuickDraw()
    {
        var actual = Chart.Bar(data1, data2, x1);

        await actual.JsonShouldMatch(Options, "Sample_QuickDraw.json");
    }

    [Fact]
    public async Task AreaChart()
    {
        var actual = Chart
            .Create(new LineDataSet(data1).WithArea(), new LineDataSet(data2).WithArea())
            ;

        await actual.JsonShouldMatch(Options, "Sample_AreaChart.json");
    }
}
