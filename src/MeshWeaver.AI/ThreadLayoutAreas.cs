using System.Collections.Immutable;
using System.Reactive.Linq;
using MeshWeaver.Application.Styles;
using MeshWeaver.Data;
using MeshWeaver.Domain;
using MeshWeaver.Graph;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using System.Text.Json;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Controls = MeshWeaver.Layout.Controls;
using MeshThread = MeshWeaver.AI.Thread;

namespace MeshWeaver.AI;

/// <summary>
/// Provides dedicated views for Thread nodes with a conversation-focused layout.
/// Features:
/// - Thread area: Main view showing thread content and message history
/// - History area: Shows delegation sub-threads as a list
/// - Thumbnail: Compact card for catalog display
/// </summary>
public static class ThreadLayoutAreas
{

    /// <summary>
    /// Adds the thread-specific views to the hub's layout.
    /// Thread area is the default — shows title + ThreadChatControl.
    /// </summary>
    public static MessageHubConfiguration AddThreadLayoutAreas(this MessageHubConfiguration configuration)
        => configuration
            // Per-thread-hub last-good holder — survives area/component re-creation so the fresh data section
            // can be seeded immediately (see SubscribeThreadVm / ThreadVmHolder).
            .WithServices(services => services.AddSingleton<ThreadVmHolder>())
            // Legacy ThreadSubmission.ApplyResubmit / ThreadSubmission.ApplyDeleteFromMessage handlers
            // removed — click actions now call ThreadSubmission.ApplyResubmit /
            // ApplyDeleteFromMessage directly. See RequestViaStreamUpdate.md.
            .AddDefaultMeshMenu()
            .AddNodeMenuItems("SidePanel", SidePanelMenuProvider)
            .AddNodeMenuItems(DelegationsMenuProvider)
            .AddNodeMenuItems(ChangesMenuProvider)
            .AddLayout(layout => layout
                .WithDefaultArea(ThreadNodeType.ThreadArea)
                .WithView(ThreadNodeType.ThreadArea, ThreadView)
                .WithView(MeshNodeLayoutAreas.OverviewArea, ThreadProgressView)
                .WithView(ThreadNodeType.ThreadChatArea, ThreadChatView)
                .WithView(ThreadNodeType.StreamingArea, StreamingView)
                .WithView(ThreadNodeType.HistoryArea, HistoryView)
                .WithView(ThreadNodeType.HeaderArea, HeaderView)
                .WithView(ThreadNodeType.ChangesArea, ChangesAreaView)
                // The thread's own composer selectors — binds Thread.Composer (the composer
                // copied onto the thread at creation), same wiring as the composer node's area.
                .WithView(ThreadComposerView.SelectorsArea, ThreadComposerView.ComposerSelectors)
                .WithView(MeshNodeLayoutAreas.ThumbnailArea, Thumbnail)
                .WithView(MeshNodeLayoutAreas.ThreadsArea, ThreadsCatalog));

    /// <summary>
    /// Side panel menu items (New Chat, History, Full Screen). Static set — emitted once.
    /// </summary>
    private static IObservable<IReadOnlyCollection<NodeMenuItemDefinition>> SidePanelMenuProvider(
        LayoutAreaHost host, RenderingContext ctx)
        => Observable.Return<IReadOnlyCollection<NodeMenuItemDefinition>>(
        [
            new("New Chat", "new-chat", Order: 0),
            new("History", "history", Order: 10),
            new("Full Screen", "fullscreen", Order: 20),
        ]);

    /// <summary>
    /// Main menu item: Delegations (sub-thread history).
    /// </summary>
    private static IObservable<IReadOnlyCollection<NodeMenuItemDefinition>> DelegationsMenuProvider(
        LayoutAreaHost host, RenderingContext ctx)
    {
        var hubPath = host.Hub.Address.ToString();
        return Observable.Return<IReadOnlyCollection<NodeMenuItemDefinition>>(
        [
            new("Delegations", ThreadNodeType.HistoryArea, Order: 12,
                Href: MeshNodeLayoutAreas.BuildUrl(hubPath, ThreadNodeType.HistoryArea)),
        ]);
    }

    /// <summary>
    /// Main menu item: Changes (aggregated node modifications + bulk revert).
    /// </summary>
    private static IObservable<IReadOnlyCollection<NodeMenuItemDefinition>> ChangesMenuProvider(
        LayoutAreaHost host, RenderingContext ctx)
    {
        var hubPath = host.Hub.Address.ToString();
        return Observable.Return<IReadOnlyCollection<NodeMenuItemDefinition>>(
        [
            new("Changes", ThreadNodeType.ChangesArea, Order: 13,
                Href: MeshNodeLayoutAreas.BuildUrl(hubPath, ThreadNodeType.ChangesArea)),
        ]);
    }

    private static string GetContextDisplayName(string path)
    {
        var segments = path.Split('/');
        return segments.Length > 0 ? segments[^1] : path;
    }


    /// <summary>
    /// Overrides the default Threads catalog view to add a "Create Thread" button.
    /// Injected via AddThreadLayoutAreas configuration.
    /// </summary>
    public static UiControl ThreadsCatalog(LayoutAreaHost host, RenderingContext _)
    {
        var hubPath = host.Hub.Address.ToString();
        var createUrl = string.IsNullOrEmpty(hubPath)
            ? $"/Create?type={Uri.EscapeDataString(ThreadNodeType.NodeType)}"
            : $"/{hubPath}/Create?type={Uri.EscapeDataString(ThreadNodeType.NodeType)}";

        return Controls.Stack
            .WithView(Controls.Stack
                .WithOrientation(Orientation.Horizontal)
                .WithStyle("justify-content: flex-end; padding: 0 0 12px 0;")
                .WithView(Controls.Button("Create Thread")
                    .WithAppearance(Appearance.Accent)
                    .WithIconStart(FluentIcons.Add())
                    .WithNavigateToHref(createUrl)))
            .WithView(Controls.MeshSearch
                // -content.status:Done hides finished threads from the catalog;
                // user can type `content.status:Done` in the search box to surface them.
                .WithHiddenQuery($"namespace:{hubPath}/{ThreadNodeType.ThreadPartition} nodeType:{ThreadNodeType.NodeType} -content.status:Done sort:lastModified-desc")
                .WithPlaceholder("Search threads...")
                .WithRenderMode(MeshSearchRenderMode.Flat)
                .WithMaxColumns(3));
    }

