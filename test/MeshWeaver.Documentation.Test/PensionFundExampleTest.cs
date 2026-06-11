using System.Collections.Immutable;
using System.Linq;
using MeshWeaver.BusinessRules;
using MeshWeaver.Documentation.DataCube;
using MeshWeaver.Layout.Chart;
using MeshWeaver.Layout.Pivot;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Documentation.Test;

/// <summary>
/// Tests for the pension-fund data-cube example from the DataMesh
/// documentation (<c>Doc/DataMesh/DataCubes</c>) and the PensionFund sample
/// (<c>samples/Graph/Data/PensionFund</c>). The computed positions evaluate
/// through the REAL business-rules scopes — the <c>ScopeCodeGenerator</c>
/// emits the implementations at build time, exactly as the NodeType compiler
/// does for the sample's Code nodes — so every number the doc page shows is
/// pinned against the same engine that runs in the portal.
/// </summary>
public class PensionFundExampleTest
{
    private const string Ns = "PensionFund/Position";
    private const string Y2024 = "PensionFund/Year/2024";
    private const string Y2025 = "PensionFund/Year/2025";

    private static ScopeRegistry<BalanceSheetStorage> CreateRegistry()
        => new ServiceCollection()
            .AddBusinessRules(typeof(PositionValue).Assembly)
            .BuildServiceProvider()
            .CreateScopeRegistry(PensionFundSampleData.Storage);

    private static double Value(ScopeRegistry<BalanceSheetStorage> registry, string position, string year)
        => registry.GetScope<PositionValue>(new PositionYear($"{Ns}/{position}", year)).Value;

    // ── The model: 21 positions, 30 facts, no Id anywhere ──────────────

    [Fact]
    public void Model_AllMeshNodes_NoIdProperty()
    {
        PensionFundSampleData.Positions.Should().HaveCount(21);
        PensionFundSampleData.Positions.Values.Count(p => p.Aggregation == PositionAggregation.Atomic).Should().Be(15);
        PensionFundSampleData.Entries.Should().HaveCount(30);

        // The identity of a fact is its node path — the record has NO Id.
        typeof(BalanceSheetEntry).GetProperty("Id").Should().BeNull();

        // Dimension columns store dimension node PATHS.
        PensionFundSampleData.Entries.Should().OnlyContain(e =>
            e.Position.StartsWith("PensionFund/Position/")
            && e.Year.StartsWith("PensionFund/Year/")
            && e.Currency == "PensionFund/Currency/CHF");
    }

    // ── Atomic positions read the facts ────────────────────────────────

    [Fact]
    public void AtomicPosition_ReadsTheFactValue()
    {
        var registry = CreateRegistry();

        Value(registry, "Cash", Y2024).Should().Be(50);
        Value(registry, "Equities", Y2025).Should().Be(340);
        Value(registry, "PensionersCapital", Y2024).Should().Be(280);
    }

    // ── Computed positions: formulas modelled out of other positions ───

    [Fact]
    public void TotalAssets_SumsTheSixAssetAtoms()
    {
        var registry = CreateRegistry();

        Value(registry, "TotalAssets", Y2024).Should().Be(1_060);   // 50+400+300+200+100+10
        Value(registry, "TotalAssets", Y2025).Should().Be(1_142);   // 60+410+340+210+110+12
    }

    [Fact]
    public void BalanceSheet_Balances_BothYears()
    {
        var registry = CreateRegistry();

        Value(registry, "BalanceSheetSum", Y2024).Should().Be(1_060);
        Value(registry, "TotalLiabilities", Y2024).Should().Be(1_060);
        Value(registry, "BalanceSheetSum", Y2025).Should().Be(1_142);
        Value(registry, "TotalLiabilities", Y2025).Should().Be(1_142);
    }

    [Fact]
    public void PensionCapital_ActivesPlusPensionersPlusTechnicalProvisions()
    {
        var registry = CreateRegistry();

        Value(registry, "PensionCapital", Y2024).Should().Be(920);   // 600+280+40
        Value(registry, "PensionCapital", Y2025).Should().Be(964);   // 620+300+44
    }

