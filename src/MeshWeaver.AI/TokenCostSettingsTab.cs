using System.Collections.Immutable;
using System.ComponentModel;
using System.Reactive.Linq;
using MeshWeaver.Application.Styles;
using MeshWeaver.Data;
using MeshWeaver.Graph;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Controls = MeshWeaver.Layout.Controls;
using MeshThread = MeshWeaver.AI.Thread;

namespace MeshWeaver.AI;

/// <summary>
/// The "Token Cost" settings tab — a space-wide AI usage summary. It appears on the
/// Settings page of every node within a Space (always referring to the containing
/// Space) and sums every thread's per-model token usage
/// (<see cref="MeshThread.TokensByModel"/>) over a user-picked date range, pricing
/// each model via <see cref="ModelPricing"/> (model-node prices, default table
/// fallback). Hidden outside a Space and for users who lack Read on it.
/// Mirrors <c>GitHubSyncSettingsTab</c>'s space-gating; the aggregate UI follows the
/// Northwind <c>OrdersSummaryArea</c> toolbar-driven pattern.
/// </summary>
public static class TokenCostSettingsTab
{
    /// <summary>Settings tab id.</summary>
    public const string TabId = "TokenCost";

    /// <summary>Registers the Token Cost settings tab provider (shown on any node within a Space).</summary>
    public static MessageHubConfiguration AddTokenCostSettingsTab(this MessageHubConfiguration config)
        => config.AddSettingsMenuItems(new SettingsMenuItemProvider(GetTab));

    private static IObservable<IReadOnlyList<SettingsMenuItemDefinition>> GetTab(
        LayoutAreaHost host, RenderingContext ctx)
    {
        IReadOnlyList<SettingsMenuItemDefinition> none = Array.Empty<SettingsMenuItemDefinition>();
        var tab = new SettingsMenuItemDefinition(
            Id: TabId,
            Label: "Token Cost",
            ContentBuilder: BuildContent,
            Group: "Usage",
            Icon: FluentIcons.Money(),
            GroupIcon: FluentIcons.Money(),
            Order: 260,
            // Viewing usage needs Read on the Space; gated below alongside the Space-node check.
            RequiredPermission: Permission.None);

        // Show on the Settings page of any node within a Space, always referring to the containing
        // Space (first path segment — Spaces are top-level). Gate on (root is a Space) AND (Read).
        var spaceRoot = SpaceRootPath(host.Hub.Address.ToString());
        if (string.IsNullOrEmpty(spaceRoot))
            return Observable.Return(none);

        return host.Workspace.GetMeshNodeStream(spaceRoot)
            .Select(node => string.Equals(node?.NodeType, SpaceNodeType.NodeType, StringComparison.Ordinal))
            .CombineLatest(
                host.Hub.GetEffectivePermissions(spaceRoot),
                (isSpace, perms) => isSpace && perms.HasFlag(Permission.Read))
            .DistinctUntilChanged()
            .Select(show => show ? (IReadOnlyList<SettingsMenuItemDefinition>)new[] { tab } : none)
            .Catch<IReadOnlyList<SettingsMenuItemDefinition>, Exception>(_ => Observable.Return(none))
            .StartWith(none);
    }

    /// <summary>The partition root for a node path — its first segment (the containing Space).</summary>
    private static string SpaceRootPath(string? path) =>
        string.IsNullOrEmpty(path) ? "" : path.Split('/', 2)[0];

    internal static UiControl BuildContent(LayoutAreaHost host, StackControl stack, MeshNode? node)
    {
        var spacePath = SpaceRootPath(node?.Path ?? host.Hub.Address.ToString());
        if (string.IsNullOrEmpty(spacePath))
            return stack.WithView(Controls.Html("<p><em>Token cost is available inside a Space.</em></p>"));

        var meshQuery = host.Hub.ServiceProvider.GetService<IMeshService>();
        if (meshQuery is null)
            return stack.WithView(Controls.Html("<p>Query service not available.</p>"));

        stack = stack.WithView(Controls.H2("Token cost").WithStyle("margin: 0 0 8px 0;"));
        stack = stack.WithView(Controls.Html(
            "<p style=\"font-size:0.85rem;color:var(--neutral-foreground-hint);margin-bottom:16px;\">"
            + "Tokens and cost for every thread in this space, summed per model. Pick a date range — "
            + "threads are included by their last-activity date.</p>"));

        var priceResolver = TokenCostSummary.ObservePriceResolver(meshQuery);
        // Default to the past month. (Layout-area content builder — DateTime.UtcNow is fine here;
        // the no-DateTime rule is workflow-script-only.)
        var now = DateTime.UtcNow.Date;
        var defaultRange = new TokenCostRange { From = now.AddMonths(-1), To = now };

        // Threads live under each node's _Thread partition. Matches SpaceLayoutAreas.BuildLatestThreads
        // ({space}/*/_Thread); threads attached directly to the space root are out of scope (rare).
        var threadsQuery = $"nodeType:{ThreadNodeType.NodeType} namespace:{spacePath}/*/{ThreadNodeType.ThreadPartition}";

        var view = host.Toolbar(defaultRange, (range, area, _) =>
        {
            var threads = meshQuery.Query<MeshNode>(MeshQueryRequest.FromQuery(threadsQuery))
                .Select(c => (IReadOnlyList<MeshNode>)c.Items)
                .Catch<IReadOnlyList<MeshNode>, Exception>(_ =>
                    Observable.Return((IReadOnlyList<MeshNode>)Array.Empty<MeshNode>()));

            return threads.CombineLatest(priceResolver, (nodes, priceFor) =>
            {
                var from = range.From.Date;
                var to = range.To.Date;
                var inRange = nodes
                    .Where(n =>
                    {
                        var d = n.LastModified.UtcDateTime.Date;
                        return d >= from && d <= to;
                    })
                    .ToList();

                var aggregate = TokenCostSummary.Merge(inRange.Select(n =>
                    (IReadOnlyDictionary<string, ModelTokenUsage>)
                    ((n.Content as MeshThread)?.TokensByModel
                     ?? ImmutableDictionary<string, ModelTokenUsage>.Empty)));

                var rows = TokenCostSummary.BuildRows(aggregate, priceFor);
                var caption = Controls.Html(
                    $"<p style=\"font-size:0.8rem;color:var(--neutral-foreground-hint);margin:8px 0;\">"
                    + $"{inRange.Count} thread(s) with activity in {from:yyyy-MM-dd} – {to:yyyy-MM-dd}</p>");
                return (UiControl)Controls.Stack
                    .WithView(caption)
                    .WithView(Controls.Html(TokenCostSummary.RenderHtml(rows, "No token usage in this period.")));
            });
        });

        return stack.WithView(view);
    }

    /// <summary>
    /// Date-range filter for the space token-cost summary. Auto-renders as two date
    /// pickers via the <c>Toolbar</c> macro (DateTime → DateTimeControl).
    /// </summary>
    public record TokenCostRange
    {
        /// <summary>Inclusive start of the range (filters threads by last-activity date).</summary>
        [Description("From")]
        public DateTime From { get; init; }

        /// <summary>Inclusive end of the range.</summary>
        [Description("To")]
        public DateTime To { get; init; }
    }
}
