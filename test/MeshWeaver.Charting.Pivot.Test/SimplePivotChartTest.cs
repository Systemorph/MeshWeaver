using System.Reactive.Linq;
using System.Text.Json;
using MeshWeaver.Charting.Enums;
using MeshWeaver.Data;
using MeshWeaver.DataCubes;
using MeshWeaver.Domain;
using MeshWeaver.Fixture;
using MeshWeaver.Json.Assertions;
using MeshWeaver.Messaging;
using MeshWeaver.Pivot.Builder;
using Xunit.Abstractions;

namespace MeshWeaver.Charting.Pivot.Test;

public record Name : Dimension
{
    public static Name[] Data =
    {
        new() { SystemName = "P", DisplayName = "Paolo" },
        new() { SystemName = "A", DisplayName = "Alessandro" }
    };
}

public record Country : Dimension
{
    public static Country[] Data =
    {
        new() { SystemName = "IT", DisplayName = "Italy" },
        new() { SystemName = "RU", DisplayName = "Russia" }
    };
}

public record RecordWithValues
{
    [NotVisible]
    [Dimension(typeof(Name))]
    [IdentityProperty]
    public string Name { get; init; }

    [NotVisible]
    [Dimension(typeof(Country))]
    [IdentityProperty]
    public string Country { get; init; }

    [IdentityProperty]
    [NotVisible]
    [Dimension(typeof(int), nameof(ValueIndex))]
    public int ValueIndex { get; init; }

    public double Value { get; init; }

    public RecordWithValues(string name, string country, int valueIndex, double value)
    {
        Name = name;
        Country = country;
        Value = value;
        ValueIndex = valueIndex;
    }
}

public class SimplePivotChartTest(ITestOutputHelper toh) : HubTestBase(toh)
{
    protected override MessageHubConfiguration ConfigureHost(MessageHubConfiguration configuration)
    {
        return base.ConfigureHost(configuration)
            .AddData(data =>
                data.FromConfigurableDataSource(
                    "Records",
                    dataSource =>
                        dataSource
                            .WithType<RecordWithValues>()
                            .WithType<Country>(type => type.WithInitialData(Country.Data))
                            .WithType<Name>(type => type.WithInitialData(Name.Data))
                )
            );
    }

    private static readonly RecordWithValues[] RecordsWithValues =
    {
        new("P", "IT", 1, 1.1),
        new("P", "IT", 2, 2.2),
        new("P", "IT", 3, 3.3),
        new("P", "IT", 4, 4.4),
        new("A", "IT", 1, -3.9),
        new("A", "IT", 2, 3.4),
        new("A", "IT", 3, 3),
        new("A", "IT", 4, 2.7),
        new("A", "RU", 1, 4.1),
        new("A", "RU", 2, 4),
        new("A", "RU", 3, -2),
        new("A", "RU", 4, 4),
        new("A", "RU", 5, 3),
        new("A", "RU", 6, 4),
        new("A", "RU", 7, 4)
    };

    private static readonly IDataCube<RecordWithValues> CubeWithValues =
        RecordsWithValues.ToDataCube();


    private JsonSerializerOptions Options => GetHost().JsonSerializerOptions;

    [Fact]
    public async Task BarChartAggregatedByCountry()
    {
        var charSlicedByName = await GetHost().GetWorkspace()
            .Pivot(CubeWithValues)
            
            .SliceColumnsBy(nameof(RecordWithValues.ValueIndex))
            .SliceRowsBy(nameof(Name))
            .ToBarChart()
            .Select(c => c.WithTitle("AggregateByCountry"))
            .FirstAsync();
        await charSlicedByName.JsonShouldMatch(
            Options,
            $"{nameof(BarChartAggregatedByCountry)}.json"
        );
    }

    [Fact]
    public async Task BarChartAggregatedByName()
    {
        var charSliceByCountry = await GetHost().GetWorkspace()
            .Pivot(CubeWithValues)
            .SliceColumnsBy(nameof(RecordWithValues.ValueIndex))
            .SliceRowsBy(nameof(Country))
            .ToBarChart()
            .FirstAsync();
        await charSliceByCountry.JsonShouldMatch(
            Options,
            $"{nameof(BarChartAggregatedByName)}.json"
        );
    }

