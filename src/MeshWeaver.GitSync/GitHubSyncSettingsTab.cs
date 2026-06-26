using System.Reactive.Linq;
using System.Text.Json;
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
/// The "GitHub Sync" settings tab — the GUI for the whole feature. The feature acts on a
/// whole Space, so the tab appears on the Settings page of EVERY node within a Space (always
/// referring to the containing Space), not just on the Space root. It is hidden outside a Space
/// (user/Admin partitions, etc.) and for users who lack Update on the Space. Three sections:
/// <list type="number">
///   <item><b>Your GitHub account</b> — per-user OAuth Connect (device flow) / Disconnect.</item>
///   <item><b>Repository</b> — repo URL, branch (+ create-branch / create-repo toggles), subdirectory.</item>
///   <item><b>Sync</b> — "Sync now" (export), the stored synced commit, and an editable
///     commit field with "Re-import at this commit" (re-import the Space to that state).</item>
/// </list>
/// Mirrors <c>ModelsSettingsTab</c>: form data via <c>host.UpdateData</c> + bound controls,
/// click actions calling <see cref="GitHubSyncService"/>/<see cref="GitHubOAuthService"/>,
/// a live HTML status area, and a databound connect-state body.
/// </summary>
public static class GitHubSyncSettingsTab
{
    /// <summary>The settings-menu item id for the GitHub Sync tab.</summary>
    public const string TabId = "GitHubSync";

    private const string ResultId = "ghSyncResult";
    private const string CommitFormId = "ghReimportForm";
    // Holds the path of the PR draft the user is currently editing (empty = none yet).
    private const string PrPathId = "ghPrPath";
    // Holds the path of the currently-running operation activity (empty = none). The progress
    // panel binds to it; Cancel flips RequestedStatus on it.
    private const string ActivityPathId = "ghActivityPath";

    /// <summary>Registers the GitHub Sync settings tab provider (shown on any node within a Space).</summary>
    public static MessageHubConfiguration AddGitHubSyncSettingsTab(this MessageHubConfiguration config)
        => config.AddSettingsMenuItems(new SettingsMenuItemProvider(GetTab));

