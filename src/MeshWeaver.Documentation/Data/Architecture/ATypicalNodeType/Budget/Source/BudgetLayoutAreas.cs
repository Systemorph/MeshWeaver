// <meshweaver>
// Id: BudgetLayoutAreas
// DisplayName: Budget Views
// </meshweaver>
#nullable enable
using System;
using System.Reactive.Linq;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Mesh;

/// <summary>
/// The budget's views. The area reads the node's OWN stream and composes platform controls; the
/// composition itself is the pure <see cref="View"/>, so a test can check exactly what the area
/// renders without a mesh.
/// </summary>
public static class BudgetLayoutAreas
{
    /// <summary>The default area of a budget node.</summary>
    public const string SummaryArea = "Summary";

    /// <summary>
    /// Registers the budget's views. The landing area goes through <c>WithNodePage</c>, never a
    /// bare <c>WithView</c>: that is what gives the page its Type · Created · Updated line (the
    /// framework composes it), and a bare registration ships without one, invisibly.
    /// </summary>
    /// <param name="layout">The layout definition.</param>
    /// <returns>The layout definition with the views added.</returns>
    public static LayoutDefinition AddBudgetLayoutAreas(this LayoutDefinition layout) =>
        layout.WithNodePage(SummaryArea, Summary).WithDefaultArea(SummaryArea);

    /// <summary>
    /// The summary of this node's budget, live: every change to the node re-renders it. Reads the
    /// node through <c>GetMeshNodeStream()</c> (authoritative, never a query) and types the content
    /// with <c>ContentAs</c> (never a cast, which is a silent null for untyped JSON).
    /// </summary>
    /// <param name="host">The layout area host.</param>
    /// <param name="context">The rendering context.</param>
    /// <returns>The summary control, re-emitted on every change of the node.</returns>
    public static IObservable<UiControl?> Summary(LayoutAreaHost host, RenderingContext context)
    {
        var texts = BudgetTexts.For(host.ViewerLocale());
        var options = host.Hub.JsonSerializerOptions;
        return host.Workspace.GetMeshNodeStream()
            .Select(node => (UiControl?)View(node.ContentAs<Budget>(options), texts));
    }

    /// <summary>
    /// What the summary shows for <paramref name="budget"/>: the figures as Markdown and the
    /// utilisation as a progress bar — or a one-line notice when the node has no budget yet.
    /// </summary>
    /// <param name="budget">The node's content, or null when it has none.</param>
    /// <param name="texts">The viewer's text table.</param>
    /// <returns>The control.</returns>
    public static UiControl View(Budget? budget, BudgetTexts texts)
    {
        if (budget is null)
            return Controls.Markdown(texts.NoBudget);
        var utilisation = BudgetRules.Utilisation(budget) ?? 0m;
        var percent = (int)Math.Round(Math.Clamp(utilisation, 0m, 1m) * 100m, MidpointRounding.AwayFromZero);
        return Controls.Stack
            .WithView(Controls.Markdown(Figures(budget, texts)))
            .WithView(Controls.Progress(texts.Utilisation, percent));
    }

    /// <summary>The figures of a budget as Markdown, amounts formatted in the viewer's culture.</summary>
    /// <param name="budget">The budget.</param>
    /// <param name="texts">The viewer's text table.</param>
    /// <returns>The Markdown.</returns>
    public static string Figures(Budget budget, BudgetTexts texts)
    {
        var culture = texts.Culture;
        string Amount(decimal value) => $"{value.ToString("N2", culture)} {budget.Currency}";
        return $"### {budget.Purpose}\n\n"
            + $"- **{texts.Planned}:** {Amount(budget.Planned)}\n"
            + $"- **{texts.Spent}:** {Amount(budget.Spent)}\n"
            + $"- **{texts.Remaining}:** {Amount(BudgetRules.Remaining(budget))}\n"
            + $"- **{texts.Health}:** {texts.HealthLabel(BudgetRules.Health(budget))}\n";
    }
}
