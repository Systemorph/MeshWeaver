using System.Reactive.Linq;
using MeshWeaver.Application.Styles;
using MeshWeaver.ContentCollections.Indexing;
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

    // ── Explore-index state ──
    // The live search box's query text (pre-filled with `namespace:{collection} scope:subtree `).
    private const string ExploreQueryId = "ciExploreQuery";
    // The selected chunk to expand, encoded as `collection<US>file<US>index` (empty = none).
    private const string ExploreSelectedId = "ciExploreSelected";
    private const char SelSep = '\u001f';

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
            "(re)index files already in the collection; unchanged files are skipped. Use <em>Rebuild</em> to " +
            "force re-extraction of every file — e.g. to backfill page/position provenance onto files that " +
            "were indexed before it existed.")));

        // ── Re-index (operations-as-script Activity) ──────────────────────────
        stack = stack.WithView(Section("Re-index"));
        var reindexRow = Controls.Stack.WithWidth("100%").WithStyle("flex-direction:row; gap:8px; flex-wrap:wrap;");
        reindexRow = reindexRow.WithView(Controls.Button("Re-index all content")
            .WithAppearance(Appearance.Accent)
            .WithClickAction(c =>
            {
                observer.ReindexAll(new[] { collectionPath },
                        onActivityCreated: path => c.Host.UpdateData(ActivityPathId, path))
                    .Subscribe(_ => { }, ex => c.Host.UpdateData(ResultId, Err(ex.Message)));
                return Task.CompletedTask;
            }));
        // Rebuild: forces re-extraction past the hash gate — backfills page/position onto already-indexed
        // (content-unchanged) files, which a plain re-index would skip.
        reindexRow = reindexRow.WithView(Controls.Button("Rebuild (re-extract page/position)")
            .WithAppearance(Appearance.Outline)
            .WithClickAction(c =>
            {
                observer.ReindexAll(new[] { collectionPath },
                        onActivityCreated: path => c.Host.UpdateData(ActivityPathId, path), force: true)
                    .Subscribe(_ => { }, ex => c.Host.UpdateData(ResultId, Err(ex.Message)));
                return Task.CompletedTask;
            }));
        stack = stack.WithView(reindexRow);

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

        // ── Explore the index (live chunk search + tool-call inspector) ───────
        stack = BuildExploreSection(host, stack, collectionPath);

        return stack;
    }

    /// <summary>
    /// The "Explore index" section: a pre-filled search box over this space's content index, the equivalent
    /// <c>search_chunks</c> tool call for the current query, the ranked chunk hits, and an expandable chunk
    /// reader (<c>get_chunk</c> with prev/next). Reactive — the box drives the results live; clicking a hit
    /// opens the chunk below. Calls the SAME <see cref="ContentChunkSearch"/> engine the agent/MCP
    /// <c>search_chunks</c> tool uses, so the "maps to" claim is exact, not a mock.
    /// </summary>
    private static StackControl BuildExploreSection(LayoutAreaHost host, StackControl stack, string collectionPath)
    {
        stack = stack.WithView(Section("Explore index"));
        stack = stack.WithView(Controls.Html(
            "<p style=\"font-size:0.85rem;color:var(--neutral-foreground-hint);\">Search this space's indexed " +
            "content by meaning. The query uses mesh search syntax — <code>namespace:&lt;node&gt;/&lt;collection&gt;</code> " +
            "picks the collection and <code>scope:subtree</code> (the default) checks only that collection and " +
            "anything nested under it; add free-text terms after it. Each search maps to the same " +
            "<code>search_chunks</code> tool the agents call; click a hit to read the full chunk " +
            "(<code>get_chunk</code>) and step through neighbours.</p>"));

        // Pre-fill the box with this collection + the subtree scope, and start with no chunk expanded.
        host.UpdateData(ExploreQueryId, $"namespace:{collectionPath} scope:subtree ");
        host.UpdateData(ExploreSelectedId, "");

        stack = stack.WithView(new TextFieldControl(new JsonPointerReference(""))
            .WithPlaceholder("namespace:<node>/<collection> scope:subtree <free-text terms>")
            .WithImmediate(true) with
        { DataContext = LayoutAreaReference.GetDataPointer(ExploreQueryId) });

        // Live results: re-run on every (throttled, distinct) query change; Switch drops the superseded
        // search so a fast typist never sees a stale result land late.
        stack = stack.WithView((h, _) => h.Stream.GetDataStream<string>(ExploreQueryId)
            .Throttle(TimeSpan.FromMilliseconds(400))
            .DistinctUntilChanged()
            .Select(q => BuildExploreResults(h, collectionPath, q))
            .Switch()
            .StartWith((UiControl?)Controls.Stack.WithWidth("100%")));

        // The expanded chunk reader (bound to the selected hit; empty until one is clicked).
        stack = stack.WithView((h, _) => h.Stream.GetDataStream<string>(ExploreSelectedId)
            .DistinctUntilChanged()
            .Select(sel => BuildSelectedChunk(h, sel))
            .Switch()
            .StartWith((UiControl?)Controls.Stack.WithWidth("100%")));

        return stack;
    }

    /// <summary>Runs the content search for the current query and renders the tool-call line + hit rows.</summary>
    private static IObservable<UiControl?> BuildExploreResults(LayoutAreaHost host, string collectionPath, string? query)
    {
        var store = host.Hub.ServiceProvider.GetService<IChunkedContentVectorStore>();
        var embedder = host.Hub.ServiceProvider.GetService<IChunkEmbedder>();
        return ContentChunkSearch
            .SearchContent(store, embedder, query ?? "", limit: 20, defaultNamespace: collectionPath)
            .Select(result => (UiControl?)BuildResultsPanel(result))
            .Catch<UiControl?, Exception>(ex => Observable.Return((UiControl?)Controls.Html(Err(ex.Message))));
    }

    private static UiControl BuildResultsPanel(ContentSearchResult result)
    {
        var panel = Controls.Stack.WithWidth("100%").WithStyle("gap:8px; margin-top:8px;");

        // Tool-call inspector — the exact search_chunks call this query maps to.
        panel = panel.WithView(Controls.Html(
            "<div style=\"font-size:0.8rem;color:var(--neutral-foreground-hint);margin-bottom:2px;\">Maps to tool call</div>" +
            $"<pre style=\"margin:0;padding:8px 12px;background:var(--neutral-layer-2);border-radius:6px;" +
            $"font-family:monospace;font-size:0.8rem;white-space:pre-wrap;word-break:break-word;\">{Esc(result.ToolCall)}</pre>"));

        if (!string.IsNullOrEmpty(result.Message))
            return panel.WithView(Controls.Html(Note(result.Message)));

        if (result.Hits.Count == 0)
            return panel.WithView(Controls.Html(Note("No matching chunks. Try different terms or a broader scope.")));

        var list = Controls.Stack.WithWidth("100%").WithStyle(
            "gap:0; margin-top:4px; border:1px solid var(--neutral-stroke-rest);border-radius:6px;" +
            "max-height:320px;overflow:auto;");
        foreach (var hit in result.Hits)
            list = list.WithView(BuildHitRow(hit));
        return panel.WithView(list);
    }

    /// <summary>One clickable hit row: rank · file · chunk index header + snippet. Click expands the chunk.</summary>
    private static UiControl BuildHitRow(ChunkHit hit)
    {
        var row = Controls.Stack.WithWidth("100%").WithStyle(
            "gap:2px; padding:6px 8px; border-bottom:1px solid var(--neutral-stroke-rest);");
        row = row.WithView(Controls.Button($"#{hit.Rank}  {hit.FilePath} · chunk {hit.ChunkIndex}")
            .WithAppearance(Appearance.Lightweight)
            .WithClickAction(c =>
            {
                c.Host.UpdateData(ExploreSelectedId, EncodeSelection(hit.CollectionPath, hit.FilePath, hit.ChunkIndex));
                return Task.CompletedTask;
            }));
        row = row.WithView(Controls.Html(
            $"<div style=\"font-size:0.8rem;color:var(--neutral-foreground-hint);padding-left:8px;\">{Esc(hit.Snippet)}</div>"));
        return row;
    }

    // ── Expanded chunk reader (get_chunk + prev/next) ──

    private static IObservable<UiControl?> BuildSelectedChunk(LayoutAreaHost host, string? encoded)
    {
        if (string.IsNullOrEmpty(encoded))
            return Observable.Return((UiControl?)Controls.Stack.WithWidth("100%"));

        var parts = encoded.Split(SelSep);
        if (parts.Length != 3 || !int.TryParse(parts[2], out var index))
            return Observable.Return((UiControl?)Controls.Stack.WithWidth("100%"));
        var collection = parts[0];
        var file = parts[1];

        var store = host.Hub.ServiceProvider.GetService<IChunkedContentVectorStore>();
        if (store is null)
            return Observable.Return((UiControl?)Controls.Stack.WithWidth("100%"));

        return store.GetChunk(collection, file, index)
            .SelectMany(chunk => store.GetChunkCount(collection, file)
                .Select(total => (UiControl?)BuildChunkPanel(collection, file, index, chunk, total)))
            .Catch<UiControl?, Exception>(ex => Observable.Return((UiControl?)Controls.Html(Err(ex.Message))));
    }

    private static UiControl BuildChunkPanel(
        string collection, string file, int index, ContentChunk? chunk, int total)
    {
        var stack = Controls.Stack.WithWidth("100%").WithStyle(
            "gap:6px; margin-top:8px; padding:12px; background:var(--neutral-layer-2);border-radius:6px;");

        stack = stack.WithView(Controls.Html(
            $"<div style=\"font-weight:600;font-size:0.9rem;\">{Esc(file)} · chunk {index}" +
            (total > 0 ? $" / {total}" : "") + "</div>" +
            "<div style=\"font-size:0.78rem;color:var(--neutral-foreground-hint);margin-top:2px;\">Maps to tool call</div>" +
            $"<pre style=\"margin:2px 0 0 0;padding:6px 10px;background:var(--neutral-layer-1);border-radius:6px;" +
            $"font-family:monospace;font-size:0.78rem;white-space:pre-wrap;word-break:break-word;\">" +
            $"{Esc($"get_chunk(collectionPath: \"{collection}\", filePath: \"{file}\", chunkIndex: {index})")}</pre>"));

        if (chunk is null)
        {
            stack = stack.WithView(Controls.Html(Note(total == 0
                ? $"No chunks indexed for '{file}'."
                : $"No chunk at index {index}; valid range is 0..{total - 1}.")));
            return stack;
        }

        // The chunk text verbatim (HtmlEncoded preformatted block — genuinely-text display, not structured data).
        stack = stack.WithView(Controls.Html(
            "<pre style=\"margin:0;padding:10px 12px;background:var(--neutral-layer-1);border-radius:6px;" +
            "font-size:0.82rem;white-space:pre-wrap;word-break:break-word;max-height:280px;overflow:auto;\">" +
            $"{Esc(chunk.Text)}</pre>"));

        // Prev / next / close stepping.
        var nav = Controls.Stack.WithWidth("100%").WithStyle("flex-direction:row; gap:8px;");
        if (index > 0)
            nav = nav.WithView(Controls.Button("← Prev")
                .WithAppearance(Appearance.Outline)
                .WithClickAction(c => { c.Host.UpdateData(ExploreSelectedId, EncodeSelection(collection, file, index - 1)); return Task.CompletedTask; }));
        if (index < total - 1)
            nav = nav.WithView(Controls.Button("Next →")
                .WithAppearance(Appearance.Outline)
                .WithClickAction(c => { c.Host.UpdateData(ExploreSelectedId, EncodeSelection(collection, file, index + 1)); return Task.CompletedTask; }));
        nav = nav.WithView(Controls.Button("Close")
            .WithAppearance(Appearance.Stealth)
            .WithClickAction(c => { c.Host.UpdateData(ExploreSelectedId, ""); return Task.CompletedTask; }));
        stack = stack.WithView(nav);

        return stack;
    }

    private static string EncodeSelection(string collection, string file, int index) =>
        string.Join(SelSep, collection, file, index.ToString());

    private static string Note(string message) =>
        $"<p style=\"font-size:0.85rem;color:var(--neutral-foreground-hint);margin:4px 0;\">{Esc(message)}</p>";

    // ── Activity progress panel (binds to the running re-index activity node) ──

    private static UiControl BuildActivityPanel(LayoutAreaHost host, string? activityPath)
    {
        var stack = Controls.Stack.WithWidth("100%");
        if (string.IsNullOrEmpty(activityPath))
            return stack;

        // Live Messages + Status (re-renders on every progress tick).
        stack = stack.WithView((h, _) => h.Hub.GetWorkspace().GetMeshNodeStream(activityPath)
            .Select(n => (UiControl?)Controls.Html(ActivityHtml(n.ContentAs<ActivityLog>(host.Hub.JsonSerializerOptions))))
            .StartWith((UiControl?)Controls.Html("")));

        // Cancel — flips RequestedStatus = Cancelled; the runner's watcher trips the command's token.
        stack = stack.WithView((h, _) => h.Hub.GetWorkspace().GetMeshNodeStream(activityPath)
            .Select(n => n.ContentAs<ActivityLog>(host.Hub.JsonSerializerOptions)?.Status)
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
