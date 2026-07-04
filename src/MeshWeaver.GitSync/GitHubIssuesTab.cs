using System.Reactive.Linq;
using MeshWeaver.Application.Styles;
using MeshWeaver.Data;
using MeshWeaver.Graph;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Layout.DataGrid;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Messaging;
using MeshWeaver.Utils;
using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.GitSync;

/// <summary>A DataGrid row for a synced GitHub issue (plain, camel-cased for the grid binding).</summary>
public record GitHubIssueRow(int Number, string Title, string State, string Author, string Labels, int Comments, string Updated);

/// <summary>A DataGrid row for a live GitHub pull request summary.</summary>
public record GitHubPrRow(int Number, string Title, string Author, string Status, string Draft, string Branches, string Updated);

/// <summary>
/// The "GitHub Issues &amp; PRs" settings tab — the browse/act surface for the issue + pull-request
/// integration (the sync/commit/PR-draft flow lives in the "GitHub Sync" tab). Appears on the
/// Settings page of any node within a Space (always acting on the containing Space), gated on
/// Update, exactly like <see cref="GitHubSyncSettingsTab"/>.
///
/// <para>🚨 Structured data is rendered with <see cref="DataGridControl"/> bound to plain row
/// records — NEVER hand-built HTML. Issues are mesh nodes, so their grid binds to a LIVE synced
/// query (<see cref="IssueService.WatchIssueNodes"/>) and refreshes itself after a sync or webhook.
/// Pull requests are read LIVE from GitHub (never persisted), pushed into the grid on load / refresh.</para>
/// </summary>
public static class GitHubIssuesTab
{
    /// <summary>The settings-menu item id for the GitHub Issues &amp; PRs tab.</summary>
    public const string TabId = "GitHubIssues";

    private const string ResultId = "ghIssuesResult";
    private const string ActivityPathId = "ghIssuesActivityPath";
    private const string NewIssueFormId = "ghNewIssueForm";
    private const string MergeFormId = "ghMergeForm";
    private const string IssuesGridId = "ghIssuesGrid";
    private const string PrGridId = "ghPrGrid";

    /// <summary>Registers the GitHub Issues &amp; PRs settings tab provider (shown on any node within a Space).</summary>
    public static MessageHubConfiguration AddGitHubIssuesTab(this MessageHubConfiguration config)
        => config.AddSettingsMenuItems(new SettingsMenuItemProvider(GetTab));

    private static IObservable<IReadOnlyList<SettingsMenuItemDefinition>> GetTab(
        LayoutAreaHost host, RenderingContext ctx)
    {
        IReadOnlyList<SettingsMenuItemDefinition> none = Array.Empty<SettingsMenuItemDefinition>();
        var tab = new SettingsMenuItemDefinition(
            Id: TabId,
            Label: "GitHub Issues & PRs",
            ContentBuilder: BuildContent,
            Group: "Integration",
            Icon: FluentIcons.Document(),
            GroupIcon: FluentIcons.Document(),
            Order: 260,
            RequiredPermission: Permission.None);

        var spaceRoot = SpaceRootPath(host.Hub.Address.ToString());
        if (string.IsNullOrEmpty(spaceRoot))
            return Observable.Return(none);

        return host.Workspace.GetMeshNodeStream(spaceRoot)
            .Select(node => string.Equals(node?.NodeType, GitHubSyncService.SpaceNodeType, StringComparison.Ordinal))
            .CombineLatest(
                host.Hub.GetEffectivePermissions(spaceRoot),
                (isSpace, perms) => isSpace && perms.HasFlag(Permission.Update))
            .DistinctUntilChanged()
            .Select(show => show ? (IReadOnlyList<SettingsMenuItemDefinition>)new[] { tab } : none)
            .Catch<IReadOnlyList<SettingsMenuItemDefinition>, Exception>(_ => Observable.Return(none))
            .StartWith(none);
    }

    private static string SpaceRootPath(string? path) =>
        string.IsNullOrEmpty(path) ? "" : path.Split('/', 2)[0];

