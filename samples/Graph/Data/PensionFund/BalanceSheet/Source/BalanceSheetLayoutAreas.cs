// <meshweaver>
// Id: BalanceSheetLayoutAreas
// DisplayName: Balance Sheet Views
// </meshweaver>

using System.ComponentModel.DataAnnotations;
using System.Reactive.Linq;
using System.Text;
using System.Threading.Tasks;
using MeshWeaver.BusinessRules;
using MeshWeaver.Domain;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Chart;
using MeshWeaver.Layout.Composition;
using Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Draft record for the "New Entry" dialog: every dimension column is a
/// MeshNodePicker over the dimension NODES (queried by the dimension TYPE's
/// path), so capturing a fact means picking nodes.
/// </summary>
public record BalanceSheetEntryDraft
{
    [MeshNode("nodeType:PensionFund/Position")]
    [Display(Name = "Position")]
    public string? Position { get; init; }

    [MeshNode("nodeType:PensionFund/Year")]
    [Display(Name = "Year")]
    public string? Year { get; init; }

    [MeshNode("nodeType:PensionFund/Currency")]
    [Display(Name = "Currency")]
    public string? Currency { get; init; }

    [DisplayFormat(DataFormatString = "{0:N1}")]
    [Display(Name = "Amount (m)")]
    public double Amount { get; init; }
}

/// <summary>
/// Balance-sheet views: the statement itself (atomic + computed positions via
/// the PositionValue scopes), the funding-ratio KPIs, the asset allocation
/// chart, and the new-entry dialog with mesh-node pickers.
/// </summary>
[Display(GroupName = "Balance Sheet", Order = 100)]
public static class BalanceSheetLayoutAreas
{
    public static LayoutDefinition AddBalanceSheetLayoutAreas(this LayoutDefinition layout) =>
        layout
            .WithView(nameof(BalanceSheetStatement), BalanceSheetStatement)
            .WithView(nameof(KeyFigures), KeyFigures)
            .WithView(nameof(AssetAllocation), AssetAllocation)
            .WithView(nameof(NewEntryDialog), NewEntryDialog);

    private static ScopeRegistry<BalanceSheetStorage> CreateRegistry(
        LayoutAreaHost host, BalanceSheetStorage storage)
        => host.Hub.ServiceProvider.CreateScopeRegistry(storage);

    private static string Name(BalanceSheetStorage storage, string path)
        => path.Split('/')[^1];

    /// <summary>
    /// The balance sheet: assets and liabilities per year, with the computed
    /// positions (Total Assets, Pension Capital, Balance Sheet Sum, Funding
    /// Ratio) evaluated through the PositionValue scopes — the formulas live
    /// on the Position nodes, not in this view.
    /// </summary>
    [Display(Name = "Balance Sheet", GroupName = "Balance Sheet", Order = 1)]
    public static IObservable<UiControl> BalanceSheetStatement(this LayoutAreaHost host, RenderingContext ctx)
        => BalanceSheetData.LoadStorage(host.Workspace).Select(storage =>
        {
            var registry = CreateRegistry(host, storage);
            var years = storage.Years;
            if (years.Length == 0 || storage.Positions.Count == 0)
                return (UiControl)Controls.Markdown("*No balance-sheet data loaded yet.*");

            var sb = new StringBuilder();
            sb.Append("| Position |");
            foreach (var y in years) sb.Append($" {Name(storage, y)} |");
            sb.Append("\n");
            sb.Append("|---|");
            foreach (var _ in years) sb.Append("---:|");
            sb.Append("\n");

            void Row(string path, Position position, bool bold)
            {
                var label = bold ? $"**{path.Split('/')[^1]}**" : path.Split('/')[^1];
                sb.Append($"| {label} |");
                foreach (var y in years)
                {
                    var value = registry.GetScope<PositionValue>(new PositionYear(path, y)).Value;
                    var text = position.Aggregation == PositionAggregation.Ratio
                        ? $"{value:P1}"
                        : $"{value:N1}";
                    sb.Append(bold ? $" **{text}** |" : $" {text} |");
                }
                sb.Append("\n");
            }

            void Section(BalanceSheetSide side)
            {
                foreach (var path in storage.OrderedPositions
                             .Where(p => storage.Positions[p].Side == side))
                    Row(path, storage.Positions[path],
                        storage.Positions[path].Aggregation != PositionAggregation.Atomic);
            }

            sb.Append($"| **Assets** |{string.Concat(Enumerable.Repeat(" |", years.Length))}\n");
            Section(BalanceSheetSide.Assets);
            sb.Append($"| **Liabilities** |{string.Concat(Enumerable.Repeat(" |", years.Length))}\n");
            Section(BalanceSheetSide.Liabilities);
            sb.Append($"| **Computed** |{string.Concat(Enumerable.Repeat(" |", years.Length))}\n");
            Section(BalanceSheetSide.Computed);

            return (UiControl)Controls.Markdown($"### Balance Sheet (CHF m)\n\n{sb}");
        });

