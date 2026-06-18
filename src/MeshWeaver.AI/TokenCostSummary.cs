using System.Collections.Immutable;
using System.Reactive.Linq;
using System.Web;
using MeshWeaver.Data;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;

namespace MeshWeaver.AI;

/// <summary>
/// Shared building blocks for the token-cost summaries shown (a) on a Thread and
/// (b) on a Space's Settings page. Turns a per-model token aggregate
/// (<see cref="Thread.TokensByModel"/>, or a sum of many threads') into priced
/// rows and renders them as a compact HTML table. Pricing is resolved live from
/// <c>LanguageModel</c> nodes (explicit per-model prices) with a fall back to the
/// built-in <see cref="ModelPricing"/> defaults — so a price edit re-prices
/// historical usage and a never-configured model still shows a number.
/// </summary>
public static class TokenCostSummary
{
    /// <summary>One priced model row in a summary table.</summary>
    /// <param name="Model">Bare model id (e.g. <c>claude-opus-4-6</c>).</param>
    /// <param name="InputTokens">Cumulative input tokens for the model.</param>
    /// <param name="OutputTokens">Cumulative output tokens for the model.</param>
    /// <param name="Cost">Computed cost, or null when no price is known.</param>
    /// <param name="Currency">Currency the cost is in (defaults to USD).</param>
    public record CostRow(string Model, long InputTokens, long OutputTokens, decimal? Cost, string Currency);

    /// <summary>
    /// Builds priced rows from a per-model token aggregate, sorted by total tokens
    /// descending. Models with zero tokens are dropped.
    /// </summary>
    public static IReadOnlyList<CostRow> BuildRows(
        IReadOnlyDictionary<string, ModelTokenUsage> tokensByModel,
        Func<string, ModelPriceRate?> priceFor)
        => tokensByModel
            .Where(kv => kv.Value.InputTokens > 0 || kv.Value.OutputTokens > 0)
            .OrderByDescending(kv => (long)kv.Value.InputTokens + kv.Value.OutputTokens)
            .Select(kv =>
            {
                var rate = priceFor(kv.Key);
                return new CostRow(
                    kv.Key, kv.Value.InputTokens, kv.Value.OutputTokens,
                    rate?.Cost(kv.Value.InputTokens, kv.Value.OutputTokens),
                    rate?.Currency ?? "USD");
            })
            .ToList();