    private static IObservable<IReadOnlyList<SettingsMenuItemDefinition>> GetTab(
        LayoutAreaHost host, RenderingContext ctx)
    {
        IReadOnlyList<SettingsMenuItemDefinition> none = Array.Empty<SettingsMenuItemDefinition>();
        var tab = new SettingsMenuItemDefinition(
            Id: TabId,
            Label: "GitHub Sync",
            ContentBuilder: BuildContent,
            Group: "Integration",
            Icon: FluentIcons.Document(),
            GroupIcon: FluentIcons.Document(),
            Order: 250,
            // Visibility AND the Update check are gated on the CONTAINING SPACE below — not on the
            // current node. The feature rewrites/exports the whole Space, so the right permission to
            // require is Update on the Space, regardless of which node's Settings page we're on.
            RequiredPermission: Permission.None);

        // GitHub Sync acts on the whole Space, so the tab appears on the Settings page of EVERY node
        // within a Space, always referring to the containing Space. Spaces are top-level (a Space's
        // path IS its id — see SpaceNodeType), so the first segment of the current node's path is its
        // containing partition root. Gate on (that root is a Space) AND (the user may Update it).
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

    /// <summary>The partition root for a node path — its first segment. Spaces are top-level (their
    /// path IS their id), so the first segment of any node's path is its containing Space's path
    /// (when that partition is a Space). Empty for a null/empty path.</summary>
    private static string SpaceRootPath(string? path) =>
        string.IsNullOrEmpty(path) ? "" : path.Split('/', 2)[0];

    internal static UiControl BuildContent(LayoutAreaHost host, StackControl stack, MeshNode? node)
    {
        var sp = host.Hub.ServiceProvider;
        var sync = sp.GetRequiredService<GitHubSyncService>();
        var oauth = sp.GetRequiredService<GitHubOAuthService>();
        var creds = sp.GetRequiredService<GitHubCredentialService>();
        var prService = sp.GetRequiredService<PullRequestService>();
        var userId = sp.GetService<AccessService>()?.Context?.ObjectId ?? "";
        // The tab can render on ANY node within a Space (see GetTab); it always acts on the
        // CONTAINING Space. spacePath = the partition root (first path segment, where the _GitSync
        // config + all sync operations live); currentPath = the node whose Settings page we're on
        // (used only for the OAuth "return here" redirect so Connect brings the user back to this tab).
        var currentPath = node?.Path ?? "";
        var spacePath = SpaceRootPath(currentPath);

        if (string.IsNullOrEmpty(spacePath))
            return stack.WithView(Controls.Html("<p><em>GitHub Sync is available inside a Space.</em></p>"));

        stack = stack.WithView(Controls.H2("GitHub Sync").WithStyle("margin: 0 0 8px 0;"));
        stack = stack.WithView(Controls.Html(
            "<p style=\"font-size:0.85rem;color:var(--neutral-foreground-hint);margin-bottom:16px;\">" +
            "Export this Space to a GitHub repository, and re-import it at any commit. Your GitHub " +
            "connection is personal — commits are authored as you.</p>"));

        // Surface the OAuth connect outcome. The callback redirects back here with
        // ?connect=github-ok | github-error&reason=... (the query rides on host.Reference.Id);
        // show the REAL reason instead of silently bouncing the user back not-connected.
        var connectBanner = ConnectBanner(host.Reference.Id?.ToString());
        if (connectBanner is not null)
            stack = stack.WithView(connectBanner);

        // ── 1. Your GitHub account ────────────────────────────────────────────
        stack = stack.WithView(Section("Your GitHub account"));
        if (string.IsNullOrEmpty(userId))
            stack = stack.WithView(Controls.Html(
                "<p style=\"font-size:0.85rem;color:var(--neutral-foreground-hint);\">Sign in to connect a GitHub account.</p>"));
        else
            // Live-bind to the user's credential stream so the state flips to "Connected" the instant
            // the OAuth callback's saved credential syncs in. (A one-shot read grabbed the synced
            // query's empty pre-sync snapshot and showed "Not connected" right after connecting.)
            stack = stack.WithView((h, _) => creds.GetStream(userId)
                .Select(c => (UiControl?)RenderConnect(creds, userId, currentPath, c, oauth.IsConfigured))
                .StartWith((UiControl?)Controls.Html(
                    "<p style=\"font-size:0.85rem;color:var(--neutral-foreground-hint);\">Checking GitHub connection…</p>")));

        // ── 2. Repository ─────────────────────────────────────────────────────
        // The GitHub settings ARE a mesh node ({space}/_GitSync, GitHubSyncConfig). Edit it through
        // the STANDARD node-content editor, which binds the GUI client DIRECTLY to the node stream
        // (IMeshNodeStreamCache) and writes edits back via stream.Update — NO /data replica, NO save
        // subscription, NO Save button. Ensure the node exists first (create-on-absent), then just
        // DECLARE the editor bound to its path; all value resolution + persistence happen GUI-side.
        stack = stack.WithView(Section("Repository"));
        // Robust create-on-absent: gate the editor's render on EnsureConfigNode so a Space that was
        // NEVER configured gets its {space}/_GitSync node auto-created (via the safe GetQuery+CreateNode
        // path) BEFORE the editor binds to it — the editor then binds to an existing node instead of
        // spinning on (or storm-breaking against) an absent path. Any creation failure surfaces inline.
        stack = stack.WithView((h, _) => sync.EnsureConfigNode(spacePath)
            .Select(_ => (UiControl?)MeshNodeContentEditorControl.ForType(
                GitHubSyncService.ConfigPath(spacePath), typeof(GitHubSyncConfig)))
            .Catch<UiControl?, Exception>(ex => Observable.Return((UiControl?)Controls.Html(
                Err($"Couldn't initialize GitHub settings: {ex.Message}"))))
            .StartWith((UiControl?)Controls.Html(
                "<p style=\"font-size:0.85rem;color:var(--neutral-foreground-hint);\">Loading repository settings…</p>")));

        // ── 3. Sync + re-import ───────────────────────────────────────────────
        // Every long-running GitHub op runs as an ACTIVITY (Doc/Architecture/ActivityControlPlane):
        // the click calls the unified hub extension, which creates an activity + returns its path;
        // we stash the path so the progress panel below binds to it (live Messages/Status + Cancel).
        stack = stack.WithView(Section("Sync"));
        stack = stack.WithView(Controls.Button("Sync now (commit)")
            .WithAppearance(Appearance.Accent)
            .WithClickAction(ctx =>
            {
                ctx.Host.Hub.CommitToGitHub(spacePath, userId,
                        onActivityCreated: path => ctx.Host.UpdateData(ActivityPathId, path))
                    .Subscribe(_ => { }, ex => ctx.Host.UpdateData(ResultId, Err(ex.Message)));
                return Task.CompletedTask;
            }));
        stack = stack.WithView(Controls.Button("Update to latest (checkout)")
            .WithAppearance(Appearance.Outline)
            .WithClickAction(ctx =>
            {
                ctx.Host.Hub.UpdateToLatestFromGitHub(spacePath, userId,
                        onActivityCreated: path => ctx.Host.UpdateData(ActivityPathId, path))
                    .Subscribe(_ => { }, ex => ctx.Host.UpdateData(ResultId, Err(ex.Message)));
                return Task.CompletedTask;
            }));
        stack = stack.WithView(Controls.Button("Check branch on GitHub")
            .WithAppearance(Appearance.Outline)
            .WithClickAction(ctx =>
            {
                ctx.Host.Hub.CheckBranchStateOnGitHub(spacePath, userId,
                        onActivityCreated: path => ctx.Host.UpdateData(ActivityPathId, path))
                    .Subscribe(_ => { }, ex => ctx.Host.UpdateData(ResultId, Err(ex.Message)));
                return Task.CompletedTask;
            }));

        // Live activity progress panel: binds to the running operation's activity node — its
        // Messages stream live, the terminal Status shows the outcome, and Cancel flips
        // RequestedStatus = Cancelled (Activity Control Plane). Empty until an op starts.
        stack = stack.WithView((h, _) => h.Stream.GetDataStream<string>(ActivityPathId)
            .Select(path => (UiControl?)BuildActivityPanel(h, path))
            .StartWith((UiControl?)Controls.Stack.WithWidth("100%")));

        // Last-synced status (live — re-renders after each sync via the authoritative cache stream).
        stack = stack.WithView((h, _) => sync.WatchConfig(spacePath)
            .Select(cfg => (UiControl?)Controls.Html(LastSyncedHtml(cfg)))
            .StartWith((UiControl?)Controls.Html("<p style=\"color:var(--neutral-foreground-hint);\">…</p>")));

        // Editable commit + re-import. Prefill from the Space's saved config once it arrives (same
        // synced-query empty-first-emission caveat as the repo form).
        host.UpdateData(CommitFormId, new Dictionary<string, object?> { ["commit"] = "main" });
        host.RegisterForDisposal("gh-commit-prefill", sync.WatchConfig(spacePath)
            .Where(c => c is not null)
            .Take(1)
            .Subscribe(cfg => host.UpdateData(CommitFormId, new Dictionary<string, object?>
            {
                ["commit"] = cfg!.LastSyncCommitSha ?? cfg.Branch ?? "main",
            })));
        stack = stack.WithView(BuildReimportForm(spacePath, userId));

        // ── 4. Pull request (AI-drafted → user edits the bound node → submit) ──
        stack = stack.WithView(Section("Pull request"));
        stack = stack.WithView(Controls.Html(
            "<p style=\"font-size:0.85rem;color:var(--neutral-foreground-hint);margin:0 0 8px 0;\">" +
            "Draft a pull request with AI, edit the title and body, then submit it to GitHub. " +
            "The draft is a mesh node bound directly to the editor below — your edits save as you type.</p>"));

        // "Draft pull request" — AI drafts title+body and creates a draft PR node, then we point
        // the editor at that node by stashing its path in the PrPathId data id.
        stack = stack.WithView(Controls.Button("Draft pull request with AI")
            .WithAppearance(Appearance.Accent)
            .WithClickAction(ctx =>
            {
                ctx.Host.UpdateData(ResultId, Pending("Asking the agent to draft a pull request…"));
                prService.CreateDraft(spacePath, headBranch: null, baseBranch: "main").Subscribe(
                    prNode =>
                    {
                        ctx.Host.UpdateData(PrPathId, prNode.Path);
                        ctx.Host.UpdateData(ResultId, Ok("Draft created — edit the title and body below, then Submit."));
                    },
                    ex => ctx.Host.UpdateData(ResultId, Err(ex.Message)));
                return Task.CompletedTask;
            }));

        // The PR editor + status, bound to the draft path the button stashes. Re-renders whenever
        // PrPathId changes (a new draft) — the node-content editor itself live-binds to the node
        // stream for Title/Body, and the status/link row binds to the same node's content.
        stack = stack.WithView((h, _) => h.Stream.GetDataStream<string>(PrPathId)
            .Select(prPath => (UiControl?)BuildPullRequestEditor(prService, spacePath, prPath, userId))
            .StartWith((UiControl?)Controls.Stack.WithWidth("100%")));

        // ── Result area ───────────────────────────────────────────────────────
        stack = stack.WithView((h, _) => h.Stream.GetDataStream<string>(ResultId)
            .Select(html => string.IsNullOrEmpty(html)
                ? (UiControl?)Controls.Stack.WithWidth("100%")
                : (UiControl?)Controls.Stack.WithWidth("100%").WithView(Controls.Html(html)))
            .StartWith((UiControl?)Controls.Stack.WithWidth("100%")));

        return stack;
    }

    // ── Connect (OAuth authorization-code / callback flow) ─────────────────────

    private static UiControl RenderConnect(
        GitHubCredentialService creds, string userId, string returnNodePath, GitHubCredential? cred, bool isConfigured)
    {
        if (cred is { AccessToken.Length: > 0 } || cred is { GitHubLogin.Length: > 0 })
        {
            var who = string.IsNullOrEmpty(cred!.GitHubLogin) ? "" : $" as <strong>{Esc(cred.GitHubLogin!)}</strong>";
            var body = Controls.Stack.WithOrientation(Orientation.Horizontal).WithStyle("gap:16px;align-items:center;");
            body = body.WithView(Controls.Html(
                $"<div style=\"flex:1;font-size:0.9rem;\"><span style=\"color:#22c55e;\">✓</span> Connected{who}</div>"));
            body = body.WithView(Controls.Button("Disconnect")
                .WithAppearance(Appearance.Outline)
                .WithClickAction(ctx =>
                {
                    // The connect-state view live-binds to creds.GetStream(userId); deleting the
                    // credential re-emits null and the body flips to "Not connected" on its own.
                    creds.Delete(userId).Subscribe(
                        _ => { },
                        ex => ctx.Host.UpdateData(ResultId, Err(ex.Message)));
                    return Task.CompletedTask;
                }));
            return body;
        }

        if (!isConfigured)
            return Controls.Html(
                "<p style=\"font-size:0.85rem;color:var(--neutral-foreground-hint);\">" +
                "GitHub OAuth is not configured on this server (set <code>GitHub:OAuth:ClientId</code> + " +
                "<code>ClientSecret</code>).</p>");

        // The OAuth flow is a browser redirect to the server endpoint /connect/github, which posts
        // back to /connect/github/callback, stores the token, and returns here. This MUST be a full
        // browser navigation — a plain in-app link is intercepted by the Blazor router and resolved
        // as a mesh path ("page not found"). target="_top" opts the anchor out of Blazor's link
        // interception so the browser does a real navigation to the minimal-API endpoint (the same
        // intent as NavigateTo(forceLoad:true) used by the login button).
        var returnPath = $"/{returnNodePath}/Settings/{TabId}";
        var connectUrl = $"/connect/github?returnPath={Uri.EscapeDataString(returnPath)}";
        return Controls.Html(
            "<div style=\"font-size:0.85rem;display:flex;align-items:center;gap:8px;\">" +
            "<span style=\"display:inline-block;width:8px;height:8px;border-radius:50%;" +
            "background:var(--neutral-stroke-strong-rest);flex:0 0 auto;\"></span>" +
            "<span>Not connected. " +
            $"<a href=\"{Esc(connectUrl)}\" target=\"_top\" rel=\"noopener\" " +
            "style=\"color:var(--accent-fill-rest);font-weight:600;\">Connect GitHub →</a>" +
            " (one-time browser approval; authorize for the org whose repos you'll sync).</span></div>");
    }

    // ── Re-import form (transient action input — a commit/branch to import to, not node content) ──

    private static UiControl BuildReimportForm(string spacePath, string userId)
    {
        var row = Controls.Stack.WithOrientation(Orientation.Horizontal).WithStyle("gap:8px;align-items:flex-end;");
        row = row.WithView(new TextFieldControl(new JsonPointerReference("commit"))
        {
            Label = "Commit or branch to import",
            Placeholder = "commit SHA or branch",
            DataContext = LayoutAreaReference.GetDataPointer(CommitFormId),
        }.WithWidth("320px"));
        row = row.WithView(Controls.Button("Re-import at this commit")
            .WithAppearance(Appearance.Outline)
            .WithClickAction(ctx =>
            {
                ctx.Host.Stream.GetDataStream<Dictionary<string, object?>>(CommitFormId).Take(1).Subscribe(d =>
                {
                    var commit = Str(d, "commit");
                    if (string.IsNullOrEmpty(commit))
                    {
                        ctx.Host.UpdateData(ResultId, Err("Enter a commit SHA or branch to re-import."));
                        return;
                    }
                    // Runs as an activity (progress + cancel via the panel above).
                    ctx.Host.Hub.ReimportFromGitHub(spacePath, commit, userId,
                            onActivityCreated: path => ctx.Host.UpdateData(ActivityPathId, path))
                        .Subscribe(_ => { }, ex => ctx.Host.UpdateData(ResultId, Err(ex.Message)));
                });
                return Task.CompletedTask;
            }));
        return row;
    }

    // ── Activity progress panel (binds to the running operation's activity node) ──

    /// <summary>
    /// Builds the live progress panel for the running operation at <paramref name="activityPath"/>:
    /// streams the activity's <see cref="MeshWeaver.Data.LogMessage"/> lines and terminal
    /// <see cref="MeshWeaver.Data.ActivityStatus"/>, with a Cancel button that flips
    /// <c>RequestedStatus = Cancelled</c> (Activity Control Plane). Empty until an op starts.
    /// </summary>
    private static UiControl BuildActivityPanel(LayoutAreaHost host, string? activityPath)
    {
        var stack = Controls.Stack.WithWidth("100%");
        if (string.IsNullOrEmpty(activityPath))
            return stack;

        // Live Messages + Status, bound to the activity node (re-renders on every progress tick).
        stack = stack.WithView((h, _) => h.Hub.GetWorkspace().GetMeshNodeStream(activityPath)
            .Select(node => (UiControl?)Controls.Html(ActivityHtml(node?.Content as MeshWeaver.Data.ActivityLog)))
            .StartWith((UiControl?)Controls.Html("")));

        // Cancel — flips RequestedStatus = Cancelled; the runner's watcher trips the command's token.
        stack = stack.WithView((h, _) => h.Hub.GetWorkspace().GetMeshNodeStream(activityPath)
            .Select(node => (node?.Content as MeshWeaver.Data.ActivityLog)?.Status)
            .Select(status => (UiControl?)(status == MeshWeaver.Data.ActivityStatus.Running
                ? Controls.Button("Cancel")
                    .WithAppearance(Appearance.Outline)
                    .WithClickAction(ctx => { ctx.Host.Hub.CancelActivity(activityPath); return Task.CompletedTask; })
                : Controls.Stack))
            .StartWith((UiControl?)Controls.Stack));
        return stack;
    }

    private static string ActivityHtml(MeshWeaver.Data.ActivityLog? log)
    {
        if (log is null) return "";
        var colour = log.Status switch
        {
            MeshWeaver.Data.ActivityStatus.Running => "var(--neutral-foreground-hint)",
            MeshWeaver.Data.ActivityStatus.Succeeded => "#4ade80",
            MeshWeaver.Data.ActivityStatus.Failed => "#f87171",
            MeshWeaver.Data.ActivityStatus.Cancelled => "#fbbf24",
            _ => "var(--neutral-foreground-hint)",
        };
        var lines = string.Join("", log.Messages.TakeLast(8).Select(m =>
            $"<div style=\"font-family:monospace;font-size:0.8rem;\">{Esc(m.Message)}</div>"));
        return $"<div style=\"padding:8px 12px;background:var(--neutral-layer-2);border-radius:6px;\">" +
               $"<div style=\"font-weight:600;color:{colour};margin-bottom:4px;\">{Esc(log.Status.ToString())}</div>" +
               $"{lines}</div>";
    }

    // ── Pull-request editor (the draft node IS the binding anchor) ──────────────

    /// <summary>
    /// Builds the PR editor for the draft at <paramref name="prPath"/>: the standard
    /// node-content editor (Title/Body, bound DIRECTLY to the node stream — no /data replica,
    /// no save subscription), a Submit button that opens the PR on GitHub and writes back ONLY the
    /// immutable handle (number + url), a "Check status on GitHub" button that asks GitHub LIVE
    /// (status is never stored — never replicated), and a link row bound to the node's handle.
    /// Empty until a draft is created.
    /// </summary>
    private static UiControl BuildPullRequestEditor(
        PullRequestService prService, string spacePath, string? prPath, string userId)
    {
        var stack = Controls.Stack.WithWidth("100%").WithStyle("gap:12px;");
        if (string.IsNullOrEmpty(prPath))
            return stack;

        // Title + Body editable fields, bound directly to the PR node stream.
        stack = stack.WithView(
            MeshNodeContentEditorControl.ForType(prPath, typeof(GitHubPullRequest)));

        // Action row: Submit (open on GitHub) + Check status (asks GitHub live).
        var actions = Controls.Stack.WithOrientation(Orientation.Horizontal).WithStyle("gap:8px;align-items:center;");
        actions = actions.WithView(Controls.Button("Submit pull request")
            .WithAppearance(Appearance.Accent)
            .WithClickAction(ctx =>
            {
                // Runs as an activity (progress + cancel shown in the Sync section's activity panel).
                ctx.Host.Hub.OpenPullRequestOnGitHub(spacePath, prPath, userId,
                        onActivityCreated: path => ctx.Host.UpdateData(ActivityPathId, path))
                    .Subscribe(_ => { }, ex => ctx.Host.UpdateData(ResultId, Err(ex.Message)));
                return Task.CompletedTask;
            }));
        // Status is GitHub-owned: we ASK GitHub live, never store/replicate it.
        actions = actions.WithView(Controls.Button("Check status on GitHub")
            .WithAppearance(Appearance.Outline)
            .WithClickAction(ctx =>
            {
                ctx.Host.UpdateData(ResultId, Pending("Asking GitHub for the pull-request status…"));
                prService.AskStatus(spacePath, prPath, userId).Subscribe(
                    info => ctx.Host.UpdateData(ResultId, Ok($"GitHub reports this pull request is {info.Status}.")),
                    ex => ctx.Host.UpdateData(ResultId, Err(ex.Message)));
                return Task.CompletedTask;
            }));
        stack = stack.WithView(actions);

        // Link row, bound to the PR node's immutable handle (number + url). The status is NOT shown
        // from a stored field — use "Check status on GitHub" to read it live.
        stack = stack.WithView((h, _) => prService.WatchPullRequest(prPath)
            .Select(pr => (UiControl?)Controls.Html(PullRequestLinkHtml(pr)))
            .StartWith((UiControl?)Controls.Html("")));
        return stack;
    }

    private static string PullRequestLinkHtml(GitHubPullRequest? pr)
    {
        if (pr is null) return "";
        if (pr.Number is { } n && pr.Url is { Length: > 0 } url)
            return "<p style=\"font-size:0.85rem;\">Pull request " +
                   $"<a href=\"{Esc(url)}\" target=\"_top\" rel=\"noopener\" " +
                   $"style=\"color:var(--accent-fill-rest);font-weight:600;\">#{n} ↗</a> opened on GitHub — " +
                   "use <em>Check status on GitHub</em> for its current state.</p>";
        return "<p style=\"font-size:0.85rem;color:var(--neutral-foreground-hint);\">Draft — not yet opened on GitHub.</p>";
    }

    // ── small helpers ─────────────────────────────────────────────────────────

    private static string LastSyncedHtml(GitHubSyncConfig? cfg) =>
        cfg?.LastSyncCommitSha is { Length: > 0 } sha
            ? $"<p style=\"font-size:0.85rem;color:var(--neutral-foreground-hint);\">Last synced: " +
              $"{cfg.LastSyncedAt:yyyy-MM-dd HH:mm} UTC — commit <span style=\"font-family:monospace;\">{Esc(sha)}</span></p>"
            : "<p style=\"font-size:0.85rem;color:var(--neutral-foreground-hint);\">Not synced yet.</p>";

    private static UiControl Section(string title) =>
        Controls.Html($"<h3 style=\"margin:20px 0 8px 0;font-size:1rem;\">{Esc(title)}</h3>");

    private static string? Str(Dictionary<string, object?> d, string key)
    {
        var v = d.GetValueOrDefault(key);
        var s = v is JsonElement je && je.ValueKind == JsonValueKind.String ? je.GetString() : v?.ToString();
        return string.IsNullOrWhiteSpace(s) ? null : s.Trim();
    }

    /// <summary>Renders a banner for the OAuth connect outcome carried on the area id's query
    /// (<c>?connect=github-ok|github-error&amp;reason=...</c>), or null when there's no connect result.</summary>
    private static UiControl? ConnectBanner(string? referenceId)
    {
        if (string.IsNullOrEmpty(referenceId) || !referenceId.Contains('?')) return null;
        var query = System.Web.HttpUtility.ParseQueryString(referenceId[(referenceId.IndexOf('?') + 1)..]);
        return query["connect"] switch
        {
            "github-ok" => Controls.Html(Ok("GitHub connected.")),
            "github-error" => Controls.Html(Err("GitHub connect failed" +
                (string.IsNullOrEmpty(query["reason"]) ? "." : $": {query["reason"]}"))),
            _ => null,
        };
    }

    private static string Short(string sha) => string.IsNullOrEmpty(sha) ? "" : sha[..Math.Min(8, sha.Length)];
    private static string Esc(string s) => System.Web.HttpUtility.HtmlEncode(s);

    private static string Ok(string m) =>
        $"<p style=\"padding:8px 12px;color:#4ade80;background:var(--neutral-layer-2);border-radius:6px;\">{Esc(m)}</p>";
    private static string Err(string m) =>
        $"<p style=\"padding:8px 12px;color:#f87171;background:var(--neutral-layer-2);border-radius:6px;\">{Esc(m)}</p>";
    private static string Pending(string m) =>
        $"<p style=\"padding:8px 12px;color:var(--neutral-foreground-hint);background:var(--neutral-layer-2);border-radius:6px;\">{Esc(m)}</p>";
}
