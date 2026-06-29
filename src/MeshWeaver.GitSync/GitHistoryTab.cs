using System.Reactive.Linq;
using System.Text.Json;
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
using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.GitSync;

/// <summary>
/// The "Git history" settings tab — a read-only git browser over the on-disk working tree
/// (<see cref="GitWorkingTreeService"/>). It renders the commit log of the Space's connected repo,
/// the files each commit changed, and any uncommitted working-tree changes — and shows a Monaco
/// side-by-side <see cref="DiffEditorControl"/> for the selected change. This is the same per-user
/// checkout the co-hosted AI CLIs operate on, so it doubles as a window into what the assistants did.
///
/// <para>Sits beside <see cref="WorkingTreeTab"/> (Code workspace, which edits + commits + pushes);
/// this tab only reads. Following the framework rules: tabular data is rendered with
/// <see cref="DataGridControl"/> (never hand-built HTML), diffs with <see cref="DiffEditorControl"/>;
/// every git call is reactive through <see cref="GitWorkingTreeService"/> / <see cref="GitCli"/>
/// (no <c>async</c>/<c>await</c>/<c>Task</c>); UI state lives only in <c>host</c> data ids.</para>
/// </summary>
public static class GitHistoryTab
{
    /// <summary>The settings-menu item id for the Git-history tab.</summary>
    public const string TabId = "GitHistory";

    private const int MaxCommits = 100;
    private const char Sep = '\x1f'; // diff-selection key delimiter (can't occur in a file path)

    private const string ResultId = "ghResult";          // checkout/refresh status line
    private const string RefreshId = "ghRefresh";        // bumped → re-query log + working-tree status
    private const string SelectedCommitId = "ghCommit";  // selected commit hash ("" = none)
    private const string SelectedChangeId = "ghChange";  // diff selection key ("" = none)
    private const string CommitRowsId = "ghCommitRows";  // commit-log grid data
    private const string WtRowsId = "ghWtRows";          // working-tree change grid data
    private const string CommitFileRowsId = "ghCommitFiles"; // selected-commit change grid data

    /// <summary>Registers the Git-history settings tab provider (shown on any node within a Space).</summary>
    public static MessageHubConfiguration AddGitHistoryTab(this MessageHubConfiguration config)
        => config.AddSettingsMenuItems(new SettingsMenuItemProvider(GetTab));

