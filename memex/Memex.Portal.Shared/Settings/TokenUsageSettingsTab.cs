using System.Collections.Immutable;
using System.Globalization;
using System.Reactive.Linq;
using System.Text.Json;
using MeshWeaver.AI;
using MeshWeaver.Application.Styles;
using MeshWeaver.Data;
using MeshWeaver.Graph;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Layout.DataGrid;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using MeshWeaver.Utils;

namespace Memex.Portal.Shared.Settings;

/// <summary>
/// Admin settings tab: aggregated token usage + estimated cost, read from the per-model
/// <see cref="TokenUsage"/> satellites ({thread}/_Usage/{model}). Filterable by time window and
/// groupable by model / person / thread. Cost is derived on read — the price authored on each
/// model's catalog node first, the built-in <see cref="ModelPricing.Defaults"/> table as fallback
/// (<see cref="ModelPriceCatalog"/>) — and never stored, so a price edit re-prices history.
///
/// <para>Gated like the other Administration tabs via <c>AdminMenuGate.IsPlatformAdmin</c>; the
/// query is RLS-scoped to what the viewer can read. Rendered with framework controls
/// (<see cref="DataGridControl"/> + a button toolbar) — no hand-built HTML.</para>
/// </summary>
public static class TokenUsageSettingsTab
{
    public const string TabId = "TokenUsage";
    private const string FilterDataId = "tokenUsageFilter";

    // Immutable lookups (constants, never written at runtime) — the grouping + time-window choices.
    private static readonly (string Key, string Label)[] Groupings =
        [("Model", "By model"), ("Person", "By person"), ("Thread", "By thread")];
    private static readonly (int Days, string Label)[] Windows =
        [(7, "Last 7 days"), (30, "Last 30 days"), (90, "Last 90 days"), (0, "All time")];

    private static FilterState Default => new("Model", 30);

    public static MessageHubConfiguration AddTokenUsageSettingsTab(this MessageHubConfiguration config)
        => config.AddGlobalSettingsMenuItems(new GlobalSettingsMenuItemProvider(GetTab));

    private static IObservable<IReadOnlyList<GlobalSettingsMenuItemDefinition>> GetTab(
        LayoutAreaHost host, RenderingContext ctx)
    {
        var tab = new GlobalSettingsMenuItemDefinition(
            Id: TabId,
            Label: "Token Usage",
            ContentBuilder: BuildContent,
            Group: "Administration",
            Icon: FluentIcons.Database(),
            GroupIcon: FluentIcons.Shield(),
            Order: 320)
            { LabelKey = "settings.tokenUsage", GroupKey = "settings.groupAdministration" };

        return AdminMenuGate.IsPlatformAdmin(host)
            .Select(isAdmin => isAdmin
                ? (IReadOnlyList<GlobalSettingsMenuItemDefinition>)new[] { tab }
                : []);
    }

    internal static UiControl BuildContent(LayoutAreaHost host, StackControl stack)
    {
        var ws = host.Hub.GetWorkspace();
        var jsonOptions = ws.Hub.JsonSerializerOptions;
        host.UpdateData(FilterDataId, Default);

        stack = stack.WithView(Controls.H2(host.Localize("settings.tokenUsage")).WithStyle("margin: 0 0 4px 0;"));
        stack = stack.WithView(Controls.Markdown(
            "Aggregated token usage and estimated cost from the per-model `_Usage` satellites " +
            "(`{thread}/_Usage/{model}`). Cost uses the price on each model's catalog node — the " +
            "built-in price table backs anything unpriced — and re-prices automatically when a " +
            "price changes. Cache reads/writes bill at their reduced/premium rates. Scope follows " +
            "your read access."));

        // Filter toolbar (grouping + time window) — reactive so the active choice stays highlighted.
        stack = stack.WithView((h, _) =>
            h.Stream.GetDataStream<FilterState>(FilterDataId)
                .StartWith(Default)
                .Select(f => (UiControl?)BuildToolbar(f)));

        // Live SHARED model catalog → authored prices. Only the platform catalog is queryable from
        // an admin view (a user's BYO models live in their own partition); those fall back to the
        // built-in table, same as before. Seeded + caught so a catalog hiccup can never blank the
        // grid — pricing degrades, usage still renders.
        var prices = ws.GetQuery("tokenusage:prices", AgentPickerProjection.BuildModelQueries())
            .Select(nodes => (IReadOnlyDictionary<string, ModelPriceRate>)
                ModelPriceCatalog.FromNodes(nodes, jsonOptions))
            .Catch(Observable.Return((IReadOnlyDictionary<string, ModelPriceRate>)
                ImmutableDictionary<string, ModelPriceRate>.Empty))
            .StartWith(ImmutableDictionary<string, ModelPriceRate>.Empty);

        // The grid: query × filter × prices, recomputed reactively on any change (never .Take(1) on a live feed).
        stack = stack.WithView((h, _) =>
            ws.GetQuery("tokenusage:list", $"nodeType:{TokenUsageNodeType.NodeType}")
                .CombineLatest(
                    h.Stream.GetDataStream<FilterState>(FilterDataId).StartWith(Default),
                    prices,
                    (nodes, filter, catalog) => (UiControl?)BuildGrid(nodes, filter, jsonOptions, catalog, locale: host.ViewerLocale())));

        return stack;
    }

