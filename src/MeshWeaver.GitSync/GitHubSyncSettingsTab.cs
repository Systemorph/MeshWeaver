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
/// The Space-settings "GitHub Sync" tab — the GUI for the whole feature. Shows only
/// on Space nodes. Three sections:
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
    public const string TabId = "GitHubSync";

    private const string ResultId = "ghSyncResult";
    private const string CommitFormId = "ghReimportForm";

    /// <summary>Registers the GitHub Sync settings tab provider (shown only on Space nodes).</summary>
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
            RequiredPermission: Permission.Update);

        return host.Workspace.GetMeshNodeStream()
            .Select(node => string.Equals(node?.NodeType, GitHubSyncService.SpaceNodeType, StringComparison.Ordinal)
                ? (IReadOnlyList<SettingsMenuItemDefinition>)new[] { tab }
                : none)
            .DistinctUntilChanged()
            .Catch<IReadOnlyList<SettingsMenuItemDefinition>, Exception>(_ => Observable.Return(none))
            .StartWith(none);
    }

    internal static UiControl BuildContent(LayoutAreaHost host, StackControl stack, MeshNode? node)
    {
        var sp = host.Hub.ServiceProvider;
        var sync = sp.GetRequiredService<GitHubSyncService>();
        var oauth = sp.GetRequiredService<GitHubOAuthService>();
        var creds = sp.GetRequiredService<GitHubCredentialService>();
        var userId = sp.GetService<AccessService>()?.Context?.ObjectId ?? "";
        var spacePath = node?.Path ?? "";

        if (!string.Equals(node?.NodeType, GitHubSyncService.SpaceNodeType, StringComparison.Ordinal))
            return stack.WithView(Controls.Html("<p><em>GitHub Sync is available on Space nodes.</em></p>"));

        stack = stack.WithView(Controls.H2("GitHub Sync").WithStyle("margin: 0 0 8px 0;"));
        stack = stack.WithView(Controls.Html(
            "<p style=\"font-size:0.85rem;color:var(--neutral-foreground-hint);margin-bottom:16px;\">" +
            "Export this Space to a GitHub repository, and re-import it at any commit. Your GitHub " +
            "connection is personal — commits are authored as you.</p>"));

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
                .Select(c => (UiControl?)RenderConnect(creds, userId, spacePath, c, oauth.IsConfigured))
                .StartWith((UiControl?)Controls.Html(
                    "<p style=\"font-size:0.85rem;color:var(--neutral-foreground-hint);\">Checking GitHub connection…</p>")));

        // ── 2. Repository ─────────────────────────────────────────────────────
        // The GitHub settings ARE a mesh node ({space}/_GitSync, GitHubSyncConfig). Edit it through
        // the STANDARD node-content editor, which binds the GUI client DIRECTLY to the node stream
        // (IMeshNodeStreamCache) and writes edits back via stream.Update — NO /data replica, NO save
        // subscription, NO Save button. Ensure the node exists first (create-on-absent), then just
        // DECLARE the editor bound to its path; all value resolution + persistence happen GUI-side.
        stack = stack.WithView(Section("Repository"));
        sync.EnsureConfigNode(spacePath).Subscribe(_ => { },
            ex => host.UpdateData(ResultId, Err(ex.Message)));
        stack = stack.WithView(
            MeshNodeContentEditorControl.ForType(GitHubSyncService.ConfigPath(spacePath), typeof(GitHubSyncConfig)));

        // ── 3. Sync + re-import ───────────────────────────────────────────────
        stack = stack.WithView(Section("Sync"));
        stack = stack.WithView(Controls.Button("Sync now")
            .WithAppearance(Appearance.Accent)
            .WithClickAction(ctx =>
            {
                ctx.Host.UpdateData(ResultId, Pending("Syncing this Space to GitHub…"));
                sync.SyncToGitHub(spacePath, userId).Subscribe(
                    res => ctx.Host.UpdateData(ResultId, Ok(
                        $"Synced — commit {Short(res.CommitSha)} ({res.FilesWritten} written, {res.FilesDeleted} removed)" +
                        (res.RepoCreated ? ", repository created" : "") + ".")),
                    ex => ctx.Host.UpdateData(ResultId, Err(ex.Message)));
                return Task.CompletedTask;
            }));

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
        stack = stack.WithView(BuildReimportForm(sync, spacePath, userId));

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
        GitHubCredentialService creds, string userId, string spacePath, GitHubCredential? cred, bool isConfigured)
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
        var returnPath = $"/{spacePath}/Settings/{TabId}";
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

    private static UiControl BuildReimportForm(GitHubSyncService sync, string spacePath, string userId)
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
                    ctx.Host.UpdateData(ResultId, Pending($"Re-importing this Space at {Esc(commit)}…"));
                    sync.ReimportAtCommit(spacePath, commit, userId).Subscribe(
                        r => ctx.Host.UpdateData(ResultId, Ok($"Re-imported {r.Outcome} ({r.Count} node(s)) at {Esc(commit)}.")),
                        ex => ctx.Host.UpdateData(ResultId, Err(ex.Message)));
                });
                return Task.CompletedTask;
            }));
        return row;
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

    private static string Short(string sha) => string.IsNullOrEmpty(sha) ? "" : sha[..Math.Min(8, sha.Length)];
    private static string Esc(string s) => System.Web.HttpUtility.HtmlEncode(s);

    private static string Ok(string m) =>
        $"<p style=\"padding:8px 12px;color:#4ade80;background:var(--neutral-layer-2);border-radius:6px;\">{Esc(m)}</p>";
    private static string Err(string m) =>
        $"<p style=\"padding:8px 12px;color:#f87171;background:var(--neutral-layer-2);border-radius:6px;\">{Esc(m)}</p>";
    private static string Pending(string m) =>
        $"<p style=\"padding:8px 12px;color:var(--neutral-foreground-hint);background:var(--neutral-layer-2);border-radius:6px;\">{Esc(m)}</p>";
}
