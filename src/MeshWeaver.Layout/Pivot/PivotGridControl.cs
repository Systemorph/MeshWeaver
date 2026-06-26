namespace MeshWeaver.Layout.Pivot;

/// <summary>
/// UI control that renders a pivot (cross-tab) grid from <see cref="Data"/> according to
/// the supplied <see cref="Configuration"/>. Optionally shows a pager for large result sets.
/// </summary>
/// <param name="Data">The data source (observable or collection) to pivot.</param>
/// <param name="Configuration">The pivot configuration describing rows, columns, and measures.</param>
public record PivotGridControl(object Data, PivotConfiguration Configuration) : UiControl<PivotGridControl>("grid", "1.0")
{
    /// <summary>When <c>true</c>, a pager is rendered below the grid to navigate large result sets.</summary>
    public bool? ShowPager { get; init; }
    /// <summary>Number of rows displayed per page when <see cref="ShowPager"/> is enabled.</summary>
    public int? PageSize { get; init; }
}