    /// <summary>
    /// Merges several per-model token tallies into one summed aggregate (used by
    /// the Space summary to fold every thread's <see cref="Thread.TokensByModel"/>
    /// together).
    /// </summary>
    public static ImmutableDictionary<string, ModelTokenUsage> Merge(
        IEnumerable<IReadOnlyDictionary<string, ModelTokenUsage>> tallies)
    {
        var acc = new Dictionary<string, ModelTokenUsage>(StringComparer.OrdinalIgnoreCase);
        foreach (var tally in tallies)
            foreach (var (model, usage) in tally)
                acc[model] = (acc.TryGetValue(model, out var existing) ? existing : new ModelTokenUsage())
                    .Add(usage.InputTokens, usage.OutputTokens);
        return acc.ToImmutableDictionary(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Live price resolver built from the accessible <c>LanguageModel</c> nodes:
    /// an explicit per-model price wins, else the <see cref="ModelPricing"/>
    /// default for the id. Emits the defaults-only resolver immediately so the
    /// table renders before the model query resolves.
    /// </summary>
    public static IObservable<Func<string, ModelPriceRate?>> ObservePriceResolver(IMeshService meshQuery)
        => meshQuery.Query<MeshNode>(
                MeshQueryRequest.FromQuery($"nodeType:{LanguageModelNodeType.NodeType}"))
            .Select(change =>
            {
                var byId = change.Items
                    .Select(n => n.Content as ModelDefinition)
                    .Where(d => d is not null)
                    .GroupBy(d => d!.Id, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(g => g.Key, g => g.First()!, StringComparer.OrdinalIgnoreCase);
                return (Func<string, ModelPriceRate?>)(id =>
                    ModelPricing.Resolve(id, byId.GetValueOrDefault(id)));
            })
            .Catch<Func<string, ModelPriceRate?>, Exception>(_ =>
                Observable.Return<Func<string, ModelPriceRate?>>(id => ModelPricing.Default(id)))
            .StartWith(id => ModelPricing.Default(id));

    /// <summary>
    /// Renders the priced rows as a compact HTML table (Model · Tokens in · Tokens
    /// out · Cost) with a totals footer grouped by currency. Returns an
    /// "empty"-state message when there are no rows.
    /// </summary>
    public static string RenderHtml(IReadOnlyList<CostRow> rows, string? emptyText = null)
    {
        if (rows.Count == 0)
            return "<p style=\"color: var(--neutral-foreground-hint); font-size: 0.9rem; margin: 0;\">"
                 + HttpUtility.HtmlEncode(emptyText ?? "No token usage recorded yet.")
                 + "</p>";

        const string cell = "padding: 4px 10px; text-align: right; white-space: nowrap;";
        const string cellL = "padding: 4px 10px; text-align: left; white-space: nowrap;";
        const string head = "padding: 4px 10px; text-align: right; font-weight: 600; "
                          + "border-bottom: 1px solid var(--neutral-stroke-divider); color: var(--neutral-foreground-hint);";

        var sb = new System.Text.StringBuilder();
        sb.Append("<table style=\"border-collapse: collapse; font-size: 0.85rem; min-width: 360px;\">");
        sb.Append("<thead><tr>")
          .Append($"<th style=\"{head.Replace("text-align: right", "text-align: left")}\">Model</th>")
          .Append($"<th style=\"{head}\">Tokens in</th>")
          .Append($"<th style=\"{head}\">Tokens out</th>")
          .Append($"<th style=\"{head}\">Cost</th>")
          .Append("</tr></thead><tbody>");

        foreach (var r in rows)
        {
            sb.Append("<tr>")
              .Append($"<td style=\"{cellL}\">{HttpUtility.HtmlEncode(r.Model)}</td>")
              .Append($"<td style=\"{cell}\">{r.InputTokens:N0}</td>")
              .Append($"<td style=\"{cell}\">{r.OutputTokens:N0}</td>")
              .Append($"<td style=\"{cell}\">{FormatCost(r.Cost, r.Currency)}</td>")
              .Append("</tr>");
        }
        sb.Append("</tbody>");

        // Totals footer — tokens always; cost per currency (omit unpriced rows).
        var totalIn = rows.Sum(r => r.InputTokens);
        var totalOut = rows.Sum(r => r.OutputTokens);
        var costByCurrency = rows
            .Where(r => r.Cost.HasValue)
            .GroupBy(r => r.Currency)
            .Select(g => (Currency: g.Key, Total: g.Sum(r => r.Cost!.Value)))
            .ToList();
        var totalCostText = costByCurrency.Count switch
        {
            0 => "—",
            1 => FormatCost(costByCurrency[0].Total, costByCurrency[0].Currency),
            _ => string.Join(" + ", costByCurrency.Select(c => FormatCost(c.Total, c.Currency)))
        };
        const string foot = "padding: 6px 10px; text-align: right; font-weight: 600; "
                          + "border-top: 1px solid var(--neutral-stroke-divider);";
        sb.Append("<tfoot><tr>")
          .Append($"<td style=\"{foot.Replace("text-align: right", "text-align: left")}\">Total</td>")
          .Append($"<td style=\"{foot}\">{totalIn:N0}</td>")
          .Append($"<td style=\"{foot}\">{totalOut:N0}</td>")
          .Append($"<td style=\"{foot}\">{totalCostText}</td>")
          .Append("</tr></tfoot>");

        sb.Append("</table>");
        return sb.ToString();
    }

    /// <summary>Formats a cost with its currency, trimming trailing zeros down to 2–4 dp.</summary>
    private static string FormatCost(decimal? cost, string currency)
    {
        if (cost is not { } c)
            return "—";
        // Small costs need more precision; large ones read better at 2 dp.
        var text = c < 1m ? c.ToString("0.####") : c.ToString("N2");
        return $"{text} {HttpUtility.HtmlEncode(currency)}";
    }
}
