using System.Linq;
using System.Reactive.Linq;
using System.Text.Json;
using MeshWeaver.Application.Styles;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Layout.DataGrid;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.Graph.Configuration;

/// <summary>
/// The platform-admin "Feedback" review tab. Feedback is filed PRIVATELY under each user's own
/// partition (<c>{userId}/Feedback/{id}</c>) — the self-scope rule makes each entry visible only to
/// its submitter, and to no other regular user. Platform admins are NOT data superusers, so this tab
/// is the ONE place they can see everything: it runs a single cross-partition <c>nodeType:Feedback</c>
/// query <b>as System</b> (the explicit RLS bypass on <see cref="MeshQueryRequest.UserId"/>), gated on
/// <c>hub.IsGlobalAdmin</c> at BOTH the menu (tab visibility) AND the content (before the query runs).
/// Read-only; no writes. Grouped under "Administration" beside the other admin tabs.
/// </summary>
public static class FeedbackReviewTab
{
    /// <summary>The settings-menu item id for the Feedback review tab.</summary>
    public const string TabId = "FeedbackReview";

    /// <summary>Registers the Feedback review settings tab provider (global admins only).</summary>
    public static MessageHubConfiguration AddFeedbackReviewTab(this MessageHubConfiguration config)
        => config.AddSettingsMenuItems(new SettingsMenuItemProvider(GetTab));

    private static IObservable<IReadOnlyList<SettingsMenuItemDefinition>> GetTab(
        LayoutAreaHost host, RenderingContext ctx)
    {
        IReadOnlyList<SettingsMenuItemDefinition> none = Array.Empty<SettingsMenuItemDefinition>();

        // Same home as the other admin tabs: the viewer's OWN settings page.
        var hubPath = host.Hub.Address.ToString();
        var nodeOwnerId = hubPath.StartsWith("User/", StringComparison.OrdinalIgnoreCase)
            ? hubPath["User/".Length..]
            : hubPath;
        var accessService = host.Hub.ServiceProvider.GetService<AccessService>();
        var viewerId = accessService?.Context?.ObjectId ?? accessService?.CircuitContext?.ObjectId;
        if (string.IsNullOrEmpty(viewerId)
            || !string.Equals(viewerId, nodeOwnerId, StringComparison.OrdinalIgnoreCase))
            return Observable.Return(none);

        var tab = new SettingsMenuItemDefinition(
            Id: TabId,
            Label: "Feedback",
            ContentBuilder: BuildContent,
            Group: "Administration",
            Icon: FluentIcons.Comment(),
            GroupIcon: FluentIcons.Shield(),
            Order: 330,
            Keywords: ["feedback", "review", "reports", "suggestions", "bugs", "ideas"]);

        // Canonical platform-admin check — wait for the POSITIVE with a bounded timeout; StartWith(none)
        // so the menu renders immediately and the tab appears once admin is confirmed.
        return host.Hub.IsGlobalAdmin(viewerId)
            .Where(isAdmin => isAdmin)
            .Take(1)
            .Select(_ => (IReadOnlyList<SettingsMenuItemDefinition>)new[] { tab })
            .Timeout(TimeSpan.FromSeconds(5))
            .Catch<IReadOnlyList<SettingsMenuItemDefinition>, Exception>(_ => Observable.Return(none))
            .StartWith(none);
    }

    private static UiControl BuildContent(LayoutAreaHost host, StackControl stack, MeshNode? node)
    {
        var meshService = host.Hub.ServiceProvider.GetService<IMeshService>();
        var accessService = host.Hub.ServiceProvider.GetService<AccessService>();
        var viewerId = accessService?.Context?.ObjectId ?? accessService?.CircuitContext?.ObjectId;

        stack = stack
            .WithView(Controls.Title("Feedback", 2))
            .WithView(Controls.Markdown(
                "All feedback submitted across the platform. Each entry is private to the person who "
                + "filed it — you can see them here because you're a platform admin."));

        if (meshService is null || string.IsNullOrEmpty(viewerId))
            return stack.WithView(Controls.Markdown("_Feedback review is unavailable._"));

        // 🔐 Defense-in-depth: re-confirm global-admin on the CONTENT before running the system-scoped
        // (RLS-bypassing) query. The menu gate alone must never be the only thing guarding it.
        return stack.WithView((h, _) =>
            host.Hub.IsGlobalAdmin(viewerId)
                .Take(1)
                .SelectMany(isAdmin => isAdmin
                    ? meshService
                        .Query<MeshNode>(MeshQueryRequest.FromQuery("nodeType:Feedback", WellKnownUsers.System))
                        .Select(change => BuildGrid(change.Items, h.Hub.JsonSerializerOptions))
                    : Observable.Return((UiControl?)Controls.Markdown("_Access denied._")))
                .Timeout(TimeSpan.FromSeconds(10))
                .Catch<UiControl?, Exception>(_ => Observable.Return((UiControl?)Controls.Markdown("_Could not load feedback._"))));
    }

    private static UiControl BuildGrid(IReadOnlyList<MeshNode> nodes, JsonSerializerOptions options)
    {
        if (nodes.Count == 0)
            return Controls.Markdown("_No feedback submitted yet._");

        var rows = nodes
            .Select(n =>
            {
                var fb = n.ContentAs<Feedback>(options);
                return new FeedbackRow(
                    Submitter: fb?.SubmittedByName ?? fb?.SubmittedBy ?? n.Namespace ?? string.Empty,
                    Summary: n.Name ?? string.Empty,
                    Location: fb?.Location ?? string.Empty,
                    Status: (fb?.Status ?? FeedbackStatus.New).ToString(),
                    Submitted: fb?.SubmittedAt.ToString("yyyy-MM-dd") ?? string.Empty);
            })
            .OrderByDescending(r => r.Submitted)
            .ToList();

        return Controls.DataGrid(rows)
            .WithColumn(new PropertyColumnControl<string> { Property = "submitter" }.WithTitle("Submitter"))
            .WithColumn(new PropertyColumnControl<string> { Property = "summary" }.WithTitle("Summary"))
            .WithColumn(new PropertyColumnControl<string> { Property = "location" }.WithTitle("Location"))
            .WithColumn(new PropertyColumnControl<string> { Property = "status" }.WithTitle("Status"))
            .WithColumn(new PropertyColumnControl<string> { Property = "submitted" }.WithTitle("Submitted"));
    }

    private sealed record FeedbackRow(string Submitter, string Summary, string Location, string Status, string Submitted);
}