    /// <summary>
    /// Data key for pushing Thread content to the layout area data section.
    /// The ThreadChatControl data-binds this via JsonPointerReference.
    /// </summary>
    internal const string ThreadDataKey = "thread";

    /// <summary>
    /// Renders the Thread area — the default view for threads.
    /// Emits a ThreadChatControl with data-bound Thread content. The hero header
    /// (context chip, inline-editable title + description, Mark Done) is rendered
    /// NATIVELY by the Blazor chat view as the first item INSIDE its scrollable
    /// message area (gated by <c>WithShowFullHeader()</c>) so it scrolls away with
    /// the conversation — the title/description bind directly to the data-bound
    /// <see cref="ThreadViewModel.Name"/> / <see cref="ThreadViewModel.Description"/>
    /// and write back through the node stream. Hence <c>WithShowFullHeader()</c>
    /// rather than a sibling header above the chat.
    ///
    /// IMPORTANT: The ThreadChatControl is emitted ONCE. Thread content is pushed
    /// separately via host.UpdateData() and data-bound on the control to avoid
    /// re-creating it (and thus re-rendering the Monaco editor) on every change.
    /// </summary>
    public static UiControl? ThreadView(LayoutAreaHost host, RenderingContext _)
    {
        var hubPath = host.Hub.Address.ToString();

        // OWN MeshNode stream — push ThreadViewModel to data section; contains all
        // thread state for the Blazor view.
        SubscribeThreadVm(host, hubPath);

        // Static container — never rebuilt
        return Controls.Stack
            .WithWidth("100%")
            .WithStyle("flex: 1; min-height: 0; display: flex; flex-direction: column;")
            .WithView(new ThreadChatControl()
                .WithThreadViewModel(new JsonPointerReference(LayoutAreaReference.GetDataPointer(ThreadDataKey)))
                .WithShowFullHeader()
                .WithStyle("flex: 1; min-height: 0; overflow: hidden;"));
    }

    /// <summary>
    /// Builds the <see cref="ThreadViewModel"/> snapshot pushed to the data section
    /// from the thread's own MeshNode. Shared by <see cref="ThreadView"/> and
    /// <see cref="ThreadChatView"/> (the latter in its <c>WithShowFullHeader()</c> mode).
    /// </summary>
    /// <summary>
    /// Subscribes the OWN MeshNode stream → <see cref="ThreadViewModel"/> → the data section, with a
    /// KEEP-LAST-GOOD guard. The stream alternates between the typed MeshThread (the owning hub's own write)
    /// and a cross-hub / change-feed representation that can momentarily emit a NULL or message-empty node;
    /// projecting that yields an empty viewmodel (Messages=[]) that flaps the data section to the empty state
    /// — HasNoMessages → the composer blanks: the intermittent round-N "chat vanishes" (confirmed by the
    /// footer-gate diagnostic, createdBy=&lt;null&gt; noMsgs=True at the blank). A new thread legitimately
    /// STARTS empty (the first emission passes; lastVm is null), so the discriminator is a REGRESSION to
    /// empty AFTER we have shown messages — that is the transient. Drop only those and keep the last-good.
    /// Subscribe callbacks are serialized, so the closure state is race-free.
    /// </summary>
    private static void SubscribeThreadVm(LayoutAreaHost host, string hubPath)
    {
        // The last-good lives on a per-thread-HUB holder, not a local closure: the ThreadChatView (and its
        // LayoutAreaHost) re-creates ~1-2× per conversation on a legitimate thread rebind, which would reset
        // a closure/component field to null — and the fresh GetMeshNodeStream emits null before the node
        // loads, so the re-created component would bind a null data section → blank (the residual round-N
        // "chat vanishes", pinned by TCV-INIT/TCV-EMPTY fieldNull=True). The hub OUTLIVES the area, so seed
        // the fresh data section with the holder's last-good IMMEDIATELY, before the stream's first emission.
        var holder = host.Hub.ServiceProvider.GetRequiredService<ThreadVmHolder>();
        if (holder.LastGood is not null)
            host.UpdateData(ThreadDataKey, holder.LastGood);
        var sub = host.Workspace.GetMeshNodeStream()
            .Select(node => BuildThreadViewModel(node, hubPath, host.Hub.JsonSerializerOptions))
            .DistinctUntilChanged()
            .Subscribe(vm =>
            {
                // Keep the last-good: skip a transient TRULY-empty emission (no committed Messages AND no
                // pending user text) after we had content — it would blank the data section → the round-N
                // "vanish". A Pending-only emission (the just-sent optimistic user message: Messages=0 but
                // PendingMessageTexts=[text]) IS content and MUST pass, else the new user bubble is dropped
                // server-side before it reaches the client (the "saw N-1" failure).
                static bool HasContent(ThreadViewModel v) => v.Messages.Count > 0 || v.PendingMessageTexts.Count > 0;
                if (holder.LastGood is { } last && HasContent(last) && !HasContent(vm))
                    return;
                if (HasContent(vm))
                    holder.LastGood = vm;
                host.UpdateData(ThreadDataKey, vm);
            });
        host.RegisterForDisposal(sub);
    }

