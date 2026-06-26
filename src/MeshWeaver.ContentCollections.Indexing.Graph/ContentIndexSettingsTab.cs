using System.Reactive.Linq;
using MeshWeaver.Application.Styles;
using MeshWeaver.Data;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.ContentCollections.Indexing.Graph;

/// <summary>
/// The Space-settings "Content Indexing" tab — the GUI surface for the content → vector-index feature.
/// Shown only on Space nodes, and only when content indexing is ACTIVE on the server (the pipeline was
/// wired — embeddings + a vector store configured; <see cref="ContentIndexingObserver"/> resolves). It:
/// <list type="bullet">
///   <item>shows live <b>status</b> (Active, with what auto-indexing does);</item>
///   <item>offers <b>"Re-index all content"</b> — runs <see cref="ContentIndexingObserver.ReindexAll"/>
///     as an <c>Activity</c> (live progress + Cancel), to (re)index files already in the collection
///     (unchanged files are hash-skipped);</item>
///   <item>explains how to find the produced <c>Document</c> nodes (<c>@document</c> search).</item>
/// </list>
/// Mirrors <c>MeshWeaver.GitSync</c>'s settings tab: Space-filtered registration + the activity
/// progress-panel pattern. No <c>IMeshService.Query</c> in render (status comes from DI + the live
/// activity stream), per the no-query-in-render rule.
/// </summary>
public static class ContentIndexSettingsTab
{
    /// <summary>The stable id of the Content Indexing settings tab.</summary>
    public const string TabId = "ContentIndex";

    // Holds the path of the currently-running re-index activity (empty = none); the progress panel binds
    // to it and Cancel flips its RequestedStatus.
    private const string ActivityPathId = "ciActivityPath";
    private const string ResultId = "ciResult";

    /// <summary>Registers the Content Indexing settings tab (shown on Space nodes when indexing is active).</summary>
    public static MessageHubConfiguration AddContentIndexSettingsTab(this MessageHubConfiguration config)
        => config.AddSettingsMenuItems(new SettingsMenuItemProvider(GetTab));

    private static IObservable<IReadOnlyList<SettingsMenuItemDefinition>> GetTab(
        LayoutAreaHost host, RenderingContext ctx)
    {
        IReadOnlyList<SettingsMenuItemDefinition> none = Array.Empty<SettingsMenuItemDefinition>();

        // The tab exists only where the pipeline is wired (embeddings + vector store configured). The
        // observer is a mesh singleton registered exactly when AddContentIndexingPipeline ran — its
        // absence means indexing is inert on this server, so don't surface the tab at all.
        if (host.Hub.ServiceProvider.GetService<ContentIndexingObserver>() is null)
            return Observable.Return(none);

        var tab = new SettingsMenuItemDefinition(
            Id: TabId,
            Label: "Content Indexing",
            ContentBuilder: BuildContent,
            Group: "Integration",
            Icon: FluentIcons.Document(),
            GroupIcon: FluentIcons.Document(),
            Order: 260,
            RequiredPermission: Permission.Update);

        // "Space" is SpaceNodeType.NodeType — the value a Space instance node carries. Compared as a
        // literal to avoid taking a namespace dependency just for the constant.
        return host.Workspace.GetMeshNodeStream()
            .Select(node => string.Equals(node?.NodeType, "Space", StringComparison.Ordinal)
                ? (IReadOnlyList<SettingsMenuItemDefinition>)new[] { tab }
                : none)
            .DistinctUntilChanged()
            .Catch<IReadOnlyList<SettingsMenuItemDefinition>, Exception>(_ => Observable.Return(none))
            .StartWith(none);
    }

