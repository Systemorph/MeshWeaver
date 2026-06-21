using System.Reactive.Linq;
using MeshWeaver.Application.Styles;
using MeshWeaver.Data;
using MeshWeaver.Graph;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.GitSync;

/// <summary>
/// The "Code workspace" settings tab — the GUI for the on-disk working tree
/// (<see cref="GitWorkingTreeService"/>). It checks out the Space's connected GitHub repo
/// (the <see cref="GitHubSyncConfig.RepositoryUrl"/> the GitHub-Sync tab configures), lists its
/// files, opens one in a Monaco editor, and commits + pushes the edit as the user. The same
/// per-user working tree is what the co-hosted AI CLIs operate on.
///
/// <para>Mirrors <see cref="GitHubSyncSettingsTab"/>: visible only inside a Space to users with
/// Update; form/selection state via <c>host.UpdateData</c> + data-bound sub-views; click actions
/// call the reactive service and Subscribe. The editor reuses the exact
/// <see cref="CodeEditorControl"/> data-binding pattern from <c>CodeLayoutAreas.Edit</c>.</para>
/// </summary>
public static class WorkingTreeTab
{
    public const string TabId = "CodeWorkspace";

    private const string ResultId = "wtResult";
    private const string RefreshId = "wtRefresh";       // bumped after checkout/commit → re-queries status + file list
    private const string FilterId = "wtFilter";         // file-list filter text
    private const string SelectedFileId = "wtSelected"; // repo-relative path of the open file ("" = none)
    private const string EditorContentId = "wtContent"; // Monaco-bound editor value

    /// <summary>Registers the Code-workspace settings tab provider (shown on any node within a Space).</summary>
    public static MessageHubConfiguration AddWorkingTreeTab(this MessageHubConfiguration config)
        => config.AddSettingsMenuItems(new SettingsMenuItemProvider(GetTab));

    private static IObservable<IReadOnlyList<SettingsMenuItemDefinition>> GetTab(
        LayoutAreaHost host, RenderingContext ctx)
    {
        IReadOnlyList<SettingsMenuItemDefinition> none = Array.Empty<SettingsMenuItemDefinition>();
        var tab = new SettingsMenuItemDefinition(
            Id: TabId,
            Label: "Code workspace",
            ContentBuilder: BuildContent,
            Group: "Integration",
            Icon: FluentIcons.Code(),
            GroupIcon: FluentIcons.Document(),
            Order: 260,
            RequiredPermission: Permission.None);

        // Same gate as GitHub Sync: visible inside a Space to users who may Update it.
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
        var workingTrees = sp.GetRequiredService<GitWorkingTreeService>();
        var sync = sp.GetRequiredService<GitHubSyncService>();
        var userId = sp.GetService<AccessService>()?.Context?.ObjectId ?? "";
        var spacePath = SpaceRootPath(node?.Path ?? "");

        if (string.IsNullOrEmpty(spacePath))
            return stack.WithView(Controls.Html(Hint("Code workspace is available inside a Space.")));
        if (string.IsNullOrEmpty(userId))
            return stack.WithView(Controls.Html(Hint("Sign in to use the code workspace.")));

        // Seed the selection/refresh state once so the sub-views below have a stream to bind to.
        host.UpdateData(RefreshId, "0");
        host.UpdateData(FilterId, "");
        host.UpdateData(SelectedFileId, "");

        stack = stack.WithView(Controls.H2("Code workspace").WithStyle("margin:0 0 8px 0;"));
        stack = stack.WithView(Controls.Html(Hint(
            "Check out this Space's connected GitHub repository as a working tree, edit a file, and " +
            "commit + push as yourself. The same checkout is what the AI assistants operate on.")));

        // Everything depends on the Space's connected repo (RepositoryUrl + Branch) from the GitHub-Sync config.
        stack = stack.WithView((h, _) => sync.WatchConfig(spacePath)
            .Select(cfg => (UiControl?)BuildWorkspace(workingTrees, userId, cfg))
            .StartWith((UiControl?)Controls.Html(Hint("Loading repository settings…"))));

        // Result/status line.
        stack = stack.WithView((h, _) => h.Stream.GetDataStream<string>(ResultId)
            .Select(html => (UiControl?)(string.IsNullOrEmpty(html)
                ? Controls.Stack.WithWidth("100%")
                : Controls.Stack.WithWidth("100%").WithView(Controls.Html(html))))
            .StartWith((UiControl?)Controls.Stack.WithWidth("100%")));

        return stack;
    }

