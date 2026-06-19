using MeshWeaver.Layout;
using MeshWeaver.Layout.DataGrid;
using MeshWeaver.Utils;
using Controls = MeshWeaver.Layout.Controls;

namespace MeshWeaver.AI;

/// <summary>
/// Renders the priced <see cref="TokenCostSummary.CostRow"/>s as a framework
/// <c>DataGrid</c> — the ONE place the cost overview turns rows into UI. Shared by
/// the thread Token-cost area (<c>ThreadLayoutAreas.TokenCostView</c>) and the Space
/// Token-cost settings tab (<c>TokenCostSettingsTab</c>), so neither hand-rolls a
/// table and the DataGrid generics stay OUT of the pure-data <see cref="TokenCostSummary"/>
/// (which must stay delegate/UI-free so its compile stays measurable).
/// </summary>
internal static class TokenCostGrid
{
    /// <summary>
    /// A <c>DataGrid</c> of the priced rows (Model · Tokens in · Tokens out · Cost ·
    /// Currency), or a "nothing yet" markdown line when empty.
    /// </summary>
    internal static UiControl CostGrid(IReadOnlyList<TokenCostSummary.CostRow> rows)
    {
        if (rows.Count == 0)
            return Controls.Markdown("No token usage recorded yet.");

        return Controls.DataGrid(rows)
            .WithColumn(new PropertyColumnControl<string>   { Property = nameof(TokenCostSummary.CostRow.Model).ToCamelCase() }.WithTitle("Model"))
            .WithColumn(new PropertyColumnControl<long>     { Property = nameof(TokenCostSummary.CostRow.InputTokens).ToCamelCase() }.WithTitle("Tokens in").WithFormat("N0"))
            .WithColumn(new PropertyColumnControl<long>     { Property = nameof(TokenCostSummary.CostRow.OutputTokens).ToCamelCase() }.WithTitle("Tokens out").WithFormat("N0"))
            .WithColumn(new PropertyColumnControl<decimal?> { Property = nameof(TokenCostSummary.CostRow.Cost).ToCamelCase() }.WithTitle("Cost").WithFormat("N4"))
            .WithColumn(new PropertyColumnControl<string>   { Property = nameof(TokenCostSummary.CostRow.Currency).ToCamelCase() }.WithTitle("Currency"));
    }
}