    [Fact]
    public async Task StackedBarChartTestLabelSetting()
    {
        var charStacked = await GetHost().GetWorkspace()
            .Pivot(CubeWithValues)
            .SliceColumnsBy(nameof(RecordWithValues.ValueIndex))
            .SliceRowsBy(nameof(Name), nameof(Country))
            .ToBarChart(chart =>
                chart.AsStackedWithScatterTotals()
                    .WithOptions(m => m.WithLabelsFromLevels(0, 1))
            )
            .FirstAsync();
        await charStacked.JsonShouldMatch(
            Options,
            $"{nameof(StackedBarChartTestLabelSetting)}.json"
        );
    }

    [Fact]
    public async Task BarChartTestWithOption()
    {
        var doubleColumnSlice = await GetHost().GetWorkspace()
            .Pivot(CubeWithValues)
            .SliceColumnsBy(nameof(RecordWithValues.ValueIndex), nameof(Country))
            .ToBarChart(chart => chart.WithOptions(m => m))
            .FirstAsync();
        await doubleColumnSlice.JsonShouldMatch(Options, $"{nameof(BarChartTestWithOption)}.json");
    }

    [Fact]
    public async Task BarChartWithHierarchicalColumns()
    {
        var doubleColumnSliceTwoRows = await GetHost().GetWorkspace()
            .Pivot(CubeWithValues)
            .SliceColumnsBy(nameof(Country), nameof(RecordWithValues.ValueIndex))
            .SliceRowsBy(nameof(Name))
            .ToBarChart()
            .FirstAsync();
        await doubleColumnSliceTwoRows.JsonShouldMatch(
            Options,
            $"{nameof(BarChartWithHierarchicalColumns)}.json"
        );
    }

    [Fact]
    public async Task BarChartWithOneDefaultColumnReport()
    {
        var noColumnSlice = await GetHost().GetWorkspace()
            .Pivot(CubeWithValues)
            .SliceRowsBy(nameof(RecordWithValues.ValueIndex))
            .ToBarChart()
            .FirstAsync();
        await noColumnSlice.JsonShouldMatch(
            Options,
            $"{nameof(BarChartWithOneDefaultColumnReport)}.json"
        );
    }

    [Fact]
    public async Task StackedBarChartWithManyColumns()
    {
        var stackOneOne = await GetHost().GetWorkspace()
            .Pivot(CubeWithValues)
            .SliceColumnsBy(nameof(RecordWithValues.ValueIndex))
            .SliceRowsBy(nameof(Country))
            .ToBarChart(chart => chart.AsStackedWithScatterTotals())
            .FirstAsync();
        await stackOneOne.JsonShouldMatch(
            Options,
            $"{nameof(StackedBarChartWithManyColumns)}.json"
        );
    }

    [Fact]
    public async Task StackedBarPlotsWithRestrictedColumns()
    {
        var stackTwoTwo = await GetHost().GetWorkspace()
            .Pivot(CubeWithValues)
            .SliceColumnsBy(nameof(Name))
            .SliceRowsBy(nameof(Country))
            .ToBarChart(chart => chart.AsStackedWithScatterTotals())
            .FirstAsync();
        await stackTwoTwo.JsonShouldMatch(
            Options,
            $"{nameof(StackedBarPlotsWithRestrictedColumns)}.json"
        );
    }

    [Fact]
    public async Task BarChartWithRenaming()
    {
        var charSlicedByName = await GetHost().GetWorkspace()
            .Pivot(CubeWithValues)
            .SliceColumnsBy(nameof(RecordWithValues.ValueIndex))
            .SliceRowsBy(nameof(Country))
            .ToBarChart(chart =>
                chart.WithOptions(model =>
                    model
                        .WithLabels("A", "B", "C", "D", "E", "F", "G", "H")
                        .WithLegendItems("Country1", "Country2", "Country3")
                )
                .WithColorScheme(Palettes.Brewer.Blues3)
            )
            .FirstAsync();
        await charSlicedByName.JsonShouldMatch(Options, $"{nameof(BarChartWithRenaming)}.json");
    }

