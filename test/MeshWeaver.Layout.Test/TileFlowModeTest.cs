using MeshWeaver.Data;
using Xunit;

namespace MeshWeaver.Layout.Test;

/// <summary>
/// The TILE-FLOW mode (Doc/GUI/LayoutGrid → "Tile flow"): a grid or a bound list whose children are
/// tiles of a minimum width that fill each row and wrap. The renderers draw it; what the control
/// tree must carry is the mode itself — on the grid's skin, and on the bound list, whose ROWS are
/// the tiles.
/// </summary>
public class TileFlowModeTest
{
    [Fact]
    public void LayoutGrid_WithMinTileWidth_SetsTheModeOnItsSkin()
    {
        var grid = Controls.LayoutGrid.WithMinTileWidth("280px");

        grid.Skin.MinTileWidth.Should().Be("280px");
        Controls.LayoutGrid.Skin.MinTileWidth.Should().BeNull("an undeclared grid keeps the 12-column breakpoint grid");
        grid.Should().NotBe(Controls.LayoutGrid, "the mode is part of the control's identity");
    }

    [Fact]
    public void LayoutGridSkin_WithMinTileWidth_KeepsTheOtherSettings()
    {
        var skin = new LayoutGridSkin().WithSpacing(4).WithMinTileWidth("12rem");

        skin.MinTileWidth.Should().Be("12rem");
        skin.Spacing.Should().Be(4);
    }

    /// <summary>
    /// <see cref="ItemTemplateControl"/> carries a hand-written <c>Equals</c>. A mode it leaves out
    /// compares EQUAL across a change, and the layout host's <c>DistinctUntilChanged</c> then drops
    /// the update — the list would keep rendering in its old layout.
    /// </summary>
    [Fact]
    public void ItemTemplate_MinTileWidth_IsPartOfItsEquality()
    {
        var view = Controls.Stack;
        var list = new ItemTemplateControl(view, new JsonPointerReference(""));
        var tiles = list.WithMinTileWidth("300px");

        tiles.MinTileWidth.Should().Be("300px");
        list.MinTileWidth.Should().BeNull("an undeclared list is a vertical list");
        tiles.Equals(list).Should().BeFalse("switching a list to tile flow is a change the client must see");
        tiles.Equals(list.WithMinTileWidth("300px")).Should().BeTrue();
        tiles.GetHashCode().Should().Be(list.WithMinTileWidth("300px").GetHashCode());
    }
}