    /// <summary>Headline figures per year via the BalanceSheetSummary scope.</summary>
    [Display(Name = "Key Figures", GroupName = "Balance Sheet", Order = 2)]
    public static IObservable<UiControl> KeyFigures(this LayoutAreaHost host, RenderingContext ctx)
        => BalanceSheetData.LoadStorage(host.Workspace).Select(storage =>
        {
            var registry = CreateRegistry(host, storage);
            if (storage.Years.Length == 0)
                return (UiControl)Controls.Markdown("*No data.*");

            var sb = new StringBuilder("| | " + string.Join(" | ", storage.Years.Select(y => Name(storage, y))) + " |\n");
            sb.Append("|---|").Append(string.Concat(Enumerable.Repeat("---:|", storage.Years.Length))).AppendLine();
            string Fmt(Func<BalanceSheetSummary, double> f, string format) =>
                string.Join(" | ", storage.Years.Select(y => string.Format($"{{0:{format}}}", f(registry.GetScope<BalanceSheetSummary>(y)))));
            sb.AppendLine($"| Balance Sheet Sum | {Fmt(s => s.BalanceSheetSum, "N1")} |");
            sb.AppendLine($"| Pension Capital | {Fmt(s => s.PensionCapital, "N1")} |");
            sb.AppendLine($"| **Funding Ratio** | **{Fmt(s => s.FundingRatio, "P1")}** |");
            var balanced = storage.Years.All(y => registry.GetScope<BalanceSheetSummary>(y).Balances);
            sb.AppendLine($"\n{(balanced ? "✅ Balance sheet balances." : "⚠️ Assets ≠ liabilities — check the entries!")}");

            return (UiControl)Controls.Markdown($"### Key Figures (CHF m)\n\n{sb}");
        });

    /// <summary>Asset allocation of the latest year as a pie chart.</summary>
    [Display(Name = "Asset Allocation", GroupName = "Balance Sheet", Order = 3)]
    public static IObservable<UiControl> AssetAllocation(this LayoutAreaHost host, RenderingContext ctx)
        => BalanceSheetData.LoadStorage(host.Workspace).Select(storage =>
        {
            if (storage.Years.Length == 0)
                return (UiControl)Controls.Markdown("*No data.*");
            var registry = CreateRegistry(host, storage);
            var year = storage.Years[^1];

            var atoms = storage.OrderedPositions
                .Where(p => storage.Positions[p] is { Side: BalanceSheetSide.Assets, Aggregation: PositionAggregation.Atomic })
                .Select(p => (Label: p.Split('/')[^1],
                    Value: registry.GetScope<PositionValue>(new PositionYear(p, year)).Value))
                .ToArray();

            return (UiControl)Charts.Pie(atoms.Select(a => a.Value), atoms.Select(a => a.Label))
                .WithTitle($"Asset Allocation {year.Split('/')[^1]} (CHF m)");
        });

    /// <summary>
    /// Button opening the new-entry dialog: the Edit form over the draft whose
    /// dimension fields are MeshNodePickers over the dimension nodes.
    /// </summary>
    [Display(Name = "New Entry (dialog)", GroupName = "Balance Sheet", Order = 4)]
    public static UiControl NewEntryDialog(this LayoutAreaHost host, RenderingContext ctx)
        => Controls.Button("New balance sheet entry…")
            .WithClickAction(click =>
            {
                var dialog = Controls.Dialog(
                        click.Host.Edit(new BalanceSheetEntryDraft(), "newEntry"),
                        "New Balance Sheet Entry")
                    .WithSize("M")
                    .WithClosable(true);
                click.Host.UpdateArea(DialogControl.DialogArea, dialog);
                return Task.CompletedTask;
            });
}