    /// <summary>
    /// Per-thread-hub holder for the last content-bearing <see cref="ThreadViewModel"/>. The thread hub
    /// OUTLIVES the per-render LayoutAreaHost / ThreadChatView, so this survives an area+component
    /// re-creation (a legitimate thread rebind) where a closure/component field would reset to null. It lets
    /// a freshly re-created area SEED its empty data section with the last-good immediately — instead of
    /// binding the null that GetMeshNodeStream emits before the node loads, which is the residual
    /// intermittent "chat vanishes". Registered per thread hub as a singleton (lifetime = the hub).
    /// </summary>
    public sealed class ThreadVmHolder
    {
        public ThreadViewModel? LastGood { get; set; }
    }

    internal static ThreadViewModel BuildThreadViewModel(MeshNode? node, string hubPath, JsonSerializerOptions options)
    {
        // 🚨 ContentAs (DESERIALIZE), never `as MeshThread`. The node stream alternates between the
        // typed MeshThread (the owning hub's own write) and a JsonElement (the cache / cross-hub-sync
        // / change-feed representation — "content is normally a JsonElement"). `as MeshThread` is NULL
        // for the JsonElement form, so this produced a DEFAULT viewmodel that ALTERNATED with the real
        // one → vmStream.DistinctUntilChanged never deduped → UpdateData fired on every emission → the
        // 931× FullHeader render storm that saturated the circuit and vanished the chat. Deserializing
        // yields the SAME viewmodel for both representations, so the dedup actually fires.
        var threadContent = node.ContentAs<MeshThread>(options);
        var contextPath = node?.MainNode != node?.Path ? node?.MainNode : hubPath;
        var contextDisplayName = node?.MainNode != node?.Path
            ? GetContextDisplayName(node!.MainNode) : GetThreadTitle(node);
        return new ThreadViewModel
        {
            Messages = threadContent?.Messages ?? [],
            ThreadPath = hubPath,
            Name = node?.Name,
            Description = node?.Description,
            InitialContext = contextPath,
            InitialContextDisplayName = contextDisplayName,
            IsExecuting = threadContent?.IsExecuting ?? false,
            IsDone = threadContent?.Status == ThreadExecutionStatus.Done,
            ExecutionStatus = threadContent?.ExecutionStatus,
            StreamingText = threadContent?.StreamingText,
            StreamingToolCalls = threadContent?.StreamingToolCalls,
            PendingMessageTexts = ExtractPendingTexts(threadContent),
            ExecutionStartedAt = threadContent?.ExecutionStartedAt,
            CreatedBy = node?.CreatedBy,
        };
    }

    /// <summary>
    /// Renders just the ThreadChatControl without the full-page header.
    /// Used by the side panel — the title is pushed to the data section
    /// so the side panel header can display it.
    /// </summary>
    public static UiControl? ThreadChatView(LayoutAreaHost host, RenderingContext _)
    {
        var hubPath = host.Hub.Address.ToString();
        var ownNodeStream = host.Workspace.GetMeshNodeStream();

        SubscribeThreadVm(host, hubPath);

        // Push title to data section so the side panel header can read it.
        var titleStream = ownNodeStream
            .Select(GetThreadTitle)
            .DistinctUntilChanged();
        host.RegisterForDisposal(titleStream.Subscribe(title => host.UpdateData("title", title)));

        return new ThreadChatControl()
            .WithThreadViewModel(new JsonPointerReference(LayoutAreaReference.GetDataPointer(ThreadDataKey)));
    }

    /// <summary>
    /// Overview area for Thread nodes — overrides the default property editor.
    /// Shows the active/last message as a LayoutAreaControl (bubble with tool calls).
    /// If executing: shows the active message cell (streaming + tool calls).
    /// If finished: shows the last response message cell.
    /// Never shows the Thread record's properties as an editor.
    /// </summary>
    public static IObservable<UiControl?> ThreadProgressView(LayoutAreaHost host, RenderingContext _)
    {
        var hubPath = host.Hub.Address.ToString();
        return host.Workspace.GetMeshNodeStream()
            .Select(node =>
            {
                var thread = node.ContentAs<MeshThread>(host.Hub.JsonSerializerOptions);
                if (thread == null) return (UiControl?)null;

                // Find which message to show
                string? messageId = null;
                if (thread.IsExecuting && !string.IsNullOrEmpty(thread.ActiveMessageId))
                    messageId = thread.ActiveMessageId;
                else if (thread.Messages.Count > 0)
                    messageId = thread.Messages[^1]; // last message

                if (string.IsNullOrEmpty(messageId))
                    return (UiControl?)Controls.Html(
                        "<p style=\"color: var(--neutral-foreground-hint); padding: 24px;\">No messages yet.</p>");

                var messagePath = $"{hubPath}/{messageId}";
                return (UiControl?)new LayoutAreaControl(messagePath,
                    new LayoutAreaReference(ThreadMessageNodeType.OverviewArea))
                    .WithSpinnerType(SpinnerType.Skeleton);
            });
    }

    /// <summary>
    /// Streaming area: if thread has an executing cell, returns its default layout area.
    /// Otherwise null. Simple passthrough — no title, no wrapping.
    /// </summary>
    public static IObservable<UiControl?> StreamingView(LayoutAreaHost host, RenderingContext _)
    {
        var hubPath = host.Hub.Address.ToString();
        var logger = host.Hub.ServiceProvider.GetService<ILoggerFactory>()
            ?.CreateLogger("MeshWeaver.AI.StreamingView");
        logger?.LogDebug("[StreamingView] SUBSCRIBE hub={Hub}", hubPath);

        return host.Workspace.GetMeshNodeStream()
            .Select(node =>
            {
                var thread = node.ContentAs<MeshThread>(host.Hub.JsonSerializerOptions);
                return (IsExecuting: thread?.IsExecuting ?? false, thread?.ActiveMessageId);
            })
            .DistinctUntilChanged()
            .Select(state =>
            {
                if (!state.IsExecuting || string.IsNullOrEmpty(state.ActiveMessageId))
                {
                    logger?.LogDebug("[StreamingView] EMIT_NULL hub={Hub} isExec={IsExec} activeMsg={Msg}",
                        hubPath, state.IsExecuting, state.ActiveMessageId);
                    return (UiControl?)null;
                }

                var responsePath = $"{hubPath}/{state.ActiveMessageId}";
                logger?.LogDebug("[StreamingView] EMIT_CONTROL hub={Hub} responsePath={Path}",
                    hubPath, responsePath);
                return (UiControl?)new LayoutAreaControl(responsePath,
                    new LayoutAreaReference(ThreadMessageNodeType.OverviewArea));
            });
    }