    internal static UiControl BuildContent(LayoutAreaHost host, StackControl stack, MeshNode? node)
    {
        var observer = host.Hub.ServiceProvider.GetService<ContentIndexingObserver>();
        var spacePath = node?.Path ?? "";
        var collectionPath = $"{spacePath}/content";

        stack = stack.WithView(Controls.H2("Content Indexing").WithStyle("margin: 0 0 8px 0;"));
        stack = stack.WithView(Controls.Html(
            "<p style=\"font-size:0.85rem;color:var(--neutral-foreground-hint);margin-bottom:16px;\">" +
            "Files uploaded to this space's <code>content</code> collection are chunked, embedded into a " +
            "vector index, and summarised as browsable <strong>Document</strong> nodes — found via " +
            "<code>@document &lt;query&gt;</code> autocomplete and mesh search.</p>"));

        // Defensive: GetTab already hides the tab when indexing is inert, but keep a clear message.
        if (observer is null)
        {
            stack = stack.WithView(Controls.Html(StatusHtml(false,
                "Inactive — content indexing is not configured on this server. Set an embedding provider " +
                "(<code>Embedding:Endpoint</code> + <code>Embedding:ApiKey</code>) and a vector connection " +
                "(<code>ConnectionStrings:contentindex</code>, or derived from the mesh connection).")));
            return stack;
        }

        stack = stack.WithView(Controls.Html(StatusHtml(true,
            "Active — new uploads to this space index automatically. Use <em>Re-index all content</em> to " +
            "(re)index files already in the collection; unchanged files are skipped.")));

        // ── Re-index (operations-as-script Activity) ──────────────────────────
        stack = stack.WithView(Section("Re-index"));
        stack = stack.WithView(Controls.Button("Re-index all content")
            .WithAppearance(Appearance.Accent)
            .WithClickAction(c =>
            {
                observer.ReindexAll(new[] { collectionPath },
                        onActivityCreated: path => c.Host.UpdateData(ActivityPathId, path))
                    .Subscribe(_ => { }, ex => c.Host.UpdateData(ResultId, Err(ex.Message)));
                return Task.CompletedTask;
            }));

        // Live activity progress panel: binds to the running re-index activity (Messages + Status stream
        // live; Cancel flips RequestedStatus = Cancelled). Empty until a run starts.
        stack = stack.WithView((h, _) => h.Stream.GetDataStream<string>(ActivityPathId)
            .Select(path => (UiControl?)BuildActivityPanel(h, path))
            .StartWith((UiControl?)Controls.Stack.WithWidth("100%")));

        // Result/error area.
        stack = stack.WithView((h, _) => h.Stream.GetDataStream<string>(ResultId)
            .Select(html => string.IsNullOrEmpty(html)
                ? (UiControl?)Controls.Stack.WithWidth("100%")
                : (UiControl?)Controls.Stack.WithWidth("100%").WithView(Controls.Html(html)))
            .StartWith((UiControl?)Controls.Stack.WithWidth("100%")));

        // ── Finding indexed documents ─────────────────────────────────────────
        stack = stack.WithView(Section("Indexed documents"));
        stack = stack.WithView(Controls.Html(
            "<p style=\"font-size:0.85rem;color:var(--neutral-foreground-hint);\">Each indexed file becomes a " +
            "<strong>Document</strong> node (AI summary + metadata + a link to the source file). Type " +
            "<code>@document &lt;query&gt;</code> in any editor to find them by content, or search the mesh " +
            "for <code>nodeType:Document</code>.</p>"));

        return stack;
    }

    // ── Activity progress panel (binds to the running re-index activity node) ──

    private static UiControl BuildActivityPanel(LayoutAreaHost host, string? activityPath)
    {
        var stack = Controls.Stack.WithWidth("100%");
        if (string.IsNullOrEmpty(activityPath))
            return stack;

        // Live Messages + Status (re-renders on every progress tick).
        stack = stack.WithView((h, _) => h.Hub.GetWorkspace().GetMeshNodeStream(activityPath)
            .Select(n => (UiControl?)Controls.Html(ActivityHtml(n?.Content as ActivityLog)))
            .StartWith((UiControl?)Controls.Html("")));

        // Cancel — flips RequestedStatus = Cancelled; the runner's watcher trips the command's token.
        stack = stack.WithView((h, _) => h.Hub.GetWorkspace().GetMeshNodeStream(activityPath)
            .Select(n => (n?.Content as ActivityLog)?.Status)
            .Select(status => (UiControl?)(status == ActivityStatus.Running
                ? Controls.Button("Cancel")
                    .WithAppearance(Appearance.Outline)
                    .WithClickAction(c => { c.Host.Hub.CancelActivity(activityPath); return Task.CompletedTask; })
                : Controls.Stack))
            .StartWith((UiControl?)Controls.Stack));
        return stack;
    }

    private static string ActivityHtml(ActivityLog? log)
    {
        if (log is null) return "";
        var colour = log.Status switch
        {
            ActivityStatus.Running => "var(--neutral-foreground-hint)",
            ActivityStatus.Succeeded => "#4ade80",
            ActivityStatus.Failed => "#f87171",
            ActivityStatus.Cancelled => "#fbbf24",
            _ => "var(--neutral-foreground-hint)",
        };
        var lines = string.Join("", log.Messages.TakeLast(12).Select(m =>
            $"<div style=\"font-family:monospace;font-size:0.8rem;\">{Esc(m.Message)}</div>"));
        return "<div style=\"padding:8px 12px;background:var(--neutral-layer-2);border-radius:6px;margin-top:8px;\">" +
               $"<div style=\"font-weight:600;color:{colour};margin-bottom:4px;\">{Esc(log.Status.ToString())}</div>{lines}</div>";
    }

    private static string StatusHtml(bool active, string message)
    {
        var dot = active ? "#22c55e" : "var(--neutral-stroke-strong-rest)";
        var label = active ? "Active" : "Inactive";
        return "<div style=\"font-size:0.9rem;display:flex;align-items:flex-start;gap:8px;margin-bottom:8px;\">" +
               $"<span style=\"display:inline-block;width:10px;height:10px;border-radius:50%;background:{dot};flex:0 0 auto;margin-top:5px;\"></span>" +
               $"<span><strong>{label}.</strong> {message}</span></div>";
    }

    private static UiControl Section(string title) =>
        Controls.Html($"<h3 style=\"margin:20px 0 8px 0;font-size:1rem;\">{Esc(title)}</h3>");

    private static string Esc(string s) => System.Web.HttpUtility.HtmlEncode(s);

    private static string Err(string m) =>
        $"<p style=\"padding:8px 12px;color:#f87171;background:var(--neutral-layer-2);border-radius:6px;\">{Esc(m)}</p>";
}
