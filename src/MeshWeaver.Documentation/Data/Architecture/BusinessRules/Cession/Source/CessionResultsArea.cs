// <meshweaver>
// Id: CessionResultsArea
// DisplayName: Cession Results Chart
// </meshweaver>

using System.Reactive.Linq;
using MeshWeaver.Charting;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;

/// <summary>
/// Layout area showing cession results: summary statistics + stacked bar chart.
/// Demonstrates reactive rendering from workspace data streams.
/// </summary>
public static class CessionResultsArea
{
    public static IObservable<UiControl> CessionResults(
        LayoutAreaHost host, RenderingContext ctx)
    {
        var layer = CessionSampleData.Layer;
        var results = CessionEngine.CedeIntoLayer(CessionSampleData.Claims, layer);

        var totalGross = results.Sum(r => r.GrossAmount);
        var totalCeded = results.Sum(r => r.CededAmount);
        var totalRetained = totalGross - totalCeded;
        var ratio = totalGross > 0 ? totalCeded / totalGross : 0;

        // Summary
        var stats = Controls.Markdown(
            $"**Layer:** {layer.Name} ({layer.Limit:N0} xs {layer.AttachmentPoint:N0})  \n" +
            $"**Claims:** {results.Length} | " +
            $"**Gross:** {totalGross:N0} | " +
            $"**Ceded:** {totalCeded:N0} | " +
            $"**Retained:** {totalRetained:N0} | " +
            $"**Ratio:** {ratio:P1}");

        // Stacked bar chart: retained (blue) + ceded (red) per claim
        var labels = results.Select(r => r.ClaimId);
        var retainedData = results.Select(r => r.RetainedAmount).ToArray();
        var cededData = results.Select(r => r.CededAmount).ToArray();

        var chart = Chart.Create(
                DataSet.Bar(retainedData).WithLabel("Retained").WithBackgroundColor("rgba(54, 162, 235, 0.7)"),
                DataSet.Bar(cededData).WithLabel("Ceded").WithBackgroundColor("rgba(255, 99, 132, 0.7)")
            )
            .WithLabels(labels)
            .WithOptions(o => o.Stacked())
            .WithTitle("Claims — Retained vs Ceded (XL 500k xs 200k)")
            .ToControl()
            .WithStyle("width: 100%; height: 400px;");

        return Observable.Return<UiControl>(
            Controls.Stack
                .WithView(Controls.Title("Reinsurance Cession Example", 3))
                .WithView(Controls.Markdown(
                    "Excess-of-Loss layer applied to motor claims. " +
                    "Claims below the **200k** attachment are fully retained. " +
                    "Claims above are ceded up to the **500k** limit."))
                .WithView(stats)
                .WithView(chart)
        );
    }
}
