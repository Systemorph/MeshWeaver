using System.Collections.Immutable;
using MeshWeaver.Layout;

namespace MeshWeaver.Blazor.Components;

/// <summary>
/// Renders a <see cref="KpiStripControl"/> as a wrapping row of tiles. Pure projection — the tiles
/// carry already-formatted values, so this view lays out and themes, it does not compute.
/// </summary>
public partial class KpiStripView
{
    private ImmutableList<KpiItem> Items { get; set; } = [];
    private string MinTileWidth { get; set; } = DefaultMinTileWidth;

    private const string DefaultMinTileWidth = "150px";

    /// <inheritdoc />
    protected override void BindData()
    {
        base.BindData();
        if (ViewModel is null)
            return;

        DataBind(ViewModel.Items, x => x.Items,
            (value, _) => AnalysisRows.Resolve<KpiItem>(value, Hub.JsonSerializerOptions));
        DataBind(ViewModel.MinTileWidth, x => x.MinTileWidth, defaultValue: DefaultMinTileWidth);
    }
}
