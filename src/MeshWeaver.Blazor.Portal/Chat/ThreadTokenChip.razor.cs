using System.Collections.Immutable;
using System.Globalization;
using System.Reactive.Linq;
using MeshWeaver.AI;
using MeshWeaver.Graph;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Blazor.Portal.Chat;

/// <summary>
/// Compact "token usage" chip for a chat thread. Renders
/// <c>↑1.2k ↓3.4k · $0.05</c> (total input / output tokens + summed cost) and
/// expands on click into a per-model breakdown (<c>model ↑in ↓out</c>).
///
/// <para>Reads — never recomputes — the per-model <see cref="TokenUsage"/> satellites under the
/// thread (<c>{threadPath}/_Usage/*</c>). Cost comes from the LIVE model catalog first
/// (<see cref="ModelPriceCatalog"/>: the price authored on the <c>LanguageModel</c> node) and falls
/// back to the built-in <see cref="ModelPricing.Defaults"/> table — a model outside that table (any
/// OpenRouter / BYO-key model) used to bill at $0 here regardless of its node price. Fully
/// reactive: it subscribes to a LIVE query of those satellites AND of the model catalog
/// (<c>Hub.GetQuery(...)</c>), which re-emit whenever a round writes usage or an admin edits a
/// price — no <c>async</c>/<c>await</c>, no <c>.Take(1)</c>, so the chip stays live for the
/// lifetime of the component.</para>
/// </summary>
public partial class ThreadTokenChip : ComponentBase, IDisposable
{
    [Inject] private IMessageHub Hub { get; set; } = null!;

    /// <summary>
    /// Path of the thread MeshNode whose token usage to display. Matches how
    /// <c>ThreadChatView</c> threads its own <c>threadPath</c> context around.
    /// </summary>
    [Parameter] public string? ThreadPath { get; set; }

    /// <summary>
    /// The viewer's home path, so the price catalog also covers their bring-your-own-key models
    /// (<c>{user}/_Memex/{provider}/{model}</c>) — the same <c>userPath</c> the model picker passes
    /// to <c>AgentPickerProjection.ObserveModels</c>. Null just narrows the catalog to the shared
    /// providers; the built-in table still backs anything it misses.
    /// </summary>
    [Parameter] public string? UserPath { get; set; }

    private ILogger<ThreadTokenChip>? _logger;
    private IDisposable? _subscription;
    private string? _subscribedPath;
    private string? _subscribedUserPath;
    private ImmutableDictionary<string, ModelPriceRate> _priceCatalog =
        ImmutableDictionary<string, ModelPriceRate>.Empty;
    private bool _disposed;
    private bool _expanded;

    private long _totalInput;
    private long _totalOutput;
    private long _totalCacheRead;
    private long _totalCacheWrite;
    private decimal _totalCost;
    private IReadOnlyList<ModelRow> _rows = [];

    private bool HasData => _rows.Count > 0;

    // When any prompt caching happened, surface how much of the input was cache hits — the whole
    // point of the fix is that this used to be invisible. e.g. "↑1.2M ↓3.4k ⚡0.9M cached · $2.10".
    private string ChipLabel
        => _totalCacheRead > 0 || _totalCacheWrite > 0
            ? $"↑{FormatCompact(_totalInput)} ↓{FormatCompact(_totalOutput)} ⚡{FormatCompact(_totalCacheRead + _totalCacheWrite)} cached · {FormatCost(_totalCost)}"
            : $"↑{FormatCompact(_totalInput)} ↓{FormatCompact(_totalOutput)} · {FormatCost(_totalCost)}";

    /// <summary>
    /// Resolves the logger for the chip. Subscription setup is deferred to
    /// <c>OnParametersSet</c>, which runs once <c>ThreadPath</c> is supplied.
    /// </summary>
    protected override void OnInitialized()
    {
        base.OnInitialized();
        _logger = Hub.ServiceProvider.GetService<ILogger<ThreadTokenChip>>();
    }

    /// <summary>
    /// Re-subscribes when <c>ThreadPath</c> changes: disposes any prior subscription, resets the
    /// displayed totals, collapses the breakdown, and opens a LIVE query of the thread's per-model
    /// <c>TokenUsage</c> satellites (never <c>.Take(1)</c>) so the chip updates as rounds write usage.
    /// </summary>
    protected override void OnParametersSet()
    {
        base.OnParametersSet();
        if (ThreadPath == _subscribedPath && UserPath == _subscribedUserPath) return;

        _subscription?.Dispose();
        _subscription = null;
        _subscribedPath = ThreadPath;
        _subscribedUserPath = UserPath;
        _expanded = false;
        _priceCatalog = ImmutableDictionary<string, ModelPriceRate>.Empty;
        ApplyUsage(null);

        if (string.IsNullOrEmpty(ThreadPath)) return;

        // Live query of the per-model TokenUsage satellites under the thread ({threadPath}/_Usage/*).
        // A shared, live cache handle — re-emits whenever a round writes/updates a usage node, so the
        // chip stays live; never .Take(1) (that would freeze it).
        var usage = Hub.GetQuery(
            $"tokenchip:{ThreadPath}",
            $"path:{ThreadPath}/{TokenUsageNodeType.SatelliteSegment} scope:children nodeType:{TokenUsageNodeType.NodeType} select:path,id,namespace,name,description,nodeType,icon,order,createdBy,lastModified,content");
        if (usage is null) return;

        // Live model catalog — the SAME union the picker reads (shared providers + this thread's
        // partition + the viewer's BYO providers), projected to id → authored rate.
        //
        // 🚨 StartWith(empty) + Catch: the catalog is an ENRICHMENT, never a gate. Without the seed
        // the chip would render nothing until the model query first emits; without the catch, a
        // model-query error (a partition the viewer can't read) would kill the combined stream and
        // take the token counts down with it. Either way costs degrade to the built-in table.
        var prices = Hub.GetQuery(
                $"tokenchip-models:{ThreadPath}|{UserPath}",
                AgentPickerProjection.BuildModelQueries(
                    currentPath: AgentPickerProjection.PartitionOf(ThreadPath),
                    userPath: UserPath))
            .Select(nodes => ModelPriceCatalog.FromNodes(nodes, Hub.JsonSerializerOptions, _logger))
            .Catch((Exception ex) =>
            {
                _logger?.LogDebug(ex, "[ThreadTokenChip] model price query errored for {Path} — falling back to built-in prices", ThreadPath);
                return Observable.Return(ImmutableDictionary<string, ModelPriceRate>.Empty);
            })
            .StartWith(ImmutableDictionary<string, ModelPriceRate>.Empty);

        _subscription = usage
            .CombineLatest(prices, (nodes, catalog) => (nodes, catalog))
            .Subscribe(
                x => InvokeAsync(() =>
                {
                    if (_disposed) return;
                    _priceCatalog = x.catalog;
                    ApplyUsage(x.nodes);
                    StateHasChanged();
                }),
                ex => _logger?.LogDebug(ex, "[ThreadTokenChip] usage query errored for {Path}", ThreadPath));
    }