    private static UiControl BuildToolbar(FilterState f, string? locale = null)
    {
        var bar = Controls.Stack.WithOrientation(Orientation.Horizontal)
            .WithStyle("gap: 6px; flex-wrap: wrap; align-items: center; margin: 8px 0;");
        bar = bar.WithView(Controls.Markdown(LocalizationCatalog.Get("ui.groupLabel", locale)));
        foreach (var (key, label) in Groupings)
            bar = bar.WithView(Btn(label, f.GroupBy == key, cur => cur with { GroupBy = key }));
        bar = bar.WithView(Controls.Markdown(LocalizationCatalog.Get("ui.periodLabel", locale)));
        foreach (var (days, label) in Windows)
            bar = bar.WithView(Btn(label, f.WindowDays == days, cur => cur with { WindowDays = days }));
        return bar;
    }

    private static UiControl Btn(string label, bool active, Func<FilterState, FilterState> update)
        => Controls.Button(label)
            .WithAppearance(active ? Appearance.Accent : Appearance.Outline)
            .WithClickAction(ctx =>
            {
                ctx.Host.Stream.GetDataStream<FilterState>(FilterDataId).Take(1).Subscribe(cur =>
                    ctx.Host.UpdateData(FilterDataId, update(cur ?? Default)));
                return Task.CompletedTask;
            });

    private static UiControl BuildGrid(
        IEnumerable<MeshNode> nodes, FilterState filter, JsonSerializerOptions jsonOptions,
        IReadOnlyDictionary<string, ModelPriceRate>? priceCatalog = null, string? locale = null)
    {
        var cutoff = filter.WindowDays > 0
            ? DateTimeOffset.UtcNow.AddDays(-filter.WindowDays)
            : DateTimeOffset.MinValue;

        var usages = nodes
            .Select(n => (node: n, u: n.ContentAs<TokenUsage>(jsonOptions, null)))
            .Where(x => x.u is not null && x.node.LastModified >= cutoff)
            .ToList();

        string KeyOf((MeshNode node, TokenUsage? u) x) => filter.GroupBy switch
        {
            "Person" => string.IsNullOrEmpty(x.u!.UserId) ? "(unknown)" : x.u!.UserId!,
            "Thread" => string.IsNullOrEmpty(x.u!.ThreadId) ? "(unknown)" : LastSegment(x.u!.ThreadId!),
            _ => string.IsNullOrEmpty(x.u!.Model) ? "(unknown)" : x.u!.Model,
        };

        var rows = usages
            .GroupBy(KeyOf)
            .Select(g =>
            {
                long inp = g.Sum(x => x.u!.InputTokens);
                long outp = g.Sum(x => x.u!.OutputTokens);
                // Cache-aware, like the thread token chip: cache reads bill at the reduced rate and
                // writes at the premium, so a cache-heavy agent isn't billed as if every prompt
                // token were fresh (the two surfaces disagreed while this used the 2-arg overload).
                decimal cost = g.Sum(x => ModelPriceCatalog.RateFor(x.u!.Model, priceCatalog)
                    ?.Cost(x.u!.InputTokens, x.u!.OutputTokens, x.u!.CacheReadTokens, x.u!.CacheWriteTokens) ?? 0m);
                return new UsageRow(g.Key, inp, outp, inp + outp, cost);
            })
            .OrderByDescending(r => r.Total)
            .ToList();

        if (rows.Count == 0)
            return Controls.Markdown(LocalizationCatalog.Get("ui.mdNoTokenUsage", locale));

        long totIn = rows.Sum(r => r.Input);
        long totOut = rows.Sum(r => r.Output);
        decimal totCost = rows.Sum(r => r.Cost);
        var usd = CultureInfo.GetCultureInfo("en-US");

        var grid = Controls.DataGrid(rows)
            .WithColumn(new PropertyColumnControl<string> { Property = nameof(UsageRow.Group).ToCamelCase() }
                .WithTitle(filter.GroupBy))
            .WithColumn(new PropertyColumnControl<long> { Property = nameof(UsageRow.Input).ToCamelCase() }
                .WithTitle("Input").WithFormat("N0"))
            .WithColumn(new PropertyColumnControl<long> { Property = nameof(UsageRow.Output).ToCamelCase() }
                .WithTitle("Output").WithFormat("N0"))
            .WithColumn(new PropertyColumnControl<long> { Property = nameof(UsageRow.Total).ToCamelCase() }
                .WithTitle("Total").WithFormat("N0"))
            .WithColumn(new PropertyColumnControl<decimal> { Property = nameof(UsageRow.Cost).ToCamelCase() }
                .WithTitle("Cost (USD)").WithFormat("C2"));

        return Controls.Stack
            .WithView(Controls.Markdown(
                $"**{rows.Count}** {filter.GroupBy.ToLowerInvariant()} group(s) · " +
                $"**{totIn + totOut:N0}** tokens (↑{totIn:N0} / ↓{totOut:N0}) · " +
                $"est. **{totCost.ToString("C2", usd)}**"))
            .WithView(grid);
    }

    private static string LastSegment(string path)
    {
        var i = path.LastIndexOf('/');
        return i >= 0 && i < path.Length - 1 ? path[(i + 1)..] : path;
    }

    private sealed record FilterState(string GroupBy, int WindowDays);

    private sealed record UsageRow(string Group, long Input, long Output, long Total, decimal Cost);
}
