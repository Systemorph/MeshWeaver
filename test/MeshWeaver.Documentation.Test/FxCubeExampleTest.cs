using System.Collections.Immutable;
using System.Linq;
using MeshWeaver.Documentation.DataCube;
using MeshWeaver.Layout.Chart;
using MeshWeaver.Layout.Pivot;
using Xunit;

namespace MeshWeaver.Documentation.Test;

/// <summary>
/// Tests for the data-cube example from the DataMesh documentation
/// (<c>Doc/DataMesh/DataCubes</c>) and the FutuRe sample
/// (<c>samples/Graph/Data/FutuRe/FxCube</c>). Pins the FX-conversion and
/// slice/dice numbers the doc's executable blocks display, and the shape of
/// the pivot-grid and chart controls the doc renders — so the page can never
/// silently drift from the code it demonstrates.
/// </summary>
public class FxCubeExampleTest
{
    // ── The cube and its FX conversion ─────────────────────────────────

    [Fact]
    public void Cube_Has18Facts_LocalGrandTotal()
    {
        FxCubeSampleData.Facts.Should().HaveCount(18);
        FxCube.GrandTotal(FxCubeSampleData.Facts).Should().Be(2_880_000);
    }

    [Fact]
    public void ConvertToGroupCurrency_PlanRates()
    {
        var chf = FxCube.ConvertToGroupCurrency(
            FxCubeSampleData.Facts, FxCubeSampleData.RatesToChf, FxMode.Plan);

        chf.Should().OnlyContain(f => f.Currency == "CHF");
        FxCube.GrandTotal(chf).Should().Be(2_690_000);   // 500k·1.0 + 960k·0.95 + 1,420k·0.90
    }

    [Fact]
    public void ConvertToGroupCurrency_ActualRates()
    {
        var chf = FxCube.ConvertToGroupCurrency(
            FxCubeSampleData.Facts, FxCubeSampleData.RatesToChf, FxMode.Actual);

        FxCube.GrandTotal(chf).Should().Be(2_642_400);   // 500k·1.0 + 960k·0.93 + 1,420k·0.88
    }

    // ── Slice & dice on the converted cube ─────────────────────────────

    [Fact]
    public void Slice_PlanChf_ByYear()
    {
        var chf = FxCube.ConvertToGroupCurrency(
            FxCubeSampleData.Facts, FxCubeSampleData.RatesToChf, FxMode.Plan);

        var byYear = FxCube.Slice(chf, f => f.Year);
        byYear["2024"].Should().Be(1_288_000);
        byYear["2025"].Should().Be(1_402_000);
    }

    [Fact]
    public void Slice_PlanChf_ByLineOfBusiness()
    {
        var chf = FxCube.ConvertToGroupCurrency(
            FxCubeSampleData.Facts, FxCubeSampleData.RatesToChf, FxMode.Plan);

        var byLob = FxCube.Slice(chf, f => f.LineOfBusiness);
        byLob["Property"].Should().Be(1_177_000);
        byLob["Casualty"].Should().Be(924_500);
        byLob["Specialty"].Should().Be(588_500);
    }

    [Fact]
    public void Slice_OriginalCurrency_ShowsTheCurrencySplit()
    {
        var byCurrency = FxCube.Slice(FxCubeSampleData.Facts, f => f.Currency);

        byCurrency["CHF"].Should().Be(500_000);
        byCurrency["EUR"].Should().Be(960_000);
        byCurrency["USD"].Should().Be(1_420_000);
    }

    [Fact]
    public void Dice_PropertyEur_ThenConvert()
    {
        // Dice down to the Property × EUR sub-cube (200k + 220k local) …
        var subCube = FxCube.Dice(FxCubeSampleData.Facts, lineOfBusiness: "Property", currency: "EUR");
        subCube.Should().HaveCount(2);
        FxCube.GrandTotal(subCube).Should().Be(420_000);

        // … then convert the sub-cube at plan rates: 420k × 0.95.
        var chf = FxCube.ConvertToGroupCurrency(subCube, FxCubeSampleData.RatesToChf, FxMode.Plan);
        FxCube.GrandTotal(chf).Should().Be(399_000);
    }

    // ── The controls the doc renders ───────────────────────────────────

    [Fact]
    public void ToPivotGrid_BuildsTheConfigurationTheDocShows()
    {
        var chf = FxCube.ConvertToGroupCurrency(
            FxCubeSampleData.Facts, FxCubeSampleData.RatesToChf, FxMode.Plan);

        var grid = chf.ToPivotGrid(pivot => pivot
            .GroupRowsBy(f => f.LineOfBusiness)
            .GroupColumnsBy(f => f.Year)
            .Aggregate(f => f.Amount, agg => agg.WithFunction(AggregateFunction.Sum))
            .WithRowTotals()
            .WithColumnTotals());

        ((object[])grid.Data).Should().HaveCount(18);

        grid.Configuration.RowDimensions.Should().ContainSingle()
            .Which.Field.Should().Be(nameof(FxCubeFact.LineOfBusiness));
        grid.Configuration.ColumnDimensions.Should().ContainSingle()
            .Which.Field.Should().Be(nameof(FxCubeFact.Year));

        var aggregate = grid.Configuration.Aggregates.Should().ContainSingle().Which;
        aggregate.Field.Should().Be(nameof(FxCubeFact.Amount));
        aggregate.Function.Should().Be(AggregateFunction.Sum);

        // The builder discovers every dimension-able field for the field picker.
        grid.Configuration.AvailableDimensions.Select(d => d.Field)
            .Should().Contain(nameof(FxCubeFact.LineOfBusiness))
            .And.Contain(nameof(FxCubeFact.Year))
            .And.Contain(nameof(FxCubeFact.Currency));
    }

    [Fact]
    public void SliceBy_ToStackedColumnChart_OneSeriesPerLineOfBusiness()
    {
        var chf = FxCube.ConvertToGroupCurrency(
            FxCubeSampleData.Facts, FxCubeSampleData.RatesToChf, FxMode.Plan);

        var chart = chf
            .SliceBy(f => f.Year)
            .SliceBy(f => f.LineOfBusiness)
            .ToStackedColumnChart(g => g.Sum(f => f.Amount));

        ((ImmutableList<string>)chart.Labels!).Should().Equal("2024", "2025");
        ((ImmutableList<ChartSeries>)chart.Series!).Should().HaveCount(3);
        ((bool)chart.IsStacked!).Should().BeTrue();
    }

    [Fact]
    public void SliceBy_ToPieChart_OrdersCurrenciesByValue()
    {
        var pie = FxCubeSampleData.Facts
            .SliceBy(f => f.Currency)
            .ToPieChart(g => g.Sum(f => f.Amount));

        // Pie slices order by value descending: USD 1,420k · EUR 960k · CHF 500k.
        ((ImmutableList<string>)pie.Labels!).Should().Equal("USD", "EUR", "CHF");
    }
}
