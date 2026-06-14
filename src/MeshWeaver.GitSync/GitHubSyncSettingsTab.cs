using System.Reactive.Linq;
using System.Text.Json;
using MeshWeaver.Application.Styles;
using MeshWeaver.Data;
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
    private const string ConnectStateId = "ghConnectState";
    private const string CfgFormId = "ghSyncCfgForm";
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
            Group: "Integrations",
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
        stack = stack.WithView((h, _) => h.Stream.GetDataStream<UiControl>(ConnectStateId)
            .StartWith((UiControl)Controls.Html("<p style=\"color:var(--neutral-foreground-hint);\">…</p>")));
        if (string.IsNullOrEmpty(userId))
            host.UpdateData(ConnectStateId, Controls.Html("<p>Sign in to connect a GitHub account.</p>"));
        else
            creds.Get(userId).Take(1).Subscribe(
                c => host.UpdateData(ConnectStateId, RenderConnect(creds, userId, spacePath, c, oauth.IsConfigured)),
                _ => host.UpdateData(ConnectStateId, RenderConnect(creds, userId, spacePath, null, oauth.IsConfigured)));

        // ── 2. Repository ─────────────────────────────────────────────────────
        stack = stack.WithView(Section("Repository"));
        host.UpdateData(CfgFormId, DefaultCfgForm());
        sync.ReadConfig(spacePath).Take(1).Subscribe(cfg =>
        {
            if (cfg is not null) host.UpdateData(CfgFormId, CfgFormFrom(cfg));
        });
        stack = stack.WithView(BuildRepoForm(sync, spacePath));

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

        // Last-synced status (live).
        stack = stack.WithView((h, _) => sync.ReadConfig(spacePath)
            .Select(cfg => (UiControl?)Controls.Html(LastSyncedHtml(cfg)))
            .StartWith((UiControl?)Controls.Html("<p style=\"color:var(--neutral-foreground-hint);\">…</p>")));

        // Editable commit + re-import.
        host.UpdateData(CommitFormId, new Dictionary<string, object?> { ["commit"] = "" });
        sync.ReadConfig(spacePath).Take(1).Subscribe(cfg =>
            host.UpdateData(CommitFormId, new Dictionary<string, object?>
            {
                ["commit"] = cfg?.LastSyncCommitSha ?? cfg?.Branch ?? "main",
            }));
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
                    creds.Delete(userId).Subscribe(
                        _ => ctx.Host.UpdateData(ConnectStateId, RenderConnect(creds, userId, spacePath, null, isConfigured)),
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

        // The OAuth flow is a browser redirect to /connect/github, which posts back to
        // /connect/github/callback, stores the token, and returns here. Render a link
        // (not a click-action) so the browser navigates.
        var returnPath = $"/{spacePath}/Settings/{TabId}";
        var connectUrl = $"/connect/github?returnPath={Uri.EscapeDataString(returnPath)}";
        return Controls.Html(
            "<div style=\"font-size:0.85rem;\"><span style=\"color:#9ca3af;\">●</span> Not connected. " +
            $"<a href=\"{Esc(connectUrl)}\" style=\"color:var(--accent-fill-rest);font-weight:600;\">Connect GitHub →</a>" +
            " (one-time browser approval; authorize for the org whose repos you'll sync).</div>");
    }

    // ── Repository form ───────────────────────────────────────────────────────

    private static UiControl BuildRepoForm(GitHubSyncService sync, string spacePath)
    {
        var ctxPtr = LayoutAreaReference.GetDataPointer(CfgFormId);
        var form = Controls.Stack.WithWidth("100%").WithStyle("gap:12px;");

        form = form.WithView(new TextFieldControl(new JsonPointerReference("repositoryUrl"))
        {
            Label = "Repository URL", Placeholder = "https://github.com/owner/repo", DataContext = ctxPtr,
        }.WithWidth("100%"));
        form = form.WithView(new TextFieldControl(new JsonPointerReference("branch"))
        {
            Label = "Branch", Placeholder = "main", DataContext = ctxPtr,
        }.WithWidth("240px"));
        form = form.WithView(new TextFieldControl(new JsonPointerReference("subdirectory"))
        {
            Label = "Subdirectory (optional)", Placeholder = "(repository root)", DataContext = ctxPtr,
        }.WithWidth("320px"));
        form = form.WithView(new CheckBoxControl(new JsonPointerReference("createBranch"))
        {
            Label = "Create the branch if it doesn't exist", DataContext = ctxPtr,
        });
        form = form.WithView(new CheckBoxControl(new JsonPointerReference("createRepo"))
        {
            Label = "Create the repository (private) if it doesn't exist", DataContext = ctxPtr,
        });
        form = form.WithView(Controls.Button("Save repository settings")
            .WithClickAction(ctx =>
            {
                ctx.Host.Stream.GetDataStream<Dictionary<string, object?>>(CfgFormId).Take(1).Subscribe(d =>
                {
                    var url = Str(d, "repositoryUrl");
                    var branch = Str(d, "branch") ?? "main";
                    var sub = Str(d, "subdirectory");
                    sync.SaveConfig(spacePath, url, branch, sub, Bool(d, "createBranch", true), Bool(d, "createRepo", true))
                        .Subscribe(
                            _ => ctx.Host.UpdateData(ResultId, Ok("Saved repository settings.")),
                            ex => ctx.Host.UpdateData(ResultId, Err(ex.Message)));
                });
                return Task.CompletedTask;
            }));
        return form;
    }

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

    private static Dictionary<string, object?> DefaultCfgForm() => new()
    {
        ["repositoryUrl"] = "", ["branch"] = "main", ["subdirectory"] = "",
        ["createBranch"] = true, ["createRepo"] = true,
    };

    private static Dictionary<string, object?> CfgFormFrom(GitHubSyncConfig cfg) => new()
    {
        ["repositoryUrl"] = cfg.RepositoryUrl ?? "",
        ["branch"] = cfg.Branch,
        ["subdirectory"] = cfg.Subdirectory ?? "",
        ["createBranch"] = cfg.CreateBranchIfMissing,
        ["createRepo"] = cfg.CreateRepoIfMissing,
    };

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

    private static bool Bool(Dictionary<string, object?> d, string key, bool fallback)
    {
        var v = d.GetValueOrDefault(key);
        return v switch
        {
            bool b => b,
            JsonElement je when je.ValueKind is JsonValueKind.True => true,
            JsonElement je when je.ValueKind is JsonValueKind.False => false,
            string s when bool.TryParse(s, out var b) => b,
            _ => fallback,
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