    /// <summary>
    /// Renders the History area showing delegation sub-threads as a list.
    /// </summary>
    public static IObservable<UiControl?> HistoryView(LayoutAreaHost host, RenderingContext _)
    {
        var hubPath = host.Hub.Address.ToString();
        var meshQuery = host.Hub.ServiceProvider.GetService<IMeshService>();

        if (meshQuery == null)
        {
            return Observable.Return<UiControl?>(Controls.Html("<p style=\"color: var(--neutral-foreground-hint);\">Query service not available.</p>"));
        }

        // Live observable of child Thread nodes (delegations) — auto-updates on add/remove.
        var childrenStream = meshQuery.Query<MeshNode>(
                MeshQueryRequest.FromQuery($"namespace:{hubPath} nodeType:{ThreadNodeType.NodeType}"))
            .Select(change => (IReadOnlyList<MeshNode>)change.Items)
            .Catch<IReadOnlyList<MeshNode>, Exception>(_ => Observable.Return((IReadOnlyList<MeshNode>)Array.Empty<MeshNode>()));

        return host.Workspace.GetMeshNodeStream()
            .CombineLatest(childrenStream, (node, children) =>
                BuildHistoryView(host, node, hubPath, children ?? Array.Empty<MeshNode>()));
    }

    private static UiControl BuildHistoryView(LayoutAreaHost _, MeshNode? node, string threadPath, IReadOnlyList<MeshNode> delegations)
    {
        var container = Controls.Stack
            .WithWidth("100%")
            .WithStyle("padding: 24px;");

        // Header with back button
        var header = Controls.Stack
            .WithOrientation(Orientation.Horizontal)
            .WithWidth("100%")
            .WithStyle("align-items: center; margin-bottom: 24px;");

        header = header.WithView(Controls.Button("")
            .WithIconStart(FluentIcons.ArrowLeft(IconSize.Size16))
            .WithAppearance(Appearance.Stealth)
            .WithNavigateToHref($"/{threadPath}/{ThreadNodeType.ThreadArea}"));

        var title = GetThreadTitle(node);
        header = header.WithView(Controls.Html($"<h2 style=\"margin: 0 16px;\">Delegations - {System.Web.HttpUtility.HtmlEncode(title)}</h2>"));

        container = container.WithView(header);

        // Delegations list
        if (delegations.Count == 0)
        {
            container = container.WithView(Controls.Html(
                "<div style=\"padding: 32px; text-align: center; color: var(--neutral-foreground-hint);\">" +
                "<p>No delegations yet.</p>" +
                "<p style=\"font-size: 0.9rem;\">When an agent delegates work to another agent, it will appear here.</p>" +
                "</div>"));
        }
        else
        {
            var grid = Controls.LayoutGrid.WithSkin(s => s.WithSpacing(2));

            foreach (var delegation in delegations.OrderByDescending(d => d.LastModified))
            {
                grid = grid.WithView(
                    BuildDelegationCard(delegation),
                    itemSkin => itemSkin.WithXs(12).WithSm(6).WithMd(4).WithLg(4));
            }

            container = container.WithView(grid);
        }

        return container;
    }

    private static UiControl BuildDelegationCard(MeshNode delegationNode)
    {
        var title = delegationNode.Name ?? "Delegation";
        var timestamp = delegationNode.LastModified.ToString("g");
        var path = delegationNode.Path ?? "";

        return Controls.Stack
            .WithStyle("padding: 16px; background: var(--neutral-layer-card-container); border: 1px solid var(--neutral-stroke-rest); border-radius: 8px; cursor: pointer;")
            .WithView(Controls.Stack
                .WithOrientation(Orientation.Horizontal)
                .WithStyle("align-items: center; gap: 8px; margin-bottom: 8px;")
                .WithView(Controls.Icon(FluentIcons.Chat(IconSize.Size20)).WithStyle("color: var(--accent-fill-rest);"))
                .WithView(Controls.Html($"<strong>{System.Web.HttpUtility.HtmlEncode(title)}</strong>")))
            .WithView(Controls.Html($"<span style=\"font-size: 0.85rem; color: var(--neutral-foreground-hint);\">{timestamp}</span>"))
            .WithView(new NavLinkControl("", null, $"/{path}/{ThreadNodeType.ThreadArea}"));
    }

    /// <summary>
    /// Renders a compact thumbnail for thread nodes in catalogs.
    /// Shows title, last activity, and message count synchronously from the
    /// thread node alone — does NOT subscribe to any cell streams. The text
    /// preview is delegated to a <see cref="LayoutAreaControl"/> pointing at
    /// the last cell's "Streaming" area, so the cell hub streams its own
    /// few-lines preview lazily on the Blazor side. Catalog rendering with N
    /// threads no longer pays N × M cell-load round-trips.
    /// </summary>
    public static IObservable<UiControl?> Thumbnail(LayoutAreaHost host, RenderingContext _)
    {
        var hubPath = host.Hub.Address.ToString();
        return host.Workspace.GetMeshNodeStream()
            .Select(node => BuildThumbnail(node, hubPath, host.Hub.JsonSerializerOptions));
    }