    [Fact]
    public void AvailableAssets_SumWithNegativeWeights()
    {
        var registry = CreateRegistry();

        // TotalAssets − Payables − Accrued − EmployerContributionReserve − NonTechnical
        Value(registry, "AvailableAssets", Y2024).Should().Be(1_010);   // 1060−15−5−20−10
        Value(registry, "AvailableAssets", Y2025).Should().Be(1_084);   // 1142−18−6−22−12
    }

    [Fact]
    public void FundingRatio_RatioPosition_Bvv2Art44()
    {
        var registry = CreateRegistry();

        Value(registry, "FundingRatio", Y2024).Should().BeApproximately(1_010d / 920, 1e-12);   // ≈ 109.8%
        Value(registry, "FundingRatio", Y2025).Should().BeApproximately(1_084d / 964, 1e-12);   // ≈ 112.4%
    }

    // ── The summary scope the KeyFigures view binds to ──────────────────

    [Fact]
    public void BalanceSheetSummary_HeadlineFigures()
    {
        var registry = CreateRegistry();
        var summary2024 = registry.GetScope<BalanceSheetSummary>(Y2024);
        var summary2025 = registry.GetScope<BalanceSheetSummary>(Y2025);

        summary2024.BalanceSheetSum.Should().Be(1_060);
        summary2024.PensionCapital.Should().Be(920);
        summary2024.FundingRatio.Should().BeApproximately(1.0978, 1e-4);
        summary2024.Balances.Should().BeTrue();

        summary2025.BalanceSheetSum.Should().Be(1_142);
        summary2025.PensionCapital.Should().Be(964);
        summary2025.FundingRatio.Should().BeApproximately(1.1245, 1e-4);
        summary2025.Balances.Should().BeTrue();
    }

    [Fact]
    public void Scopes_AreCachedPerIdentity()
    {
        var registry = CreateRegistry();
        var scope = registry.GetScope<PositionValue>(new PositionYear($"{Ns}/TotalAssets", Y2024));
        var again = registry.GetScope<PositionValue>(new PositionYear($"{Ns}/TotalAssets", Y2024));

        again.Should().BeSameAs(scope);
    }

    // ── The controls the doc renders over the entry cube ───────────────

    [Fact]
    public void ToPivotGrid_PositionsByYear()
    {
        var grid = PensionFundSampleData.Entries.ToPivotGrid(pivot => pivot
            .GroupRowsBy(e => e.Position)
            .GroupColumnsBy(e => e.Year)
            .Aggregate(e => e.Amount, agg => agg.WithFunction(AggregateFunction.Sum))
            .WithRowTotals()
            .WithColumnTotals());

        ((object[])grid.Data).Should().HaveCount(30);
        grid.Configuration.RowDimensions.Should().ContainSingle()
            .Which.Field.Should().Be(nameof(BalanceSheetEntry.Position));
        grid.Configuration.ColumnDimensions.Should().ContainSingle()
            .Which.Field.Should().Be(nameof(BalanceSheetEntry.Year));
        grid.Configuration.Aggregates.Should().ContainSingle()
            .Which.Function.Should().Be(AggregateFunction.Sum);
    }

    [Fact]
    public void SliceBy_AssetAtoms_ToPieChart()
    {
        var registry = CreateRegistry();
        var assets = PensionFundSampleData.Positions
            .Where(p => p.Value is { Side: BalanceSheetSide.Assets, Aggregation: PositionAggregation.Atomic })
            .Select(p => new
            {
                Label = p.Key.Split('/')[^1],
                Value = Value(registry, p.Key.Split('/')[^1], Y2025),
            })
            .ToArray();

        var pie = assets
            .SliceBy(a => a.Label)
            .ToPieChart(g => g.Sum(a => a.Value));

        // Pie slices order by value descending: Bonds 410 · Equities 340 · …
        ((ImmutableList<string>)pie.Labels!).First().Should().Be("Bonds");
        assets.Sum(a => a.Value).Should().Be(1_142);
    }
}