    private static UiControl BuildWorkspace(GitWorkingTreeService wt, string userId, GitHubSyncConfig? cfg)
    {
        var repoFullName = RepoFullName(cfg?.RepositoryUrl);
        if (repoFullName is null)
            return Controls.Html(Hint("Connect a GitHub repository in the GitHub Sync tab first."));

        var repoSlug = RepoSlug(repoFullName);
        var branch = string.IsNullOrWhiteSpace(cfg!.Branch) ? "main" : cfg.Branch;

        var stack = Controls.Stack.WithWidth("100%").WithStyle("gap:12px;");

        // Checkout / pull.
        stack = stack.WithView(Controls.Button($"Check out {repoFullName} ({branch})")
            .WithAppearance(Appearance.Accent)
            .WithIconStart(FluentIcons.ArrowDownload())
            .WithClickAction(ctx =>
            {
                ctx.Host.UpdateData(ResultId, Pending($"Checking out {repoFullName}…"));
                wt.Checkout(userId, repoFullName, branch).Subscribe(
                    tree =>
                    {
                        ctx.Host.UpdateData(ResultId, Ok($"Checked out {repoFullName} on {tree.Branch}."));
                        ctx.Host.UpdateData(RefreshId, Guid.NewGuid().ToString("N"));
                    },
                    ex => ctx.Host.UpdateData(ResultId, Err(ex.Message)));
                return Task.CompletedTask;
            }));

        // Live status (branch + pending changes), re-queried on each refresh.
        stack = stack.WithView((h, _) => h.Stream.GetDataStream<string>(RefreshId)
            .SelectMany(_ => wt.Status(userId, repoSlug)
                .Select(s => (UiControl?)Controls.Html(StatusHtml(s)))
                .Catch<UiControl?, Exception>(_ => Observable.Return((UiControl?)Controls.Html(
                    Hint("Not checked out yet — use the button above.")))))
            .StartWith((UiControl?)Controls.Html(Hint("…"))));

        // File filter.
        stack = stack.WithView(new TextFieldControl(new JsonPointerReference(""))
            .WithPlaceholder("Filter files…")
            .WithImmediate(true) with
        { DataContext = LayoutAreaReference.GetDataPointer(FilterId) });

        // File list (clickable) — re-queried on refresh, filtered live by the filter box.
        stack = stack.WithView((h, _) => h.Stream.GetDataStream<string>(RefreshId)
            .SelectMany(_ => wt.ListFiles(userId, repoSlug)
                .Catch<IReadOnlyList<string>, Exception>(_ => Observable.Return((IReadOnlyList<string>)Array.Empty<string>())))
            .CombineLatest(h.Stream.GetDataStream<string>(FilterId).StartWith(""), (files, filter) => (files, filter))
            .Select(x => (UiControl?)BuildFileList(wt, userId, repoSlug, x.files, x.filter))
            .StartWith((UiControl?)Controls.Html(Hint("Loading files…"))));

        // Editor pane for the selected file (re-renders when a different file is opened).
        stack = stack.WithView((h, _) => h.Stream.GetDataStream<string>(SelectedFileId)
            .Select(path => (UiControl?)(string.IsNullOrEmpty(path)
                ? Controls.Html(Hint("Select a file to edit."))
                : BuildEditorPane(wt, userId, repoSlug, branch, path)))
            .StartWith((UiControl?)Controls.Html(Hint("Select a file to edit."))));

        return stack;
    }

    private static UiControl BuildFileList(
        GitWorkingTreeService wt, string userId, string repoSlug, IReadOnlyList<string> files, string? filter)
    {
        const int cap = 200;
        var matched = string.IsNullOrWhiteSpace(filter)
            ? files
            : files.Where(f => f.Contains(filter!, StringComparison.OrdinalIgnoreCase)).ToArray();

        var list = Controls.Stack.WithWidth("100%")
            .WithStyle("max-height:240px;overflow:auto;gap:2px;border:1px solid var(--neutral-stroke-rest);border-radius:6px;padding:6px;");

        if (matched.Count == 0)
        {
            return list.WithView(Controls.Html(Hint(files.Count == 0
                ? "No files (check out the repository first)."
                : "No files match the filter.")));
        }

        foreach (var file in matched.Take(cap))
        {
            var path = file; // capture per-iteration for the click closure
            list = list.WithView(Controls.Button(path)
                .WithAppearance(Appearance.Stealth)
                .WithStyle("justify-content:flex-start;font-family:monospace;font-size:0.8rem;")
                .WithClickAction(ctx =>
                {
                    // Read the file, seed the editor content, THEN flip the selected path so the editor
                    // pane re-renders with the content already in place.
                    wt.ReadFile(userId, repoSlug, path).Subscribe(
                        content =>
                        {
                            ctx.Host.UpdateData(EditorContentId, content);
                            ctx.Host.UpdateData(SelectedFileId, path);
                        },
                        ex => ctx.Host.UpdateData(ResultId, Err(ex.Message)));
                    return Task.CompletedTask;
                }));
        }

        if (matched.Count > cap)
            list = list.WithView(Controls.Html(Hint($"… {matched.Count - cap} more — refine the filter.")));

        return list;
    }