    internal static UiControl BuildContent(LayoutAreaHost host, StackControl stack, MeshNode? node)
    {
        var sp = host.Hub.ServiceProvider;
        var issues = sp.GetRequiredService<IssueService>();
        var prs = sp.GetRequiredService<PullRequestService>();
        var userId = sp.GetService<AccessService>()?.Context?.ObjectId ?? "";
        var spacePath = SpaceRootPath(node?.Path ?? "");

        if (string.IsNullOrEmpty(spacePath))
            return stack.WithView(Controls.Html("<p><em>GitHub Issues are available inside a Space.</em></p>"));

        stack = stack.WithView(Controls.H2("GitHub Issues & Pull Requests").WithStyle("margin:0 0 8px 0;"));
        stack = stack.WithView(Controls.Html(
            "<p style=\"font-size:0.85rem;color:var(--neutral-foreground-hint);margin-bottom:16px;\">" +
            "Browse and act on the configured repository's issues and pull requests. Set the repository " +
            "and connect GitHub in the <strong>GitHub Sync</strong> tab first.</p>"));

        // ── Issues ─────────────────────────────────────────────────────────────
        stack = stack.WithView(Section("Issues"));
        stack = stack.WithView(Controls.Button("Sync issues from GitHub")
            .WithAppearance(Appearance.Accent)
            .WithClickAction(c =>
            {
                c.Host.UpdateData(ResultId, Pending("Syncing issues from GitHub…"));
                c.Host.Hub.SyncIssuesFromGitHub(spacePath, userId,
                        onActivityCreated: p => c.Host.UpdateData(ActivityPathId, p))
                    .Subscribe(_ => { }, ex => c.Host.UpdateData(ResultId, Err(ex.Message)));
                return Task.CompletedTask;
            }));

        stack = stack.WithView(BuildNewIssueForm(issues, spacePath, userId));

        // Live issues grid — binds to the synced query, refreshes itself as issues land.
        host.RegisterForDisposal(issues.WatchIssueNodes(spacePath)
            .Select(nodes => MapIssues(host, nodes))
            .Subscribe(
                rows => host.UpdateData(IssuesGridId, rows),
                // Surface a faulted synced query instead of a silently-frozen grid.
                ex => host.UpdateData(ResultId, Err($"Issue list stopped updating: {ex.Message}"))));
        stack = stack.WithView(new DataGridControl(new JsonPointerReference(LayoutAreaReference.GetDataPointer(IssuesGridId)))
            .WithColumn(new PropertyColumnControl<int> { Property = nameof(GitHubIssueRow.Number).ToCamelCase() }.WithTitle("#"))
            .WithColumn(new PropertyColumnControl<string> { Property = nameof(GitHubIssueRow.Title).ToCamelCase() }.WithTitle("Title"))
            .WithColumn(new PropertyColumnControl<string> { Property = nameof(GitHubIssueRow.State).ToCamelCase() }.WithTitle("State"))
            .WithColumn(new PropertyColumnControl<string> { Property = nameof(GitHubIssueRow.Author).ToCamelCase() }.WithTitle("Author"))
            .WithColumn(new PropertyColumnControl<string> { Property = nameof(GitHubIssueRow.Labels).ToCamelCase() }.WithTitle("Labels"))
            .WithColumn(new PropertyColumnControl<int> { Property = nameof(GitHubIssueRow.Comments).ToCamelCase() }.WithTitle("Comments"))
            .WithColumn(new PropertyColumnControl<string> { Property = nameof(GitHubIssueRow.Updated).ToCamelCase() }.WithTitle("Updated"))
            .WithItemSize(36)
            .Resizable());

        // ── Pull requests ────────────────────────────────────────────────────
        stack = stack.WithView(Section("Pull requests"));
        stack = stack.WithView(Controls.Button("Refresh pull requests")
            .WithAppearance(Appearance.Outline)
            .WithClickAction(c =>
            {
                PushPrs(prs, spacePath, userId, rows => c.Host.UpdateData(PrGridId, rows),
                    err => c.Host.UpdateData(ResultId, Err(err)));
                return Task.CompletedTask;
            }));

        stack = stack.WithView(BuildMergeForm(spacePath, userId));

        // Initial PR load (live from GitHub, never persisted).
        host.RegisterForDisposal(prs.ListAll(spacePath, null, userId)
            .Select(MapPrs)
            .Catch<IReadOnlyList<GitHubPrRow>, Exception>(_ =>
                Observable.Return((IReadOnlyList<GitHubPrRow>)Array.Empty<GitHubPrRow>()))
            .Subscribe(rows => host.UpdateData(PrGridId, rows), _ => { }));
        stack = stack.WithView(new DataGridControl(new JsonPointerReference(LayoutAreaReference.GetDataPointer(PrGridId)))
            .WithColumn(new PropertyColumnControl<int> { Property = nameof(GitHubPrRow.Number).ToCamelCase() }.WithTitle("#"))
            .WithColumn(new PropertyColumnControl<string> { Property = nameof(GitHubPrRow.Title).ToCamelCase() }.WithTitle("Title"))
            .WithColumn(new PropertyColumnControl<string> { Property = nameof(GitHubPrRow.Author).ToCamelCase() }.WithTitle("Author"))
            .WithColumn(new PropertyColumnControl<string> { Property = nameof(GitHubPrRow.Status).ToCamelCase() }.WithTitle("Status"))
            .WithColumn(new PropertyColumnControl<string> { Property = nameof(GitHubPrRow.Draft).ToCamelCase() }.WithTitle("Draft"))
            .WithColumn(new PropertyColumnControl<string> { Property = nameof(GitHubPrRow.Branches).ToCamelCase() }.WithTitle("Head → Base"))
            .WithColumn(new PropertyColumnControl<string> { Property = nameof(GitHubPrRow.Updated).ToCamelCase() }.WithTitle("Updated"))
            .WithItemSize(36)
            .Resizable());

        // ── Result area ─────────────────────────────────────────────────────
        stack = stack.WithView((h, _) => h.Stream.GetDataStream<string>(ResultId)
            .Select(html => string.IsNullOrEmpty(html)
                ? (UiControl?)Controls.Stack.WithWidth("100%")
                : (UiControl?)Controls.Stack.WithWidth("100%").WithView(Controls.Html(html)))
            .StartWith((UiControl?)Controls.Stack.WithWidth("100%")));

        return stack;
    }

