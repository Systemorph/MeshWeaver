// <meshweaver>
// Id: FxCubeLayoutAreas
// DisplayName: FX Cube Views
// </meshweaver>

using System.ComponentModel.DataAnnotations;
using System.Reactive.Linq;
using MeshWeaver.Domain;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Chart;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Layout.Pivot;

/// <summary>
/// Draft record for the "New Fact" dialog. The [Dimension] columns render as
/// dropdowns; the [MeshNode] property renders a MeshNodePicker over the
/// FutuRe/LineOfBusiness dimension NODES — dimensions are mesh nodes, so
/// picking one is picking a node path.
/// </summary>
public record FxCubeFactDraft
{
    [MeshNode("nodeType:FutuRe/LineOfBusiness")]
    [Display(Name = "Line of Business (mesh node)")]
    public string? LineOfBusinessPath { get; init; }

    [UiControl<SelectControl>(Options = new[] { "2024", "2025" })]
    public string Year { get; init; } = "2025";

    [UiControl<SelectControl>(Options = new[] { "CHF", "EUR", "USD" })]
    public string Currency { get; init; } = "CHF";

    [DisplayFormat(DataFormatString = "{0:N0}")]
    public double Amount { get; init; }
}

/// <summary>
/// Slice-and-dice views over the FX cube: pivot tables (plan/actual CHF),
/// stacked and pie charts, the Edit form, and a dialog hosting the
/// mesh-node-picker draft form.
/// </summary>
[Display(GroupName = "FX Cube", Order = 100)]
public static class FxCubeLayoutAreas
{
    public static LayoutDefinition AddFxCubeLayoutAreas(this LayoutDefinition layout) =>
        layout
            .WithView(nameof(PlanChfPivot), PlanChfPivot)
            .WithView(nameof(ActualChfPivot), ActualChfPivot)
            .WithView(nameof(PlanChfByYearStacked), PlanChfByYearStacked)
            .WithView(nameof(CurrencySplit), CurrencySplit)
            .WithView(nameof(EditFact), EditFact)
            .WithView(nameof(NewFactDialog), NewFactDialog);

    private static IObservable<IReadOnlyCollection<FxCubeFact>> GetFacts(LayoutAreaHost host)
        => host.Workspace.GetStream<FxCubeFact>()!
            .Select(facts => (IReadOnlyCollection<FxCubeFact>)(facts?.ToArray() ?? []));

    /// <summary>
    /// Slice & dice table: rows by Line of Business, columns by Year,
    /// amounts converted to CHF at PLAN rates. Drag further dimensions in
    /// via the built-in field picker.
    /// </summary>
    [Display(Name = "Plan (CHF) Pivot", GroupName = "FX Cube", Order = 1)]
    public static IObservable<UiControl> PlanChfPivot(this LayoutAreaHost host, RenderingContext ctx)
        => GetFacts(host).Select(facts =>
            (UiControl)FxCubeEngine.ConvertToGroupCurrency(facts, FxMode.Plan)
                .ToPivotGrid(pivot => pivot
                    .GroupRowsBy(f => f.LineOfBusiness)
                    .GroupColumnsBy(f => f.Year)
                    .Aggregate(f => f.Amount, agg => agg.WithFunction(AggregateFunction.Sum))
                    .WithRowTotals()
                    .WithColumnTotals())
                with { Style = "width: 100%;" });

    /// <summary>Same pivot at ACTUAL rates — compare with the plan view.</summary>
    [Display(Name = "Actual (CHF) Pivot", GroupName = "FX Cube", Order = 2)]
    public static IObservable<UiControl> ActualChfPivot(this LayoutAreaHost host, RenderingContext ctx)
        => GetFacts(host).Select(facts =>
            (UiControl)FxCubeEngine.ConvertToGroupCurrency(facts, FxMode.Actual)
                .ToPivotGrid(pivot => pivot
                    .GroupRowsBy(f => f.LineOfBusiness)
                    .GroupColumnsBy(f => f.Year)
                    .Aggregate(f => f.Amount, agg => agg.WithFunction(AggregateFunction.Sum))
                    .WithRowTotals()
                    .WithColumnTotals())
                with { Style = "width: 100%;" });

    /// <summary>
    /// Stacked column chart: x-axis Year, one stacked series per Line of
    /// Business, plan-CHF amounts.
    /// </summary>
    [Display(Name = "Plan CHF by Year", GroupName = "FX Cube", Order = 3)]
    public static IObservable<UiControl> PlanChfByYearStacked(this LayoutAreaHost host, RenderingContext ctx)
        => GetFacts(host).Select(facts =>
            (UiControl)FxCubeEngine.ConvertToGroupCurrency(facts, FxMode.Plan)
                .SliceBy(f => f.Year)
                .SliceBy(f => f.LineOfBusiness)
                .ToStackedColumnChart(g => g.Sum(f => f.Amount))
                .WithTitle("Plan (CHF) by Year and Line of Business"));

    /// <summary>Pie of the ORIGINAL currency split — before any conversion.</summary>
    [Display(Name = "Currency Split", GroupName = "FX Cube", Order = 4)]
    public static IObservable<UiControl> CurrencySplit(this LayoutAreaHost host, RenderingContext ctx)
        => GetFacts(host).Select(facts =>
            (UiControl)facts
                .SliceBy(f => f.Currency)
                .ToPieChart(g => g.Sum(f => f.Amount))
                .WithTitle("Local Amounts by Currency"));

    /// <summary>
    /// The standard Edit form over a fact — the [Dimension] columns render
    /// as dropdowns, Amount as a number field. No per-field UI code.
    /// </summary>
    [Display(Name = "Edit a Fact", GroupName = "FX Cube", Order = 5)]
    public static UiControl EditFact(this LayoutAreaHost host, RenderingContext ctx)
        => host.Edit(FxCubeData.Facts[0], "editFact");

    /// <summary>
    /// Button opening a modal dialog whose content is the Edit form over
    /// <see cref="FxCubeFactDraft"/> — including the MeshNodePicker bound to
    /// the FutuRe/LineOfBusiness dimension nodes.
    /// </summary>
    [Display(Name = "New Fact (dialog)", GroupName = "FX Cube", Order = 6)]
    public static UiControl NewFactDialog(this LayoutAreaHost host, RenderingContext ctx)
        => Controls.Button("New fact…")
            .WithClickAction(click =>
            {
                var dialog = Controls.Dialog(
                        click.Host.Edit(new FxCubeFactDraft(), "newFact"),
                        "New FX Cube Fact")
                    .WithSize("M")
                    .WithClosable(true);
                click.Host.UpdateArea(DialogControl.DialogArea, dialog);
                return Task.CompletedTask;
            });
}