    private static UiControl BuildEditorPane(
        GitWorkingTreeService wt, string userId, string repoSlug, string branch, string path)
    {
        var stack = Controls.Stack.WithWidth("100%").WithStyle("gap:8px;");
        stack = stack.WithView(Controls.Html(
            $"<div style=\"font-family:monospace;font-size:0.85rem;font-weight:600;\">{Esc(path)}</div>"));

        // Monaco editor bound to the editor-content data id (same pattern as CodeLayoutAreas.Edit).
        var editor = new CodeEditorControl()
            .WithLanguage(LanguageFor(path))
            .WithHeight("500px")
            .WithLineNumbers(true)
            .WithMinimap(false)
            .WithWordWrap(false) with
        {
            DataContext = LayoutAreaReference.GetDataPointer(EditorContentId),
            Value = new JsonPointerReference(""),
        };
        stack = stack.WithView(editor);

        stack = stack.WithView(Controls.Button("Commit & push")
            .WithAppearance(Appearance.Accent)
            .WithIconStart(FluentIcons.Save())
            .WithClickAction(ctx =>
            {
                ctx.Host.Stream.GetDataStream<string>(EditorContentId).Take(1).Subscribe(content =>
                {
                    ctx.Host.UpdateData(ResultId, Pending($"Committing {path}…"));
                    wt.WriteFile(userId, repoSlug, path, content ?? "")
                        .SelectMany(_ => wt.CommitAndPush(userId, repoSlug, $"Edit {path} (via portal)", branch))
                        .Subscribe(
                            r =>
                            {
                                ctx.Host.UpdateData(ResultId, r.Ok
                                    ? Ok($"Committed & pushed {path}.")
                                    : Err(r.Message));
                                ctx.Host.UpdateData(RefreshId, Guid.NewGuid().ToString("N"));
                            },
                            ex => ctx.Host.UpdateData(ResultId, Err(ex.Message)));
                });
                return Task.CompletedTask;
            }));

        return stack;
    }

    // ── small helpers ─────────────────────────────────────────────────────────

    private static string StatusHtml(WorkingTreeStatus s)
    {
        if (s.IsClean)
            return $"<div style=\"font-size:0.85rem;color:var(--neutral-foreground-hint);\">On <strong>{Esc(s.Branch)}</strong> — clean.</div>";
        var changes = string.Join("", s.Changes.Take(20).Select(c =>
            $"<div style=\"font-family:monospace;font-size:0.8rem;\">{Esc(c.Status)} {Esc(c.Path)}</div>"));
        return $"<div style=\"padding:6px 10px;background:var(--neutral-layer-2);border-radius:6px;\">" +
               $"<div style=\"font-size:0.85rem;margin-bottom:4px;\">On <strong>{Esc(s.Branch)}</strong> — {s.Changes.Count} pending change(s):</div>" +
               $"{changes}</div>";
    }

    /// <summary>Monaco language id from a file extension.</summary>
    private static string LanguageFor(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        return ext switch
        {
            ".cs" => "csharp",
            ".ts" => "typescript",
            ".js" => "javascript",
            ".json" => "json",
            ".md" => "markdown",
            ".css" => "css",
            ".html" or ".cshtml" or ".razor" => "html",
            ".xml" or ".csproj" or ".props" or ".targets" or ".slnx" => "xml",
            ".yml" or ".yaml" => "yaml",
            ".sh" => "shell",
            ".sql" => "sql",
            ".py" => "python",
            _ => "plaintext",
        };
    }

    /// <summary>Parses an <c>owner/repo</c> full name from a GitHub repo URL.</summary>
    private static string? RepoFullName(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        var u = url.Trim();
        var idx = u.IndexOf("github.com/", StringComparison.OrdinalIgnoreCase);
        var rest = (idx >= 0 ? u[(idx + "github.com/".Length)..] : u).TrimEnd('/');
        if (rest.EndsWith(".git", StringComparison.OrdinalIgnoreCase)) rest = rest[..^4];
        var parts = rest.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 2 ? $"{parts[0]}/{parts[1]}" : null;
    }

    private static string RepoSlug(string repoFullName)
    {
        var slash = repoFullName.LastIndexOf('/');
        return slash >= 0 ? repoFullName[(slash + 1)..] : repoFullName;
    }

    private static string Esc(string s) => System.Web.HttpUtility.HtmlEncode(s);
    private static string Hint(string m) =>
        $"<p style=\"font-size:0.85rem;color:var(--neutral-foreground-hint);margin:4px 0;\">{Esc(m)}</p>";
    private static string Ok(string m) =>
        $"<p style=\"padding:8px 12px;color:#4ade80;background:var(--neutral-layer-2);border-radius:6px;\">{Esc(m)}</p>";
    private static string Err(string m) =>
        $"<p style=\"padding:8px 12px;color:#f87171;background:var(--neutral-layer-2);border-radius:6px;\">{Esc(m)}</p>";
    private static string Pending(string m) =>
        $"<p style=\"padding:8px 12px;color:var(--neutral-foreground-hint);background:var(--neutral-layer-2);border-radius:6px;\">{Esc(m)}</p>";
}