    // ── Forms ──────────────────────────────────────────────────────────────────

    private static UiControl BuildNewIssueForm(IssueService issues, string spacePath, string userId)
    {
        var row = Controls.Stack.WithOrientation(Orientation.Horizontal).WithStyle("gap:8px;align-items:flex-end;flex-wrap:wrap;");
        row = row.WithView(new TextFieldControl(new JsonPointerReference("title"))
        {
            Label = "New issue title",
            Placeholder = "e.g. Broken import on empty file",
            DataContext = LayoutAreaReference.GetDataPointer(NewIssueFormId),
        }.WithWidth("320px"));
        row = row.WithView(new TextFieldControl(new JsonPointerReference("body"))
        {
            Label = "Body (optional)",
            Placeholder = "Describe the issue…",
            DataContext = LayoutAreaReference.GetDataPointer(NewIssueFormId),
        }.WithWidth("320px"));
        row = row.WithView(Controls.Button("Create issue")
            .WithAppearance(Appearance.Outline)
            .WithClickAction(c =>
            {
                c.Host.Stream.GetDataStream<Dictionary<string, object?>>(NewIssueFormId).Take(1).Subscribe(d =>
                {
                    var title = Str(d, "title");
                    if (string.IsNullOrWhiteSpace(title))
                    {
                        c.Host.UpdateData(ResultId, Err("Enter a title for the new issue."));
                        return;
                    }
                    issues.CreateIssue(spacePath, title, Str(d, "body"), null, userId).Subscribe(
                        n => c.Host.UpdateData(ResultId, Ok($"Issue created — {n.Name}.")),
                        ex => c.Host.UpdateData(ResultId, Err(ex.Message)));
                });
                return Task.CompletedTask;
            }));
        return row;
    }

