// <meshweaver>
// Id: CessionResultsArea
// DisplayName: Cession Results Chart
// </meshweaver>

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Chart;
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

        // Column chart: retained + ceded per claim — framework ChartControl (Radzen-rendered).
        var labels = results.Select(r => $"{r.ClaimId}").ToArray();
        var retainedData = results.Select(r => (object)r.RetainedAmount);
        var cededData = results.Select(r => (object)r.CededAmount);

        var chart = Charts.Create()
            .WithSeries(new ColumnSeries(retainedData, "Retained"))
            .WithSeries(new ColumnSeries(cededData, "Ceded"))
            .WithLabels(labels)
            .WithTitle("Claims — Retained vs Ceded (XL 500k xs 200k)");

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