    private static UiControl BuildThumbnail(MeshNode? node, string hubPath, JsonSerializerOptions options)
    {
        var thread = node.ContentAs<MeshThread>(options);
        var cellIds = thread?.Messages ?? ImmutableList<string>.Empty;
        var title = node?.Name ?? "Thread";
        var lastActivity = node?.LastModified.ToString("g") ?? "";

        // Preview is a lazy embedded layout area pointing at the last cell's
        // compact Streaming view (last 3 lines + tool-call chips). The cell
        // hub only activates when the catalog tile becomes visible. When the
        // thread has no cells, fall back to a static "No messages yet" line.
        UiControl previewView = cellIds.Count > 0
            ? new LayoutAreaControl(
                    $"{hubPath}/{cellIds[^1]}",
                    new LayoutAreaReference("Streaming"))
                .WithSpinnerType(SpinnerType.Skeleton)
                .WithStyle("margin: 8px 0 0 0; max-height: 60px; overflow: hidden;")
            : Controls.Html(
                "<p style=\"margin: 8px 0 0 0; font-size: 0.9rem; " +
                "color: var(--neutral-foreground-hint);\">No messages yet.</p>");

        var countLabel = cellIds.Count switch
        {
            0 => "",
            1 => "1 message",
            _ => $"{cellIds.Count} messages"
        };

        return Controls.Stack
            .WithStyle("padding: 16px; background: var(--neutral-layer-card-container); border: 1px solid var(--neutral-stroke-rest); border-radius: 8px;")
            .WithView(Controls.Stack
                .WithOrientation(Orientation.Horizontal)
                .WithStyle("align-items: center; gap: 12px; margin-bottom: 8px;")
                .WithView(Controls.Icon(FluentIcons.Chat(IconSize.Size24)).WithStyle("color: var(--accent-fill-rest);"))
                .WithView(Controls.Stack
                    .WithView(Controls.Html($"<strong style=\"display: block;\">{System.Web.HttpUtility.HtmlEncode(title)}</strong>"))
                    .WithView(Controls.Html(
                        $"<span style=\"font-size: 0.85rem; color: var(--neutral-foreground-hint);\">" +
                        $"{lastActivity}" +
                        (string.IsNullOrEmpty(countLabel) ? "" : $" &middot; {countLabel}") +
                        "</span>"))))
            .WithView(previewView)
            .WithView(new NavLinkControl("", null, $"/{hubPath}/{ThreadNodeType.ThreadArea}"));
    }

    /// <summary>
    /// Gets the thread title from node name or falls back to default.
    /// </summary>
    private static string GetThreadTitle(MeshNode? node)
        => !string.IsNullOrEmpty(node?.Name) ? node.Name : "Thread";

    /// <summary>
    /// Adds the Threads view to the layout — shows node-scoped thread history.
    /// </summary>
    public static LayoutDefinition AddThreadsLayoutArea(this LayoutDefinition layout)
        => layout.WithView("Threads", ThreadsView);

    /// <summary>
    /// Adds the Threads view to the hub's layout configuration.
    /// </summary>
    public static MessageHubConfiguration AddThreadsLayoutArea(this MessageHubConfiguration configuration)
        => configuration.AddLayout(layout => layout.AddThreadsLayoutArea());

    /// <summary>
    /// Renders the Threads area showing node-scoped thread history.
    /// </summary>
    private static UiControl ThreadsView(LayoutAreaHost host, RenderingContext _)
    {
        var nodePath = host.Hub.Address.ToString();
        return Controls.MeshSearch
            // -content.status:Done hides finished threads from the node-scoped Threads view.
            .WithHiddenQuery($"nodeType:Thread namespace:{nodePath}/{ThreadNodeType.ThreadPartition} -content.status:Done")
            .WithNamespace(nodePath)
            .WithRenderMode(MeshSearchRenderMode.Flat)
            .WithCreateNodeType("Thread");
    }

    /// <summary>
    /// Header area shown above the chat. Renders, when applicable:
    ///   • A back-link to the parent thread (if this is a delegation sub-thread —
    ///     detected by path nesting under another thread's response message id).
    ///   • A summary of nodes modified during this thread's runs, aggregated across
    ///     every <see cref="ThreadMessage.UpdatedNodes"/> entry, with version-before/
    ///     version-after. Each entry links to the node's Versions area where the
    ///     existing compare/restore UI lives.
    /// Pure subscription on the thread MeshNode; no awaits, no QueryAsync.
    /// </summary>
    public static IObservable<UiControl?> HeaderView(LayoutAreaHost host, RenderingContext _)
    {
        var threadPath = host.Hub.Address.ToString();
        var parentLink = TryBuildParentLink(threadPath);

        var stream = host.Workspace.GetStream(new MeshNodeReference());
        if (stream is null)
            return Observable.Return<UiControl?>(parentLink);

        // Emit an immediate starting value so the LayoutAreaView never shows a skeleton
        // for this area — the header is ancillary and should never block the chat view.
        // Subsequent emissions fold in the aggregated UpdatedNodes summary.
        var initial = Observable.Return<UiControl?>(BuildHeader(parentLink, ImmutableList<NodeChangeEntry>.Empty, threadPath));

        var aggregated = stream
            .Select(change => (change.Value.ContentAs<MeshThread>(host.Hub.JsonSerializerOptions))?.Messages ?? ImmutableList<string>.Empty)
            .Where(ids => ids.Count > 0)
            .Select(ids => (ids, key: string.Join("|", ids)))
            .DistinctUntilChanged(p => p.key)
            .Select(p => CollectUpdatedNodes(host.Hub, threadPath, p.ids))
            .Switch()
            // Dedup the aggregated change-set on its VISIBLE summary (path/op/version per entry).
            // CollectUpdatedNodes re-emits as a round streams even when that summary is unchanged; without
            // this the Header layout area re-rendered per streamed token — a residual render-storm loop.
            // BuildHeader depends only on `updates`, so this is safe.
            .DistinctUntilChanged(updates => string.Join(";", updates.Select(u => $"{u.Path}|{u.Operation}|{u.VersionAfter}")))
            .Select(updates => BuildHeader(parentLink, updates, threadPath));

        return initial.Concat(aggregated);
    }