    private static IObservable<IReadOnlyList<SettingsMenuItemDefinition>> GetTab(
        LayoutAreaHost host, RenderingContext ctx)
    {
        IReadOnlyList<SettingsMenuItemDefinition> none = Array.Empty<SettingsMenuItemDefinition>();
        var tab = new SettingsMenuItemDefinition(
            Id: TabId,
            Label: "Git history",
            ContentBuilder: BuildContent,
            Group: "Integration",
            Icon: FluentIcons.History(),
            GroupIcon: FluentIcons.Document(),
            Order: 270, // just after the Code-workspace tab (260)
            RequiredPermission: Permission.None,
            Keywords: new[] { "git", "commit", "history", "diff", "changes", "log" });

        // Same gate as the Code-workspace tab: visible inside a Space to users who may Update it.
        var spaceRoot = WorkingTreeTab.SpaceRootPath(host.Hub.Address.ToString());
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

    internal static UiControl BuildContent(LayoutAreaHost host, StackControl stack, MeshNode? node)
    {
        var sp = host.Hub.ServiceProvider;
        var workingTrees = sp.GetRequiredService<GitWorkingTreeService>();
        var sync = sp.GetRequiredService<GitHubSyncService>();
        var userId = sp.GetService<AccessService>()?.Context?.ObjectId ?? "";
        var spacePath = WorkingTreeTab.SpaceRootPath(node?.Path ?? "");

        if (string.IsNullOrEmpty(spacePath))
            return stack.WithView(Controls.Markdown("Git history is available inside a Space."));
        if (string.IsNullOrEmpty(userId))
            return stack.WithView(Controls.Markdown("Sign in to browse git history."));

        // Seed the selection/refresh state once so the sub-views below have a stream to bind to.
        host.UpdateData(ResultId, "");
        host.UpdateData(RefreshId, "0");
        host.UpdateData(SelectedCommitId, "");
        host.UpdateData(SelectedChangeId, "");

        stack = stack.WithView(Controls.H2("Git history"));
        stack = stack.WithView(Controls.Markdown(
            "Browse the commit history of this Space's server-side working tree, see what each commit " +
            "changed, and inspect uncommitted changes — the same checkout the AI assistants edit."));

        // Everything keys off the Space's connected repo (slug + branch) from the GitHub-Sync config.
        stack = stack.WithView((h, _) => sync.WatchConfig(spacePath)
            .Select(cfg => (UiControl?)BuildBrowser(workingTrees, userId, cfg))
            .StartWith((UiControl?)Controls.Markdown("_Loading repository settings…_")));

        return stack;
    }

    private static UiControl BuildBrowser(GitWorkingTreeService wt, string userId, GitHubSyncConfig? cfg)
    {
        var repoFullName = WorkingTreeTab.RepoFullName(cfg?.RepositoryUrl);
        if (repoFullName is null)
            return Controls.Markdown("Connect a GitHub repository in the **GitHub Sync** tab first.");

        var repoSlug = WorkingTreeTab.RepoSlug(repoFullName);
        var branch = string.IsNullOrWhiteSpace(cfg!.Branch) ? "main" : cfg.Branch;

        var stack = Controls.Stack.WithWidth("100%").WithStyle("gap:12px;");

        // Checkout / pull + refresh toolbar.
        stack = stack.WithView(Controls.Stack.WithOrientation(Orientation.Horizontal).WithStyle("gap:8px;")
            .WithView(Controls.Button($"Check out / pull {repoFullName} ({branch})")
                .WithAppearance(Appearance.Accent)
                .WithIconStart(FluentIcons.ArrowDownload())
                .WithClickAction(ctx =>
                {
                    ctx.Host.UpdateData(ResultId, $"_Checking out {repoFullName}…_");
                    wt.Checkout(userId, repoFullName, branch).Subscribe(
                        tree =>
                        {
                            ctx.Host.UpdateData(ResultId, $"Checked out on **{tree.Branch}**.");
                            ctx.Host.UpdateData(RefreshId, Guid.NewGuid().ToString("N"));
                        },
                        ex => ctx.Host.UpdateData(ResultId, $"⚠ {ex.Message}"));
                    return Task.CompletedTask;
                }))
            .WithView(Controls.Button("Refresh")
                .WithIconStart(FluentIcons.ArrowClockwise())
                .WithClickAction(ctx =>
                {
                    ctx.Host.UpdateData(RefreshId, Guid.NewGuid().ToString("N"));
                    return Task.CompletedTask;
                })));

        // Status line.
        stack = stack.WithView((h, _) => h.Stream.GetDataStream<string>(ResultId)
            .Select(msg => (UiControl?)(string.IsNullOrEmpty(msg) ? Controls.Stack : Controls.Markdown(msg!)))
            .StartWith((UiControl?)Controls.Stack));

        // Working-tree (uncommitted) changes.
        stack = stack.WithView(Controls.H3("Working-tree changes"));
        stack = stack.WithView((h, _) => h.Stream.GetDataStream<string>(RefreshId)
            .Select(_ => wt.Status(userId, repoSlug)
                .Select(s => (UiControl?)BuildWorkingTreeChanges(h, s))
                .Catch<UiControl?, Exception>(_ => Observable.Return((UiControl?)NotCheckedOut())))
            .Switch()
            .StartWith((UiControl?)Controls.Markdown("_…_")));

        // Commit history.
        stack = stack.WithView(Controls.H3("Commits"));
        stack = stack.WithView((h, _) => h.Stream.GetDataStream<string>(RefreshId)
            .Select(_ => wt.Log(userId, repoSlug, MaxCommits)
                .Select(commits => (UiControl?)BuildCommitLog(h, commits))
                .Catch<UiControl?, Exception>(_ => Observable.Return((UiControl?)NotCheckedOut())))
            .Switch()
            .StartWith((UiControl?)Controls.Markdown("_Loading history…_")));

        // Files changed by the selected commit.
        stack = stack.WithView((h, _) => h.Stream.GetDataStream<string>(SelectedCommitId)
            .Select(hash => string.IsNullOrEmpty(hash)
                ? Observable.Return((UiControl?)Controls.Stack)
                : wt.CommitChanges(userId, repoSlug, hash!)
                    .Select(changes => (UiControl?)BuildCommitDetail(h, hash!, changes))
                    .Catch<UiControl?, Exception>(ex => Observable.Return((UiControl?)Controls.Markdown($"⚠ {ex.Message}"))))
            .Switch()
            .StartWith((UiControl?)Controls.Stack));

        // Diff pane for the selected change.
        stack = stack.WithView((h, _) => h.Stream.GetDataStream<string>(SelectedChangeId)
            .Select(key => BuildDiff(wt, userId, repoSlug, key))
            .Switch()
            .StartWith((UiControl?)SelectAChange()));

        return stack;
    }

    // ── change / commit lists (DataGrid, never hand-built HTML) ───────────────────────────────

    private static UiControl BuildWorkingTreeChanges(LayoutAreaHost host, WorkingTreeStatus status)
    {
        if (status.IsClean)
            return Controls.Markdown($"On **{status.Branch}** — working tree clean.");
        var stack = Controls.Stack.WithWidth("100%").WithStyle("gap:6px;");
        stack = stack.WithView(Controls.Markdown(
            $"On **{status.Branch}** — {status.Changes.Count} uncommitted change(s):"));
        stack = stack.WithView(BuildChangeGrid(host, WtRowsId, status.Changes, c => $"W{Sep}{c.Path}"));
        return stack;
    }

    private static UiControl BuildCommitDetail(LayoutAreaHost host, string hash, IReadOnlyList<GitFileChange> changes)
    {
        var stack = Controls.Stack.WithWidth("100%").WithStyle("gap:6px;");
        stack = stack.WithView(Controls.Markdown($"**Changed in `{Short(hash)}`** — {changes.Count} file(s):"));
        if (changes.Count == 0)
            return stack.WithView(Controls.Markdown("_No file changes._"));
        stack = stack.WithView(BuildChangeGrid(host, CommitFileRowsId, changes, c => $"C{Sep}{hash}{Sep}{c.Path}"));
        return stack;
    }

    private static UiControl BuildCommitLog(LayoutAreaHost host, IReadOnlyList<GitCommit> commits)
    {
        if (commits.Count == 0)
            return Controls.Markdown("_No commits._");

        var rows = commits.Select(c => new CommitRow(c.Hash, c.ShortHash, c.Date, c.Author, c.Subject)).ToList();
        host.UpdateData(CommitRowsId, rows);

        var grid = new DataGridControl(new JsonPointerReference(LayoutAreaReference.GetDataPointer(CommitRowsId)))
            .WithColumn(new PropertyColumnControl<string> { Property = "shortHash" }.WithTitle("Commit"))
            .WithColumn(new PropertyColumnControl<string> { Property = "date" }.WithTitle("Date"))
            .WithColumn(new PropertyColumnControl<string> { Property = "author" }.WithTitle("Author"))
            .WithColumn(new PropertyColumnControl<string> { Property = "subject" }.WithTitle("Subject"))
            .WithClickAction(HandleCommitClick);

        var stack = Controls.Stack.WithWidth("100%").WithStyle("gap:6px;").WithView(grid);
        if (commits.Count >= MaxCommits)
            stack = stack.WithView(Controls.Markdown($"_Showing the latest {MaxCommits} commits._"));
        return stack;
    }

    private static UiControl BuildChangeGrid(
        LayoutAreaHost host, string dataId, IReadOnlyList<GitFileChange> changes, Func<GitFileChange, string> keyFor)
    {
        var rows = changes.Select(c => new ChangeRow(c.Status, c.Path, keyFor(c))).ToList();
        host.UpdateData(dataId, rows);
        return new DataGridControl(new JsonPointerReference(LayoutAreaReference.GetDataPointer(dataId)))
            .WithColumn(new PropertyColumnControl<string> { Property = "status" }.WithTitle("Status"))
            .WithColumn(new PropertyColumnControl<string> { Property = "path" }.WithTitle("File"))
            .WithClickAction(HandleChangeClick);
    }

    // ── diff pane (DiffEditorControl) ─────────────────────────────────────────────────────────

    private static IObservable<UiControl?> BuildDiff(
        GitWorkingTreeService wt, string userId, string repoSlug, string? key)
    {
        if (string.IsNullOrEmpty(key))
            return Observable.Return((UiControl?)SelectAChange());

        var parts = key!.Split(Sep);
        // Working-tree change: HEAD (committed) vs the on-disk working copy.
        if (parts is ["W", var wtPath])
            return wt.ShowFile(userId, repoSlug, "HEAD", wtPath).Catch(Observable.Return(""))
                .CombineLatest(
                    wt.ReadFile(userId, repoSlug, wtPath).Catch(Observable.Return("")),
                    (head, work) => (UiControl?)BuildDiffEditor(wtPath, head, work, "HEAD", "Working tree"));
        // Commit change: the file at the commit's parent vs at the commit itself.
        if (parts is ["C", var hash, var cPath])
            return wt.ShowFile(userId, repoSlug, $"{hash}^", cPath).Catch(Observable.Return(""))
                .CombineLatest(
                    wt.ShowFile(userId, repoSlug, hash, cPath).Catch(Observable.Return("")),
                    (before, after) => (UiControl?)BuildDiffEditor(cPath, before, after, $"{Short(hash)}^", Short(hash)));

        return Observable.Return((UiControl?)SelectAChange());
    }

    private static UiControl BuildDiffEditor(
        string path, string original, string modified, string originalLabel, string modifiedLabel)
    {
        var stack = Controls.Stack.WithWidth("100%").WithStyle("gap:6px;");
        stack = stack.WithView(Controls.Markdown($"**`{path}`**"));
        stack = stack.WithView(new DiffEditorControl
        {
            OriginalContent = original,
            ModifiedContent = modified,
            OriginalLabel = originalLabel,
            ModifiedLabel = modifiedLabel,
            Language = WorkingTreeTab.LanguageFor(path),
            Height = "600px",
        });
        return stack;
    }

    // ── click handlers ────────────────────────────────────────────────────────────────────────

    private static void HandleCommitClick(UiActionContext ctx)
    {
        var row = ExtractRow<CommitRow>(ctx);
        if (row is null || string.IsNullOrEmpty(row.Hash)) return;
        ctx.Host.UpdateData(SelectedCommitId, row.Hash);
        ctx.Host.UpdateData(SelectedChangeId, ""); // reset the diff when switching commits
    }

    private static void HandleChangeClick(UiActionContext ctx)
    {
        var row = ExtractRow<ChangeRow>(ctx);
        if (row is null || string.IsNullOrEmpty(row.Key)) return;
        ctx.Host.UpdateData(SelectedChangeId, row.Key);
    }

    /// <summary>
    /// Reads the clicked DataGrid row as <typeparamref name="T"/>. The row records aren't registered
    /// in the type registry, so the cross-hub click round-trip may deliver
    /// <see cref="DataGridCellClick.Item"/> as the concrete record OR as a <see cref="JsonElement"/> —
    /// handle both by re-reading the JSON through the hub's serializer.
    /// </summary>
    private static T? ExtractRow<T>(UiActionContext ctx) where T : class
    {
        if (ctx.Payload is not DataGridCellClick { Item: { } item }) return null;
        if (item is T typed) return typed;
        var opts = ctx.Hub.JsonSerializerOptions;
        var json = item is JsonElement je ? je.GetRawText() : JsonSerializer.Serialize(item, opts);
        return JsonSerializer.Deserialize<T>(json, opts);
    }

    // ── helpers ─────────────────────────────────────────────────────────────────────────────

    // Fresh control per call — a shared instance would reuse a layout-area control Id across areas/emissions.
    private static UiControl NotCheckedOut() =>
        Controls.Markdown("_Not checked out yet — use **Check out / pull** above._");
    private static UiControl SelectAChange() =>
        Controls.Markdown("_Select a change to view its diff._");

    private static string Short(string hash) => hash.Length > 8 ? hash[..8] : hash;

    /// <summary>The commit-log grid row (camelCase property names match the DataGrid columns).</summary>
    public sealed record CommitRow(string Hash, string ShortHash, string Date, string Author, string Subject);

    /// <summary>A changed-file grid row; <see cref="Key"/> encodes the diff to show when clicked.</summary>
    public sealed record ChangeRow(string Status, string Path, string Key);
}
