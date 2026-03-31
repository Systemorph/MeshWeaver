namespace MeshWeaver.Layout.Pivot;

public record PivotGridControl(object Data, PivotConfiguration Configuration) : UiControl<PivotGridControl>("grid", "1.0")
{
    public bool? ShowPager { get; init; }
    public int? PageSize { get; init; }
}