    /// <summary>
    /// Full-page Changes view. Reuses the header's <see cref="CollectUpdatedNodes"/>
    /// aggregation + <see cref="BuildModifiedNodesHtml"/> grid, plus a
    /// "Revert All" bulk action that posts one
    /// <c>RollbackNodeRequest</c> per entry sequentially (order-sensitive to
    /// avoid parent-deleted-before-child issues).
    /// </summary>
    public static IObservable<UiControl?> ChangesAreaView(LayoutAreaHost host, RenderingContext _)
    {
        var threadPath = host.Hub.Address.ToString();
        var stream = host.Workspace.GetStream(new MeshNodeReference());
        if (stream is null)
            return Observable.Return<UiControl?>(BuildChangesEmpty());

        var aggregated = stream
            .Select(change => (change.Value.ContentAs<MeshThread>(host.Hub.JsonSerializerOptions))?.Messages ?? ImmutableList<string>.Empty)
            .DistinctUntilChanged(ids => string.Join("|", ids))
            .Select(ids => ids.IsEmpty
                ? Observable.Return(ImmutableList<NodeChangeEntry>.Empty)
                : CollectUpdatedNodes(host.Hub, threadPath, ids))
            .Switch();

        return aggregated.Select(updates => BuildChangesPage(host.Hub, threadPath, updates));
    }

    private static UiControl BuildChangesEmpty()
        => Controls.Html(
            "<div style=\"padding:24px; color:var(--neutral-foreground-hint); text-align:center;\">" +
            "<p style=\"margin:0;\">No node changes recorded for this thread.</p></div>");

    private static UiControl BuildChangesPage(
        IMessageHub hub, string threadPath, ImmutableList<NodeChangeEntry> updates)
    {
        var container = Controls.Stack
            .WithWidth("100%")
            .WithStyle("padding:24px; gap:16px;");

        // Header row: title + count + Revert All button.
        var headerStyle = "display:flex; align-items:center; gap:12px;";
        var titleHtml =
            "<h2 style=\"margin:0; font-size:1.5rem; font-weight:600;\">Changes</h2>" +
            $"<span style=\"color:var(--neutral-foreground-hint); font-size:0.9rem;\">" +
            $"{updates.Count} node{(updates.Count == 1 ? "" : "s")} modified</span>";

        var headerRow = Controls.Stack
            .WithOrientation(Orientation.Horizontal)
            .WithStyle(headerStyle)
            .WithView(Controls.Html(
                $"<div style=\"display:flex; align-items:center; gap:12px; flex:1;\">{titleHtml}</div>"));

        // Revert All button — only enabled when there's something to revert.
        var revertable = updates.Where(e => e.VersionBefore.HasValue).ToImmutableList();
        if (revertable.Count > 0)
        {
            headerRow = headerRow.WithView(Controls.Button($"Revert all ({revertable.Count})")
                .WithAppearance(Appearance.Neutral)
                .WithIconStart(FluentIcons.ArrowUndo(IconSize.Size16))
                .WithClickAction(_ => RevertAllChanges(hub, revertable)));
        }
        container = container.WithView(headerRow);

        if (updates.IsEmpty)
        {
            container = container.WithView(BuildChangesEmpty());
            return container;
        }

        // Reuse the same git-like grid as the header summary — single source of truth
        // for path / version chips / Diff / per-row Restore links.
        container = container.WithView(Controls.Html(BuildModifiedNodesHtml(updates, threadPath)));
        return container;
    }

    /// <summary>
    /// Posts <see cref="RollbackNodeRequest"/> for every entry with a
    /// <see cref="NodeChangeEntry.VersionBefore"/>, in sequence. Sequential
    /// (not parallel) so dependent ordering (parent-before-child for creates,
    /// child-before-parent for deletes) stays predictable. Fire-and-forget per
    /// request; failures are independent.
    /// </summary>
    private static void RevertAllChanges(IMessageHub hub, ImmutableList<NodeChangeEntry> entries)
    {
        foreach (var entry in entries)
        {
            if (!entry.VersionBefore.HasValue) continue;
            hub.Post(new RollbackNodeRequest(entry.Path, entry.VersionBefore.Value));
        }
    }

    /// <summary>
    /// Walks <paramref name="messageIds"/>, requests each satellite ThreadMessage via
    /// GetDataRequest (Post + RegisterCallback wrapped as an Observable), accumulates
    /// their UpdatedNodes, and emits the aggregated list once all responses arrive.
    /// </summary>
    private static IObservable<ImmutableList<NodeChangeEntry>> CollectUpdatedNodes(
        IMessageHub hub, string threadPath, ImmutableList<string> messageIds)
    {
        if (messageIds.IsEmpty) return Observable.Return(ImmutableList<NodeChangeEntry>.Empty);

        var subjects = messageIds.Select(id =>
        {
            var subject = new System.Reactive.Subjects.AsyncSubject<ImmutableList<NodeChangeEntry>>();
            var del = hub.Post(new GetDataRequest(new MeshNodeReference()),
                o => o.WithTarget(new Address($"{threadPath}/{id}")));
            if (del is null)
            {
                subject.OnNext(ImmutableList<NodeChangeEntry>.Empty);
                subject.OnCompleted();
            }
            else
            {
                hub.Observe((IMessageDelivery)del)
                    .Subscribe(
                        resp =>
                        {
                            var msg = resp.Message is GetDataResponse gdr
                                ? (gdr.Data as MeshNode)?.ContentAs<ThreadMessage>(hub.JsonSerializerOptions)
                                : null;
                            subject.OnNext(msg?.UpdatedNodes ?? ImmutableList<NodeChangeEntry>.Empty);
                            subject.OnCompleted();
                        },
                        _ =>
                        {
                            subject.OnNext(ImmutableList<NodeChangeEntry>.Empty);
                            subject.OnCompleted();
                        });
            }
            return subject.AsObservable();
        }).ToList();

        return Observable.CombineLatest(subjects)
            .Select(parts => ThreadExecution.AggregateNodeChanges(
                parts.SelectMany(p => p).ToImmutableList()))
            .Take(1)
            .Timeout(TimeSpan.FromSeconds(5))
            .Catch<ImmutableList<NodeChangeEntry>, Exception>(_ =>
                Observable.Return(ImmutableList<NodeChangeEntry>.Empty));
    }

