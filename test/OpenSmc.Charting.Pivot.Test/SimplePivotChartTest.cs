using OpenSmc.Charting.Enums;
using OpenSmc.DataCubes;
using OpenSmc.Domain.Abstractions;
using OpenSmc.Domain.Abstractions.Attributes;
using OpenSmc.Fixture;
using OpenSmc.Pivot.Builder;
using OpenSmc.TestDomain;
using Xunit;

namespace OpenSmc.Charting.Pivot.Test;

public record Name : Dimension
{
    public static Name[] Data =
    {
        new() {SystemName = "P", DisplayName = "Paolo"},
        new() {SystemName = "A", DisplayName = "Alessandro"}
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

public class SimplePivotChartTest
{
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

    private static readonly IDataCube<RecordWithValues> CubeWithValues  = RecordsWithValues.ToDataCube();

    [Fact]
    public async Task BarChartAggregatedByCountry()
    {
        var charSlicedByName =  PivotFactory.ForDataCube(CubeWithValues)
                                           .WithQuerySource(new StaticDataFieldQuerySource())
                                           .SliceColumnsBy(nameof(RecordWithValues.ValueIndex))
                                           .SliceRowsBy(nameof(Name))
                                           .ToBarChart()
                                           .WithTitle("AggregateByCountry")
                                           .Execute();
        await charSlicedByName.JsonShouldMatch($"{nameof(BarChartAggregatedByCountry)}.json");
    }

    [Fact]
    public async Task BarChartAggregatedByName()
    {
        var charSliceByCountry =  PivotFactory.ForDataCube(CubeWithValues)
                                                  .WithQuerySource(new StaticDataFieldQuerySource())
                                                  .SliceColumnsBy(nameof(RecordWithValues.ValueIndex))
                                                  .SliceRowsBy(nameof(Country))
                                                  .ToBarChart()
                                                  .Execute();
        await charSliceByCountry.JsonShouldMatch($"{nameof(BarChartAggregatedByName)}.json");
    }

    [Fact]
    public async Task StackedBarChartTestLabelSetting()
    {
        var charStacked =  PivotFactory.ForDataCube(CubeWithValues)
                                           .WithQuerySource(new StaticDataFieldQuerySource())
                                           .SliceColumnsBy(nameof(RecordWithValues.ValueIndex))
                                           .SliceRowsBy(nameof(Name), nameof(Country))
                                           .ToBarChart()
                                           .AsStackedWithScatterTotals()
                                           .WithOptions(m => m.WithLabelsFromLevels(0,1))
                                           .Execute();
        await charStacked.JsonShouldMatch($"{nameof(StackedBarChartTestLabelSetting)}.json");
    }

    [Fact]
    public async Task BarChartTestWithOption()
    {
        var doubleColumnSlice =  PivotFactory.ForDataCube(CubeWithValues)
                                                 .WithQuerySource(new StaticDataFieldQuerySource())
                                                 .SliceColumnsBy(nameof(RecordWithValues.ValueIndex), nameof(Country))
                                                 .ToBarChart()
                                                 .WithOptions(m => m)
                                                 .Execute();
        await doubleColumnSlice.JsonShouldMatch($"{nameof(BarChartTestWithOption)}.json");
    }

    [Fact]
    public async Task BarChartWithHierarchicalColumns()
    {
        var doubleColumnSliceTwoRows =  PivotFactory.ForDataCube(CubeWithValues)
                                                        .WithQuerySource(new StaticDataFieldQuerySource())
                                                        .SliceColumnsBy(nameof(Country), nameof(RecordWithValues.ValueIndex))
                                                        .SliceRowsBy(nameof(Name))
                                                        .ToBarChart()
                                                        .Execute();
        await doubleColumnSliceTwoRows.JsonShouldMatch($"{nameof(BarChartWithHierarchicalColumns)}.json");
    }

    [Fact]
    public async Task BarChartWithOneDefaultColumnReport()
    {
        var noColumnSlice =  PivotFactory.ForDataCube(CubeWithValues)
                                             .WithQuerySource(new StaticDataFieldQuerySource())
                                             .SliceRowsBy(nameof(RecordWithValues.ValueIndex))
                                             .ToBarChart()
                                             .Execute();
        await noColumnSlice.JsonShouldMatch($"{nameof(BarChartWithOneDefaultColumnReport)}.json");
    }

    [Fact]
    public async Task StackedBarChartWithManyColumns()
    {
        var stackOneOne =  PivotFactory.ForDataCube(CubeWithValues)
                                           .WithQuerySource(new StaticDataFieldQuerySource())
                                           .SliceColumnsBy(nameof(RecordWithValues.ValueIndex))
                                           .SliceRowsBy(nameof(Country))
                                           .ToBarChart()
                                           .AsStackedWithScatterTotals()
                                           .Execute();
        await stackOneOne.JsonShouldMatch($"{nameof(StackedBarChartWithManyColumns)}.json");
    }

    [Fact]
    public async Task StackedBarPlotsWithRestrictedColumns()
    {
        var stackTwoTwo =  PivotFactory.ForDataCube(CubeWithValues)
                                           .WithQuerySource(new StaticDataFieldQuerySource())
                                           .SliceColumnsBy(nameof(Name))
                                           .SliceRowsBy(nameof(Country))
                                           .ToBarChart()
                                           .AsStackedWithScatterTotals()
                                           .Execute();
        await stackTwoTwo.JsonShouldMatch($"{nameof(StackedBarPlotsWithRestrictedColumns)}.json");
    }

    [Fact]
    public async Task BarChartWithRenaming()
    {
        var charSlicedByName =  PivotFactory.ForDataCube(CubeWithValues)
                                                .WithQuerySource(new StaticDataFieldQuerySource())
                                                .SliceColumnsBy(nameof(RecordWithValues.ValueIndex))
                                                .SliceRowsBy(nameof(Country))
                                                .ToBarChart()
                                                .WithOptions(model => model.WithLabels("A", "B", "C", "D", "E", "F", "G", "H")
                                                                           .WithLegendItems("Country1", "Country2", "Country3"))
                                                .WithColorScheme(Palettes.Brewer.Blues3)
                                                .Execute();
        await charSlicedByName.JsonShouldMatch($"{nameof(BarChartWithRenaming)}.json");
    }

    [Fact]
    public async Task BarChartWithOptionsAndRenaming()
    {
        var charSliceByName =  PivotFactory.ForDataCube(CubeWithValues)
                                               .WithQuerySource(new StaticDataFieldQuerySource())
                                               .SliceColumnsBy(nameof(RecordWithValues.ValueIndex))
                                               .SliceRowsBy(nameof(Name), nameof(Country))
                                               .ToBarChart()
                                               .WithOptions(model => (model with
                                                                      {
                                                                          Rows = model.Rows
                                                                                      .Where(row => row.Descriptor.Coordinates.Count == 2)
                                                                                      .ToList()
                                                                      }).WithLegendItemsFromLevels(".", 1, 0))
                                               .Execute();
        await charSliceByName.JsonShouldMatch($"{nameof(BarChartWithOptionsAndRenaming)}.json");
    }

    [Fact]
    public async Task MixedChart()
    {
        var mixedPlot1 =  PivotFactory.ForDataCube(CubeWithValues)
                                          .WithQuerySource(new StaticDataFieldQuerySource())
                                          .SliceRowsBy(nameof(Name))
                                          .SliceColumnsBy(nameof(RecordWithValues.ValueIndex))
                                          .ToBarChart()
                                          .WithRowsAsLine("Paolo")
                                          .Execute();
        await mixedPlot1.JsonShouldMatch($"{nameof(MixedChart)}.json");
    }

    [Fact]
    public async Task LineChart()
    {
        var linePlot1 =  PivotFactory.ForDataCube(CubeWithValues)
                                         .WithQuerySource(new StaticDataFieldQuerySource())
                                         .SliceRowsBy(nameof(Name), nameof(Country))
                                         .SliceColumnsBy(nameof(RecordWithValues.ValueIndex))
                                         .ToLineChart()
                                         .WithSmoothedLines("Paolo.Italy")
                                         .WithSmoothedLines(new Dictionary<string, double>(){{"Alessandro.Italy", 0.5}})
                                         .WithRows("Alessandro.Italy", "Paolo.Italy")
                                         .WithOptions(model => model.WithLabels("8", "9", "10", "11", "12", "13", "14"))
                                         .Execute();
        await linePlot1.JsonShouldMatch($"{nameof(LineChart)}.json");
    }

    [Fact]
    public async Task SimpleRadarChart()
    {
        var radarChart =  PivotFactory.ForDataCube(CubeWithValues)
                                     .WithQuerySource(new StaticDataFieldQuerySource())
                                     .SliceRowsBy((nameof(Name)))
                                     .SliceColumnsBy(nameof(RecordWithValues.ValueIndex))
                                     .ToRadarChart()
                                     .Execute();
        await radarChart.JsonShouldMatch($"{nameof(SimpleRadarChart)}.json");
    }

    [Fact]
    public async Task RadarChartWithExtraOptions()
    {
        var radarChart =  PivotFactory.ForDataCube(CubeWithValues)
                                     .WithQuerySource(new StaticDataFieldQuerySource())
                                     .SliceRowsBy(nameof(Name), nameof(Country))
                                     .SliceColumnsBy(nameof(RecordWithValues.ValueIndex))
                                     .ToRadarChart()
                                     .WithSmoothedLines(new Dictionary<string, double>() { { "Alessandro.Italy", 0.2 } })
                                     .WithFilledArea()
                                     .WithRows("Alessandro.Italy", "Paolo.Italy")
                                     .WithColorScheme(new string[] { "#1ECBE1", "#E1341E" })
                                     .WithTitle("Two lines radar plot", t => t.WithFontSize(15).AlignAtStart())
                                     .Execute();
        await radarChart.JsonShouldMatch($"{nameof(RadarChartWithExtraOptions)}.json");
    }

    [FactWithWorkItem("26433")]
    public async Task SimpleWaterfallChart()
    {
        var filteredCube = CubeWithValues;
            //.Filter(x => x.Country == "RU" && x.Name == "A");
        var waterfall =  PivotFactory.ForDataCube(filteredCube)
                                          .WithQuerySource(new StaticDataFieldQuerySource())
                                          .SliceColumnsBy(nameof(Country), nameof(Name))
                                          .ToWaterfallChart()
                                          .WithStylingOptions(b => b.IncrementColor("#08BFD1")
                                                                    .DecrementColor("#01AB6C")
                                                                    .TotalColor("#A7E1ED")
                                                                    .LabelsFontColor("white")
                                                                    .LabelsFontSize(14))
                                          .WithTotals(col =>  col.IsTotalForSlice(nameof(Country)))
                                          //.WithLegendItems("Increments", "Decrements", "Total")
                                          .Execute();
        await waterfall.JsonShouldMatch($"{nameof(SimpleWaterfallChart)}.json");
    }
}