    [Fact]
    public async Task BarChartWithOptionsAndRenaming()
    {
        var charSliceByName = await GetHost().GetWorkspace()
            .Pivot(CubeWithValues)
            .SliceColumnsBy(nameof(RecordWithValues.ValueIndex))
            .SliceRowsBy(nameof(Name), nameof(Country))
            .ToBarChart(chart =>
                chart.WithOptions(model =>
                    (
                        model with
                        {
                            Rows = model
                                .Rows.Where(row => row.Descriptor.Coordinates.Count == 2)
                                .ToList()
                        }
                    ).WithLegendItemsFromLevels(".", 1, 0)
                )
            )
            .FirstAsync();
        await charSliceByName.JsonShouldMatch(
            Options,
            $"{nameof(BarChartWithOptionsAndRenaming)}.json"
        );
    }

    [Fact]
    public async Task MixedChart()
    {
        var mixedPlot1 = await GetHost().GetWorkspace()
            .Pivot(CubeWithValues)
            .SliceRowsBy(nameof(Name))
            .SliceColumnsBy(nameof(RecordWithValues.ValueIndex))
            .ToBarChart(chart => chart.WithRowsAsLine("Paolo"))
            .FirstAsync();
        await mixedPlot1.JsonShouldMatch(Options, $"{nameof(MixedChart)}.json");
    }

    [Fact]
    public async Task LineChart()
    {
        var linePlot1 = await GetHost().GetWorkspace()
            .Pivot(CubeWithValues)
            .SliceRowsBy(nameof(Name), nameof(Country))
            .SliceColumnsBy(nameof(RecordWithValues.ValueIndex))
            .ToLineChart(chart =>
                chart.WithSmoothedLines("Paolo.Italy")
                    .WithSmoothedLines(new Dictionary<string, double>() { { "Alessandro.Italy", 0.5 } })
                    .WithRows("Alessandro.Italy", "Paolo.Italy")
                    .WithOptions(model => model.WithLabels("8", "9", "10", "11", "12", "13", "14"))
            )
            .FirstAsync();
        await linePlot1.JsonShouldMatch(Options, $"{nameof(LineChart)}.json");
    }

    [Fact]
    public async Task SimpleRadarChart()
    {
        var radarChart = await GetHost().GetWorkspace()
            .Pivot(CubeWithValues)
            .SliceRowsBy((nameof(Name)))
            .SliceColumnsBy(nameof(RecordWithValues.ValueIndex))
            .ToRadarChart()
            .FirstAsync();
        await radarChart.JsonShouldMatch(Options, $"{nameof(SimpleRadarChart)}.json");
    }

    [Fact]
    public async Task RadarChartWithExtraOptions()
    {
        var radarChart = await GetHost().GetWorkspace()
            .Pivot(CubeWithValues)
            .SliceRowsBy(nameof(Name), nameof(Country))
            .SliceColumnsBy(nameof(RecordWithValues.ValueIndex))
            .ToRadarChart(chart =>
                chart.WithSmoothedLines(new Dictionary<string, double>() { { "Alessandro.Italy", 0.2 } })
                    .WithFilledArea()
                    .WithRows("Alessandro.Italy", "Paolo.Italy")
                    .WithColorScheme(new string[] { "#1ECBE1", "#E1341E" })
                    .WithTitle("Two lines radar plot", t => t.WithFontSize(15).AlignAtStart())
            )
            .FirstAsync();
        await radarChart.JsonShouldMatch(Options, $"{nameof(RadarChartWithExtraOptions)}.json");
    }

    [Fact]
    public async Task SimpleWaterfallChart()
    {
        var filteredCube = CubeWithValues;
        var waterfall = await GetHost().GetWorkspace()
            .Pivot(filteredCube)
            .SliceColumnsBy(nameof(Country), nameof(Name))
            .ToWaterfallChart(chart =>
                chart.WithStylingOptions(b =>
                    b.WithIncrementColor("#08BFD1")
                        .WithDecrementColor("#01AB6C")
                        .WithTotalColor("#A7E1ED")
                        .WithLabelsFontColor("white")
                        .WithLabelsFontSize(14)
                )
                .WithTotals(col => col.IsTotalForSlice(nameof(Country)))
            )
            .FirstAsync();
        await waterfall.JsonShouldMatch(Options, $"{nameof(SimpleWaterfallChart)}.json");
    }
}
