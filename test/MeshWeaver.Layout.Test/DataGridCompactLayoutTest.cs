using MeshWeaver.Layout.Composition;
using MeshWeaver.Layout.DataGrid;
using Xunit;

namespace MeshWeaver.Layout.Test;

/// <summary>
/// The compact (phone) layout of a grid (core MeshWeaver#7976): the properties a renderer reads, and the
/// prepared control — a compact grid must not carry the column-derived minimum width, or its box could
/// never shrink below the breakpoint (six columns × 120px = 720px against a 640px breakpoint).
/// </summary>
public class DataGridCompactLayoutTest
{
    private sealed record Probe() : DataGridControl(new object())
    {
        public DataGridControl Prepared(DataGridControl grid) => PrepareFor(grid);
        private static DataGridControl PrepareFor(DataGridControl grid) => new Probe() { Columns = grid.Columns, CompactBelow = grid.CompactBelow, Style = grid.Style }.Prepare();
        private DataGridControl Prepare() => PrepareRendering(new RenderingContext("grid"));
    }

    private static DataGridControl SixColumns(DataGridControl grid)
    {
        for (var i = 0; i < 6; i++)
            grid = grid.WithColumn(new PropertyColumnControl<string> { Property = $"p{i}" });
        return grid;
    }

    [Fact]
    public void ACompactGrid_HasNoColumnDerivedMinimumWidth()
    {
        var grid = SixColumns(new DataGridControl(new object())).WithCompactBelow(640);
        new Probe().Prepared(grid).Style.Should().BeNull("the phone layout needs a box narrower than 640px");
    }

    [Fact]
    public void ATableOnlyGrid_KeepsItsMinimumWidth()
        => new Probe().Prepared(SixColumns(new DataGridControl(new object()))).Style.Should().Be("min-width: 720px");

    [Fact]
    public void AnAuthoredStyle_IsKept()
        => new Probe().Prepared(SixColumns(new DataGridControl(new object())).WithCompactBelow(640).WithStyle("width: 100%")).Style
            .Should().Be("width: 100%");

    [Fact]
    public void CompactPlacements_AreCarriedOnTheColumns()
    {
        var column = new PropertyColumnControl<string> { Property = "when" }.WithCompact(ColumnCompactPlacement.Trail);
        column.Compact.Should().Be(ColumnCompactPlacement.Trail);
        new PropertyColumnControl<string> { Property = "x" }.Compact.Should().BeNull("unset reads as Body in every renderer");
        new DataGridControl(new object()).WithCompactBelow(640).CompactBelow.Should().Be(640);
    }
}