    private static UiControl? TryBuildParentLink(string threadPath)
    {
        // Sub-thread paths nest under a parent response message:
        //   {parentThreadPath}/{parentResponseMsgId}/{thisThreadId}
        // If we can find a ".../<8-hex-id>/<id>" pattern we treat this as a delegation.
        var segments = threadPath.Split('/');
        if (segments.Length < 3) return null;
        var parentMsgId = segments[^2];
        if (parentMsgId.Length != 8) return null;
        var parentThreadPath = string.Join('/', segments[..^2]);
        if (string.IsNullOrEmpty(parentThreadPath)) return null;

        var encoded = System.Web.HttpUtility.HtmlEncode(parentThreadPath);
        var html =
            $"<a href=\"/{encoded}\" style=\"display:inline-flex; align-items:center; gap:6px; " +
            $"font-size:0.78rem; color:var(--accent-fill-rest); text-decoration:none; " +
            $"padding:4px 10px; border:1px solid var(--neutral-stroke-rest); border-radius:14px;\">" +
            $"<span>&#8592;</span> Delegated from <code style=\"font-size:0.72rem;\">{encoded}</code></a>";
        return Controls.Html(html);
    }

    private static UiControl? BuildHeader(UiControl? parentLink, ImmutableList<NodeChangeEntry> updates, string threadPath)
    {
        if (parentLink is null && updates.IsEmpty) return null;

        var stack = Controls.Stack
            .WithStyle("gap:6px; padding:8px 12px; margin-bottom:8px; " +
                       "background:var(--neutral-layer-1); border:1px solid var(--neutral-stroke-rest); " +
                       "border-radius:8px;");

        if (parentLink is not null)
            stack = stack.WithView(parentLink);

        if (!updates.IsEmpty)
            stack = stack.WithView(Controls.Html(BuildModifiedNodesHtml(updates, threadPath)));

        return stack;
    }