    /// <summary>
    /// Recomputes the totals and per-model rows from the thread's per-model
    /// <see cref="TokenUsage"/> satellites (<c>{threadPath}/_Usage/*</c>). Cost is summed per model
    /// at the effective rate — the price authored on the model's catalog node when there is one,
    /// else the built-in table (<see cref="ModelPriceCatalog.RateFor"/>).
    /// </summary>
    private void ApplyUsage(IEnumerable<MeshNode>? nodes)
    {
        long totalIn = 0;
        long totalOut = 0;
        long totalCacheRead = 0;
        long totalCacheWrite = 0;
        decimal cost = 0m;
        var rows = new List<ModelRow>();
        foreach (var node in nodes ?? [])
        {
            var usage = node.ContentAs<TokenUsage>(Hub.JsonSerializerOptions, _logger);
            if (usage is null)
                continue;
            totalIn += usage.InputTokens;
            totalOut += usage.OutputTokens;
            totalCacheRead += usage.CacheReadTokens;
            totalCacheWrite += usage.CacheWriteTokens;
            // Cache-aware cost: cache reads bill at the reduced rate, writes at the premium — pricing
            // the whole input at the standard rate would badly over-state a cache-heavy agent's cost.
            cost += ModelPriceCatalog.RateFor(usage.Model, _priceCatalog)
                ?.Cost(usage.InputTokens, usage.OutputTokens, usage.CacheReadTokens, usage.CacheWriteTokens) ?? 0m;
            rows.Add(new ModelRow(usage.Model, usage.InputTokens, usage.OutputTokens,
                usage.CacheReadTokens, usage.CacheWriteTokens));
        }

        _totalInput = totalIn;
        _totalOutput = totalOut;
        _totalCacheRead = totalCacheRead;
        _totalCacheWrite = totalCacheWrite;
        _totalCost = cost;
        _rows = rows
            .OrderByDescending(r => r.Input + r.Output)
            .ToArray();
    }

    private void ToggleExpanded() => _expanded = !_expanded;

    /// <summary>
    /// Keyboard activation for the role="button" label (Enter / Space), so the
    /// expand toggle stays accessible now that it renders as an inline metadata
    /// span rather than a native button.
    /// </summary>
    private void OnLabelKeyDown(KeyboardEventArgs e)
    {
        if (e.Key is "Enter" or " " or "Spacebar")
            ToggleExpanded();
    }

    /// <summary>
    /// Compact token count: <c>&lt; 1000 → "950"</c>, <c>&lt; 1M → "1.2k"</c>
    /// (one decimal, trailing <c>.0</c> trimmed), else <c>"3.4M"</c>. Pure;
    /// culture-invariant so the decimal separator never drifts.
    /// </summary>
    public static string FormatCompact(long n)
    {
        var negative = n < 0;
        var v = Math.Abs(n);
        string s;
        if (v < 1_000)
            s = v.ToString(CultureInfo.InvariantCulture);
        else if (v < 1_000_000)
            s = (v / 1_000d).ToString("0.#", CultureInfo.InvariantCulture) + "k";
        else
            s = (v / 1_000_000d).ToString("0.#", CultureInfo.InvariantCulture) + "M";
        return negative ? "-" + s : s;
    }

    /// <summary>
    /// Cost: under $1 → up to 4 decimals trimmed (<c>"$0.0234"</c>);
    /// otherwise 2 decimals (<c>"$1.23"</c>). Culture-invariant.
    /// </summary>
    private static string FormatCost(decimal cost)
        => cost < 1m
            ? "$" + cost.ToString("0.####", CultureInfo.InvariantCulture)
            : "$" + cost.ToString("0.00", CultureInfo.InvariantCulture);

    /// <summary>
    /// Marks the chip disposed and tears down the live usage-query subscription.
    /// </summary>
    public void Dispose()
    {
        _disposed = true;
        _subscription?.Dispose();
        _subscription = null;
    }

    private sealed record ModelRow(string Model, long Input, long Output, long CacheRead, long CacheWrite);
}
