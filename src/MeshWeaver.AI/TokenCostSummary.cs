using System.Collections.Immutable;
using System.Reactive.Linq;
using MeshWeaver.Data;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;

namespace MeshWeaver.AI;

/// <summary>
/// Shared, pure-data building blocks for the token-cost summaries shown on a Thread
/// and on a Space's Settings page. Turns a per-model token aggregate
/// (<see cref="Thread.TokensByModel"/>, or a sum of many threads') into priced
/// <see cref="CostRow"/>s for a framework <c>DataGrid</c> (see
/// <see cref="TokenCostGrid"/>) — there is no hand-rolled HTML here. Pricing is
/// resolved live from <c>LanguageModel</c> nodes (explicit per-model prices),
/// falling back to the built-in <see cref="ModelPricing"/> defaults — so a price
/// edit re-prices historical usage and a never-configured model still shows a number.
///
/// <para>This type holds NO UI generics and NO delegate streams: it exposes the
/// accessible <c>LanguageModel</c> nodes as a plain <c>id → ModelDefinition</c>
/// dictionary (<see cref="ObserveModels"/>) and resolves prices inside the plain-loop
/// <see cref="BuildRows"/>. The earlier <c>IObservable&lt;Func&lt;string,
/// ModelPriceRate?&gt;&gt;</c> resolver stream was the MeshWeaver.AI compile-time
/// regression (e30e9b5f1); a nullable-returning-delegate stream blew the clean
/// compile from ~7s to &gt;7min. Keep this file delegate-free.</para>
/// </summary>
internal static class TokenCostSummary
{
    /// <summary>One priced model row for the cost DataGrid.</summary>
    /// <param name="Model">Bare model id (e.g. <c>claude-opus-4-6</c>).</param>
    /// <param name="InputTokens">Cumulative input tokens for the model.</param>
    /// <param name="OutputTokens">Cumulative output tokens for the model.</param>
    /// <param name="Cost">Computed cost, or null when no price is known.</param>
    /// <param name="Currency">Currency the cost is in (defaults to USD).</param>
    public record CostRow(string Model, long InputTokens, long OutputTokens, decimal? Cost, string Currency);

    /// <summary>The empty model lookup, emitted immediately and on error.</summary>
    private static readonly IReadOnlyDictionary<string, ModelDefinition> EmptyModels =
        ImmutableDictionary<string, ModelDefinition>.Empty;

    /// <summary>
    /// Live <c>id → ModelDefinition</c> lookup built from the accessible
    /// <c>LanguageModel</c> nodes. Emits the empty lookup immediately so the grid
    /// renders before the model query resolves; folds in the real models as the
    /// query streams. A plain foreach (not a LINQ <c>GroupBy/ToDictionary</c>) — the
    /// reactive delegate/LINQ shapes are what regressed the compile.
    /// </summary>
    public static IObservable<IReadOnlyDictionary<string, ModelDefinition>> ObserveModels(IMeshService meshQuery)
        => meshQuery.Query<MeshNode>(
                MeshQueryRequest.FromQuery($"nodeType:{LanguageModelNodeType.NodeType}"))
            .Select(change =>
            {
                var byId = new Dictionary<string, ModelDefinition>(StringComparer.OrdinalIgnoreCase);
                foreach (var node in change.Items)
                    if (node.Content is ModelDefinition def && !byId.ContainsKey(def.Id))
                        byId[def.Id] = def;
                return (IReadOnlyDictionary<string, ModelDefinition>)byId;
            })
            .Catch((Exception _) => Observable.Return(EmptyModels))
            .StartWith(EmptyModels);

    /// <summary>
    /// Builds priced rows from a per-model token aggregate, sorted by total tokens
    /// descending. Models with zero tokens are dropped. A plain loop (not a LINQ
    /// <c>Where/OrderByDescending/Select</c> chain over the dictionary) — that chain
    /// shape was the MeshWeaver.AI compile-time regression (e30e9b5f1); a loop binds
    /// in normal time. The price is resolved per row from the model node (explicit
    /// price wins) else the <see cref="ModelPricing"/> default for the id.
    /// </summary>
    public static IReadOnlyList<CostRow> BuildRows(
        IReadOnlyDictionary<string, ModelTokenUsage> tokensByModel,
        IReadOnlyDictionary<string, ModelDefinition> modelsById)
    {
        var rows = new List<CostRow>();
        foreach (var kv in tokensByModel)
        {
            var usage = kv.Value;
            if (usage.InputTokens <= 0 && usage.OutputTokens <= 0)
                continue;
            var rate = ModelPricing.Resolve(kv.Key, modelsById.GetValueOrDefault(kv.Key));
            rows.Add(new CostRow(
                kv.Key, usage.InputTokens, usage.OutputTokens,
                rate?.Cost(usage.InputTokens, usage.OutputTokens),
                rate?.Currency ?? "USD"));
        }
        rows.Sort(static (a, b) =>
            (b.InputTokens + b.OutputTokens).CompareTo(a.InputTokens + a.OutputTokens));
        return rows;
    }

    /// <summary>
    /// Merges several per-model token tallies into one summed aggregate (used by the
    /// Space summary to fold every thread's <see cref="Thread.TokensByModel"/> together).
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
}