    /// <summary>
    /// Git-like panel for the aggregated UpdatedNodes list:
    /// - Tabular layout (CSS grid): path · old-ver · → · new-ver · Diff · Restore v{old} · Restore v{new}.
    /// - All action links visible inline on each row (no hidden ⋯ menu).
    /// - Theme-safe colours that work in dark + light mode (no white-on-light-blue).
    /// - Paths rendered relative to the thread's parent namespace so the most
    ///   interesting segment (leaf) stays visible when the table narrows.
    /// - On screens &lt; 720 px the whole section collapses behind a summary row.
    /// </summary>
    private static string BuildModifiedNodesHtml(ImmutableList<NodeChangeEntry> updates, string threadPath)
    {
        // Derive the ancestor prefix we can strip from each node path to produce a
        // shorter display form. For a thread at "Org/_Thread/abc", the interesting
        // prefix to strip is "Org/" (the thread's root namespace, above _Thread).
        var threadIdx = threadPath.IndexOf("/_Thread/", StringComparison.Ordinal);
        var shortenPrefix = threadIdx > 0 ? threadPath[..(threadIdx + 1)] : null;

        static string Shorten(string path, string? prefix) =>
            prefix is not null && path.StartsWith(prefix, StringComparison.Ordinal)
                ? path[prefix.Length..]
                : path;

        var sb = new System.Text.StringBuilder();

        // Inline <style> block scoped by a unique class so the grid + media-query
        // rules apply to the rendered HTML. Lives inside the injected markdown; Blazor
        // passes it through untouched.
        sb.Append("""
            <style>
            .thread-mod-nodes { width:100%; }
            .thread-mod-nodes > summary {
                list-style:none; cursor:pointer;
                font-size:0.8rem; font-weight:600;
                color:var(--neutral-foreground-hint);
                padding:4px 0;
            }
            .thread-mod-nodes > summary::-webkit-details-marker { display:none; }
            .thread-mod-grid {
                display:grid;
                grid-template-columns: minmax(0,1fr) auto auto auto auto auto auto;
                align-items:center;
                gap:6px 10px;
                margin-top:6px;
                font-size:0.78rem;
                color:var(--neutral-foreground-rest);
            }
            .thread-mod-grid a {
                text-decoration:none;
                color:var(--accent-fill-rest);
            }
            .thread-mod-grid a:hover { text-decoration:underline; }
            .thread-mod-row {
                display:contents;
            }
            .thread-mod-path {
                min-width:0;
                overflow:hidden; text-overflow:ellipsis; white-space:nowrap;
                padding:4px 6px;
                border-radius:4px;
            }
            .thread-mod-path:hover { background:var(--neutral-layer-2); }
            .thread-mod-chip {
                padding:1px 6px; border-radius:10px;
                background:var(--neutral-layer-3);
                color:var(--neutral-foreground-rest) !important;
                font-family:monospace; font-size:0.72rem;
                border:1px solid var(--neutral-stroke-rest);
            }
            .thread-mod-chip-new {
                color:var(--accent-fill-rest) !important;
                border-color:var(--accent-fill-rest);
                font-weight:600;
            }
            .thread-mod-arrow { color:var(--neutral-foreground-hint); }
            .thread-mod-action {
                padding:2px 8px; border-radius:4px;
                font-size:0.74rem;
                color:var(--accent-fill-rest) !important;
                white-space:nowrap;
            }
            .thread-mod-action:hover { background:var(--neutral-layer-2); }
            .thread-mod-action-muted {
                color:var(--neutral-foreground-hint) !important;
            }
            .thread-mod-marker {
                font-size:0.72rem; color:var(--neutral-foreground-hint); font-style:italic;
            }
            /* Small-screen: collapse the whole section under its summary. */
            @media (max-width: 720px) {
                .thread-mod-nodes[data-wide-inline="true"] > .thread-mod-grid { display:none; }
                .thread-mod-nodes[data-wide-inline="true"][open] > .thread-mod-grid { display:grid; }
                .thread-mod-grid {
                    grid-template-columns: minmax(0,1fr) auto auto;
                }
                .thread-mod-action-mobile-hide { display:none; }
            }
            /* Wide-screen: the <details> is always open (summary acts as header). */
            @media (min-width: 721px) {
                .thread-mod-nodes[data-wide-inline="true"] > .thread-mod-grid { display:grid; }
                .thread-mod-nodes[data-wide-inline="true"] > summary { pointer-events:none; }
            }
            </style>
            """);

        sb.Append("<details class=\"thread-mod-nodes\" data-wide-inline=\"true\" open>");
        sb.Append($"<summary><span style=\"display:inline-block; width:10px;\">&#9656;</span> Modified nodes ({updates.Count})</summary>");

        sb.Append("<div class=\"thread-mod-grid\">");

        foreach (var entry in updates)
        {
            var path = entry.Path;
            var pathEnc = System.Web.HttpUtility.HtmlEncode(path);
            var displayEnc = System.Web.HttpUtility.HtmlEncode(Shorten(path, shortenPrefix));
            var op = entry.Operation ?? "";

            sb.Append("<div class=\"thread-mod-row\">");

            // Column 1: path (truncates on narrow layouts)
            sb.Append(
                $"<a href=\"/{pathEnc}\" title=\"{pathEnc}\" class=\"thread-mod-path\">{displayEnc}</a>");

            // Column 2: old version chip or "new" marker
            if (entry.VersionBefore is { } vb)
                sb.Append(
                    $"<a href=\"/{pathEnc}/Versions?version={vb}\" class=\"thread-mod-chip\" title=\"View v{vb}\">v{vb}</a>");
            else if (op.Equals("Created", StringComparison.OrdinalIgnoreCase))
                sb.Append("<span class=\"thread-mod-marker\">new</span>");
            else
                sb.Append("<span></span>");

            // Column 3: arrow
            sb.Append("<span class=\"thread-mod-arrow\">&#8594;</span>");

            // Column 4: new version chip or "deleted" marker
            if (entry.VersionAfter is { } va)
                sb.Append(
                    $"<a href=\"/{pathEnc}/Versions?version={va}\" class=\"thread-mod-chip thread-mod-chip-new\" title=\"View v{va}\">v{va}</a>");
            else if (op.Equals("Deleted", StringComparison.OrdinalIgnoreCase))
                sb.Append("<span class=\"thread-mod-marker\">deleted</span>");
            else
                sb.Append("<span></span>");

            // Column 5: Diff (old ↔ new) — points to VersionDiff with from/to params.
            if (entry.VersionBefore.HasValue && entry.VersionAfter.HasValue)
                sb.Append(
                    $"<a href=\"/{pathEnc}/VersionDiff?from={entry.VersionBefore.Value}&to={entry.VersionAfter.Value}\" " +
                    $"class=\"thread-mod-action thread-mod-action-mobile-hide\" " +
                    $"title=\"Compare v{entry.VersionBefore.Value} to v{entry.VersionAfter.Value}\">Diff</a>");
            else
                sb.Append("<span class=\"thread-mod-action-mobile-hide\"></span>");

            // Column 6: Restore to old — opens VersionDiff (which has the Restore button).
            if (entry.VersionBefore.HasValue)
                sb.Append(
                    $"<a href=\"/{pathEnc}/VersionDiff?version={entry.VersionBefore.Value}\" " +
                    $"class=\"thread-mod-action thread-mod-action-muted thread-mod-action-mobile-hide\" " +
                    $"title=\"Revert to v{entry.VersionBefore.Value}\">Restore v{entry.VersionBefore.Value}</a>");
            else
                sb.Append("<span class=\"thread-mod-action-mobile-hide\"></span>");

            // Column 7: Restore to new — opens VersionDiff (which has the Restore button).
            if (entry.VersionAfter.HasValue)
                sb.Append(
                    $"<a href=\"/{pathEnc}/VersionDiff?version={entry.VersionAfter.Value}\" " +
                    $"class=\"thread-mod-action thread-mod-action-muted thread-mod-action-mobile-hide\" " +
                    $"title=\"Restore v{entry.VersionAfter.Value}\">Restore v{entry.VersionAfter.Value}</a>");
            else
                sb.Append("<span class=\"thread-mod-action-mobile-hide\"></span>");

            sb.Append("</div>");
        }

        sb.Append("</div>");
        sb.Append("</details>");
        return sb.ToString();
    }

    /// <summary>
    /// Reads the still-pending user-message texts from <paramref name="thread"/>.
    /// Pending = id is in <see cref="Thread.UserMessageIds"/> AND in
    /// <see cref="Thread.PendingUserMessages"/>. The <c>check_inbox</c> tool
    /// drains this queue mid-stream — once drained, the texts disappear from
    /// this list (they remain visible as user cells in the conversation).
    /// </summary>
    internal static IReadOnlyList<string> ExtractPendingTexts(MeshThread? thread)
    {
        if (thread is null || thread.PendingUserMessages.IsEmpty)
            return Array.Empty<string>();

        var result = new List<string>(thread.PendingUserMessages.Count);
        foreach (var id in thread.UserMessageIds)
        {
            if (thread.PendingUserMessages.TryGetValue(id, out var msg))
                result.Add(msg.Text);
        }
        // Catch any pending entries not in UserMessageIds (defensive — shouldn't
        // happen via the normal AppendUserInput path, but don't silently drop).
        foreach (var (id, msg) in thread.PendingUserMessages)
            if (!thread.UserMessageIds.Contains(id))
                result.Add(msg.Text);

        return result;
    }
}