    private static UiControl BuildMergeForm(string spacePath, string userId)
    {
        var row = Controls.Stack.WithOrientation(Orientation.Horizontal).WithStyle("gap:8px;align-items:flex-end;flex-wrap:wrap;");
        row = row.WithView(new TextFieldControl(new JsonPointerReference("prNumber"))
        {
            Label = "Pull request # to merge",
            Placeholder = "e.g. 42",
            DataContext = LayoutAreaReference.GetDataPointer(MergeFormId),
        }.WithWidth("200px"));
        row = row.WithView(MergeButton("Merge commit", GitHubMergeMethod.Merge, spacePath, userId));
        row = row.WithView(MergeButton("Squash & merge", GitHubMergeMethod.Squash, spacePath, userId));
        return row;
    }

    private static UiControl MergeButton(string label, GitHubMergeMethod method, string spacePath, string userId) =>
        Controls.Button(label)
            .WithAppearance(Appearance.Outline)
            .WithClickAction(c =>
            {
                c.Host.Stream.GetDataStream<Dictionary<string, object?>>(MergeFormId).Take(1).Subscribe(d =>
                {
                    if (!int.TryParse(Str(d, "prNumber"), out var number) || number <= 0)
                    {
                        c.Host.UpdateData(ResultId, Err("Enter the pull request number to merge."));
                        return;
                    }
                    c.Host.UpdateData(ResultId, Pending($"Merging pull request #{number} ({method})…"));
                    c.Host.Hub.MergePullRequestOnGitHub(spacePath, number, method, userId,
                            onActivityCreated: p => c.Host.UpdateData(ActivityPathId, p))
                        .Subscribe(_ => { }, ex => c.Host.UpdateData(ResultId, Err(ex.Message)));
                });
                return Task.CompletedTask;
            });

    // ── Mapping ──────────────────────────────────────────────────────────────

    private static IReadOnlyList<GitHubIssueRow> MapIssues(LayoutAreaHost host, IReadOnlyList<MeshNode> nodes)
    {
        var opts = host.Hub.JsonSerializerOptions;
        return nodes
            .Select(n => n.ContentAs<GitHubIssue>(opts))
            .Where(i => i is not null).Select(i => i!)
            .OrderByDescending(i => i.Number)
            .Select(i => new GitHubIssueRow(
                i.Number, i.Title ?? "", i.State.ToString(), i.AuthorLogin ?? "",
                string.Join(", ", i.Labels), i.CommentsCount,
                i.UpdatedAt?.ToString("yyyy-MM-dd") ?? ""))
            .ToList();
    }

    private static IReadOnlyList<GitHubPrRow> MapPrs(IReadOnlyList<GitHubPullRequestSummary> prs) =>
        prs.OrderByDescending(p => p.Number)
            .Select(p => new GitHubPrRow(
                p.Number, p.Title, p.AuthorLogin ?? "", p.Status.ToString(),
                p.Draft ? "draft" : "", $"{p.HeadBranch} → {p.BaseBranch}",
                p.UpdatedAt?.ToString("yyyy-MM-dd") ?? ""))
            .ToList();

    private static void PushPrs(
        PullRequestService prs, string spacePath, string userId,
        Action<IReadOnlyList<GitHubPrRow>> onRows, Action<string> onError)
    {
        prs.ListAll(spacePath, null, userId).Select(MapPrs).Subscribe(onRows, ex => onError(ex.Message));
    }

    // ── Small view helpers ─────────────────────────────────────────────────────

    private static UiControl Section(string title) =>
        Controls.H3(title).WithStyle("margin:16px 0 8px 0;border-top:1px solid var(--neutral-stroke-divider-rest);padding-top:12px;");

    private static string Str(Dictionary<string, object?>? d, string key) =>
        d is not null && d.TryGetValue(key, out var v) && v is not null ? v.ToString() ?? "" : "";

    private static string Ok(string m) =>
        $"<p style=\"color:#22c55e;font-size:0.85rem;margin:8px 0;\">✓ {Esc(m)}</p>";

    private static string Err(string m) =>
        $"<p style=\"color:#ef4444;font-size:0.85rem;margin:8px 0;\">✗ {Esc(m)}</p>";

    private static string Pending(string m) =>
        $"<p style=\"color:var(--neutral-foreground-hint);font-size:0.85rem;margin:8px 0;\">… {Esc(m)}</p>";

    private static string Esc(string s) => System.Web.HttpUtility.HtmlEncode(s);
}
