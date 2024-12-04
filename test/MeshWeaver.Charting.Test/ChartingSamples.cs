using System.Text.Json;
using System.Text.Json.Serialization;
using MeshWeaver.Charting.Builders;
using MeshWeaver.Charting.Builders.DataSetBuilders;
using MeshWeaver.Charting.Enums;
using MeshWeaver.Charting.Models;
using MeshWeaver.Charting.Models.Bubble;
using MeshWeaver.Charting.Models.Line;
using MeshWeaver.Charting.Models.Polar;
using MeshWeaver.Charting.Models.Radar;
using MeshWeaver.Charting.Models.Segmented;
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
        var dataSet1 = (BarDataSet)new BarDataSetBuilder()
            .WithData(data1)
            .WithLabel("First")
            .Build();
        var dataSet2 = (BarDataSet)new BarDataSetBuilder()
            .WithData(data2)
            .WithLabel("Second")
            .Build();

        var actual = Charts
            .Bar([dataSet1, dataSet2])
            .WithLabels(labels)
            .WithLegend()
            .WithTitle("Bar Chart");

        await actual.JsonShouldMatch(Options, "Sample_BarChart.json");
    }

    [Fact]
    public async Task LineChart()
    {
        var dataSet1 = (LineDataSet)new LineDataSetBuilder()
            .WithData(data1)
            .WithLabel("First")
            .Smoothed(0.3)
            .Build();
        var dataSet2 = (LineDataSet)new LineDataSetBuilder()
            .WithData(data2)
            .WithLabel("Second")
            .Smoothed()
            .Build();

        var actual = Charts
            .Line([dataSet1, dataSet2])
            .WithLabels(labels)
            .WithLegend()
            .WithTitle("Line Chart");

        await actual.JsonShouldMatch(Options, "Sample_LineChart.json");
    }

    [Fact]
    public async Task MixedChart()
    {
        var dataSet1 = (LineDataSet)new LineDataSetBuilder()
            .WithData(data1)
            .WithLabel("First")
            .SetType(ChartType.Line)
            .Build();
        var dataSet2 = (BarDataSet)new BarDataSetBuilder()
            .WithData(data2)
            .WithLabel("Second")
            .Build();

        var actual = Charts
            .Bar([])
            .WithDataSet(dataSet1)
            .WithDataSet(dataSet2)
            .WithLabels(labels)
            .WithLegend()
            .WithTitle("Line Chart");

        await actual.JsonShouldMatch(Options, "Sample_MixedChart.json");
    }

    [Fact]
    public async Task TimelineChart()
    {
        var dataSet = (TimeLineDataSet)new TimeLineDataSetBuilder()
            .WithData(dates, data1)
            .WithLabel("First")
            .WithLineWidth(3)
            .WithoutPoint()
            .Smoothed()
            .Build();

        var actual = Charts
            .TimeLine([dataSet])
            .WithOptions(o =>
                o.SetTimeUnit(TimeIntervals.Month).ShortenYAxisNumbers().SetTimeFormat("D MMM YYYY")
            )
            .WithTitle("TimeLine Chart", o => o.WithFontSize(20));

        await actual.JsonShouldMatch(Options, "Sample_TimeLineChart.json");
    }

    [Fact]
    public async Task FloatingBarChart()
    {
        var dataSet = (FloatingBarDataSet)new FloatingBarDataSetBuilder()
            .WithDataRange(data1, data2)
            .WithLabel("First")
            .Build();

        var actual = Charts
            .FloatingBar([dataSet])
            .WithLabels(labels)
            .WithTitle("FloatingBar Chart");

        await actual.JsonShouldMatch(Options, "Sample_FloatingBarChart.json");
    }

    [Fact]
    public async Task BubbleChart()
    {
        double[] radius = { 8.0, 11.0, 20.0, 18.0 };
        var dataSet1 = (BubbleDataSet)new BubbleDataSetBuilder()
            .WithData(x1, y, radius)
            .WithLabel("First")
            .Build();
        var dataSet2 = (BubbleDataSet)new BubbleDataSetBuilder()
            .WithData(x2, y, radius)
            .WithLabel("Second")
            .Build();

        var actual = Charts
            .Bubble([dataSet1, dataSet2])
            .WithLegend()
            .WithTitle("Bubble Chart")
            .WithColorPalette(Palettes.Brewer.DarkTwo8);

        await actual.JsonShouldMatch(Options, "Sample_BubbleChart.json");
    }

    [Fact]
    public async Task PieChart()
    {
        var dataSet = (PieDataSet)new PieDataSetBuilder()
            .WithData(data1)
            .Build();

        var actual = Charts
            .Pie([dataSet])
            .WithLabels(labels)
            .WithLegend()
            .WithTitle("Pie Chart");

        await actual.JsonShouldMatch(Options, "Sample_PieChart.json");
    }

    [Fact]
    public async Task Doughnut()
    {
        var dataSet = (DoughnutDataSet)new DoughnutDataSetBuilder()
            .WithData(data1)
            .Build();

        var actual = Charts
            .Doughnut([dataSet])
            .WithLabels(labels)
            .WithLegend()
            .WithTitle("Doughnut Chart");

        await actual.JsonShouldMatch(Options, "Sample_Doughnut.json");
    }

    [Fact]
    public async Task Polar()
    {
        var dsBuilder = new PolarDataSetBuilder();
        var dsBuilderq = dsBuilder.WithData(data1);
        var dataSet = (PolarDataSet)dsBuilderq.Build();

        var actual = Charts
            .PolarArea([dataSet])
            .WithLabels(labels)
            .WithLegend()
            .WithTitle("Polar Chart");

        await actual.JsonShouldMatch(Options, "Sample_Polar.json");
    }

    [Fact]
    public async Task Radar()
    {
        var dataSet1 = (RadarDataSet)new RadarDataSetBuilder()
            .WithData(data1)
            .WithLabel("First")
            .Build();
        var dataSet2 = (RadarDataSet)new RadarDataSetBuilder()
            .WithData(data2)
            .WithLabel("Second")
            .Build();

        var actual = Charts
            .Radar([dataSet1, dataSet2])
            .WithLabels(labels)
            .WithLegend()
            .WithTitle("Radar Chart");

        await actual.JsonShouldMatch(Options, "Sample_Radar.json");
    }

    [Fact]
    public async Task Scatter()
    {
        var dataSet1 = (LineScatterDataSet)new LineScatterDataSetBuilder()
            .WithDataPoint(x1, y)
            .WithLabel("First")
            .Build();
        var dataSet2 = (LineScatterDataSet)new LineScatterDataSetBuilder()
            .WithDataPoint(x2, y)
            .WithLabel("Second")
            .Build();

        var actual = Charts
            .Scatter([dataSet1, dataSet2])
            .WithLegend()
            .WithTitle("Scatter Chart")
            .WithColorPalette(Palettes.Brewer.DarkTwo8);

        await actual.JsonShouldMatch(Options, "Sample_Scatter.json");
    }

    [Fact]
    public async Task QuickDraw()
    {
        var dataSet1 = (BarDataSet)new BarDataSetBuilder()
            .WithData(data1)
            .Build();
        var dataSet2 = (BarDataSet)new BarDataSetBuilder()
            .WithData(data2)
            .Build();
        var dataSet3 = (BarDataSet)new BarDataSetBuilder()
            .WithData(x1)
            .Build();

        var actual = Charts.Bar([dataSet1, dataSet2, dataSet3]);

        await actual.JsonShouldMatch(Options, "Sample_QuickDraw.json");
    }

    [Fact]
    public async Task AreaChart()
    {
        var dataSet1 = (LineDataSet)new LineDataSetBuilder()
            .WithData(data1)
            .WithArea()
            .Build();
        var dataSet2 = (LineDataSet)new LineDataSetBuilder()
            .WithData(data2)
            .WithArea()
            .Build();

        var actual = Charts
            .Line([dataSet1, dataSet2]);

        await actual.JsonShouldMatch(Options, "Sample_AreaChart.json");
    }
}
