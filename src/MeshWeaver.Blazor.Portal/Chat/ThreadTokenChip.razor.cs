using System.Collections.Immutable;
using System.Globalization;
using System.Reactive.Linq;
using MeshWeaver.AI;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MeshThread = MeshWeaver.AI.Thread;

namespace MeshWeaver.Blazor.Portal.Chat;

/// <summary>
/// Compact "token usage" chip for a chat thread. Renders
/// <c>↑1.2k ↓3.4k · $0.05</c> (total input / output tokens + summed cost) and
/// expands on click into a per-model breakdown (<c>model ↑in ↓out</c>).
///
/// <para>Reads — never recomputes — the per-model tallies already accumulated on
/// the thread node (<see cref="MeshThread.TokensByModel"/>). Cost is derived from
/// the built-in <see cref="ModelPricing.Default(string?)"/> table. Fully reactive:
/// it subscribes to the thread MeshNode stream (the process-wide
/// <see cref="IMeshNodeStreamCache"/> handle via <c>Hub.GetMeshNodeStream(path)</c>)
/// exactly like the sibling chat components — no <c>async</c>/<c>await</c>, no
/// <c>.Take(1)</c>, so the chip stays live for the lifetime of the component.</para>
/// </summary>
public partial class ThreadTokenChip : ComponentBase, IDisposable
{
    [Inject] private IMessageHub Hub { get; set; } = null!;

    /// <summary>
    /// Path of the thread MeshNode whose token usage to display. Matches how
    /// <c>ThreadChatView</c> threads its own <c>threadPath</c> context around.
    /// </summary>
    [Parameter] public string? ThreadPath { get; set; }

    private ILogger<ThreadTokenChip>? _logger;
    private IDisposable? _subscription;
    private string? _subscribedPath;
    private bool _disposed;
    private bool _expanded;

    private long _totalInput;
    private long _totalOutput;
    private decimal _totalCost;
    private IReadOnlyList<ModelRow> _rows = [];

    private bool HasData => _rows.Count > 0;

    private string ChipLabel
        => $"↑{FormatCompact(_totalInput)} ↓{FormatCompact(_totalOutput)} · {FormatCost(_totalCost)}";

    protected override void OnInitialized()
    {
        base.OnInitialized();
        _logger = Hub.ServiceProvider.GetService<ILogger<ThreadTokenChip>>();
    }

    protected override void OnParametersSet()
    {
        base.OnParametersSet();
        if (ThreadPath == _subscribedPath) return;

        _subscription?.Dispose();
        _subscription = null;
        _subscribedPath = ThreadPath;
        _expanded = false;
        ApplyThread(null);

        if (string.IsNullOrEmpty(ThreadPath)) return;

        // Live, shared cache handle — same read path every chat component uses.
        // Each emission re-renders the chip; never .Take(1) (that would freeze it).
        _subscription = Hub.GetMeshNodeStream(ThreadPath)
            .Select(node => node.ContentAs<MeshThread>(Hub.JsonSerializerOptions, _logger))
            .Subscribe(
                thread => InvokeAsync(() =>
                {
                    if (_disposed) return;
                    ApplyThread(thread);
                    StateHasChanged();
                }),
                ex => _logger?.LogDebug(ex, "[ThreadTokenChip] stream errored for {Path}", ThreadPath));
    }

    /// <summary>
    /// Recomputes the totals and per-model rows from the thread's
    /// <see cref="MeshThread.TokensByModel"/>. Cost is summed per model from the
    /// built-in pricing table (no per-model node price overrides — kept simple).
    /// </summary>
    private void ApplyThread(MeshThread? thread)
    {
        var byModel = thread?.TokensByModel ?? ImmutableDictionary<string, ModelTokenUsage>.Empty;

        long totalIn = 0;
        long totalOut = 0;
        decimal cost = 0m;
        var rows = new List<ModelRow>(byModel.Count);
        foreach (var (modelId, usage) in byModel)
        {
            totalIn += usage.InputTokens;
            totalOut += usage.OutputTokens;
            cost += ModelPricing.Default(modelId)?.Cost(usage.InputTokens, usage.OutputTokens) ?? 0m;
            rows.Add(new ModelRow(modelId, usage.InputTokens, usage.OutputTokens));
        }

        _totalInput = totalIn;
        _totalOutput = totalOut;
        _totalCost = cost;
        _rows = rows
            .OrderByDescending(r => (long)r.Input + r.Output)
            .ToArray();
    }

    private void ToggleExpanded() => _expanded = !_expanded;

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

    public void Dispose()
    {
        _disposed = true;
        _subscription?.Dispose();
        _subscription = null;
    }

    private sealed record ModelRow(string Model, int Input, int Output);
}
