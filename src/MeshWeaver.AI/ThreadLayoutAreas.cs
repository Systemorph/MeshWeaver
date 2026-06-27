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
                .WithView(ThreadNodeType.FullHeaderArea, FullHeaderView)
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
    /// (title, context link, modified-nodes summary, Mark Done) is rendered by the
    /// chat control INSIDE its scrollable message area (see <see cref="FullHeaderView"/>),
    /// so it scrolls away with the conversation instead of staying pinned at the top —
    /// hence <c>WithShowFullHeader()</c> rather than a sibling header above the chat.
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
        var vmStream = host.Workspace.GetMeshNodeStream().Select(node => BuildThreadViewModel(node, hubPath, host.Hub.JsonSerializerOptions));
        host.RegisterForDisposal(vmStream.DistinctUntilChanged().Subscribe(vm => host.UpdateData(ThreadDataKey, vm)));

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
    /// The full-page thread hero header — gradient surface, big glowing chat icon,
    /// inline-editable title + description, context back-link, live message-count
    /// subtitle, and Mark Done / Reopen toggle. Rendered by the full-page chat view
    /// as the FIRST item INSIDE the scrollable message area (gated by
    /// <see cref="ThreadChatControl.ShowFullHeader"/>) so it scrolls away with the
    /// conversation rather than being pinned above it. Self-contained: pushes its own
    /// title / subtitle / contextLink / description data and binds them — same pattern
    /// as <see cref="HeaderView"/>. The side panel does NOT request this area (its
    /// title lives in the panel chrome).
    /// </summary>
    public static UiControl? FullHeaderView(LayoutAreaHost host, RenderingContext _)
    {
        var hubPath = host.Hub.Address.ToString();

        var vmStream = host.Workspace.GetMeshNodeStream().Select(node => BuildThreadViewModel(node, hubPath, host.Hub.JsonSerializerOptions));

        // Push title to data section — data-bound, no observable control rebuild.
        var titleStream = host.Workspace.GetMeshNodeStream()
            .Select(GetThreadTitle)
            .DistinctUntilChanged();
        host.RegisterForDisposal(titleStream.Subscribe(title => host.UpdateData("title", title)));

        // Push description to data section — read-only viewers see it under the title
        // (users with Update permission get an inline auto-saving editor instead). Empty
        // string when unset so the read-only Html view renders nothing.
        host.RegisterForDisposal(host.Workspace.GetMeshNodeStream()
            .Select(node => node?.Description ?? string.Empty)
            .DistinctUntilChanged()
            .Subscribe(desc => host.UpdateData("description",
                string.IsNullOrWhiteSpace(desc)
                    ? string.Empty
                    : "<div style=\"font-size: 0.92rem; color: var(--neutral-foreground-hint); " +
                      "margin-top: 2px; line-height: 1.4; white-space: pre-wrap;\">" +
                      System.Web.HttpUtility.HtmlEncode(desc) + "</div>")));

        // Push context link HTML to data section — pill-shaped breadcrumb chip.
        host.RegisterForDisposal(vmStream.DistinctUntilChanged().Subscribe(vm =>
        {
            if (!string.IsNullOrEmpty(vm.InitialContext))
            {
                var displayName = System.Web.HttpUtility.HtmlEncode(vm.InitialContextDisplayName ?? vm.InitialContext);
                host.UpdateData("contextLink",
                    "<div style=\"display: block; margin-bottom: 14px;\">" +
                    $"<a href=\"/{vm.InitialContext}\" " +
                    "style=\"display: inline-flex; align-items: center; gap: 6px; " +
                    "padding: 4px 12px 4px 8px; border-radius: 999px; " +
                    "background: color-mix(in srgb, var(--accent-fill-rest) 10%, transparent); " +
                    "border: 1px solid color-mix(in srgb, var(--accent-fill-rest) 30%, transparent); " +
                    "font-size: 0.78rem; font-weight: 500; " +
                    "color: var(--accent-fill-rest); text-decoration: none; " +
                    "transition: background 150ms ease, transform 150ms ease;\" " +
                    "onmouseover=\"this.style.background='color-mix(in srgb, var(--accent-fill-rest) 18%, transparent)'; this.style.transform='translateX(-2px)';\" " +
                    "onmouseout=\"this.style.background='color-mix(in srgb, var(--accent-fill-rest) 10%, transparent)'; this.style.transform='translateX(0)';\">" +
                    "<span style=\"font-size: 14px; line-height: 1;\">&larr;</span> " +
                    $"<span>{displayName}</span></a>" +
                    "</div>");
            }
        }));

        // Push subtitle (message count) to the data section — reactive.
        host.RegisterForDisposal(vmStream
            .Select(vm => vm.Messages?.Count ?? 0)
            .DistinctUntilChanged()
            .Subscribe(count =>
            {
                var label = count switch
                {
                    0 => "No messages yet",
                    1 => "1 message",
                    _ => $"{count} messages"
                };
                host.UpdateData("subtitle",
                    "<div style=\"font-size: 0.85rem; color: var(--neutral-foreground-hint); " +
                    "margin-top: 4px; display: inline-flex; align-items: center; gap: 8px;\">" +
                    "<span style=\"width: 6px; height: 6px; border-radius: 50%; " +
                    "background: var(--accent-fill-rest); display: inline-block; " +
                    "box-shadow: 0 0 8px var(--accent-fill-rest); animation: thread-hdr-pulse 2.4s ease-in-out infinite;\"></span>" +
                    $"<span>{label}</span>" +
                    "</div>" +
                    "<style>@keyframes thread-hdr-pulse { 0%, 100% { opacity: 0.55; } 50% { opacity: 1; } }</style>");
            }));

        // Hero header: gradient surface, big glowing chat icon, title + live subtitle.
        return Controls.Stack
            .WithClass("thread-full-header")
            .WithWidth("100%")
            .WithStyle(
                "padding: 24px 28px 22px 28px; margin-bottom: 20px; " +
                "border-radius: 14px; gap: 10px; " +
                "background: linear-gradient(135deg, " +
                "color-mix(in srgb, var(--accent-fill-rest) 8%, var(--neutral-layer-1)), " +
                "var(--neutral-layer-1) 70%); " +
                "border: 1px solid color-mix(in srgb, var(--accent-fill-rest) 18%, var(--neutral-stroke-rest)); " +
                "box-shadow: 0 6px 24px -8px color-mix(in srgb, var(--accent-fill-rest) 25%, transparent);")
            .WithView(Controls.Html(new JsonPointerReference(LayoutAreaReference.GetDataPointer("contextLink"))))
            .WithView(Controls.Stack
                .WithOrientation(Orientation.Horizontal)
                .WithStyle("align-items: center; gap: 18px; flex: 1; min-width: 0;")
                .WithView(Controls.Html(
                    "<div style=\"position: relative; width: 56px; height: 56px; flex: 0 0 56px; " +
                    "border-radius: 50%; " +
                    "background: linear-gradient(135deg, var(--accent-fill-rest), " +
                    "color-mix(in srgb, var(--accent-fill-rest) 60%, #7c5cd1)); " +
                    "display: inline-flex; align-items: center; justify-content: center; " +
                    "box-shadow: 0 8px 20px -4px color-mix(in srgb, var(--accent-fill-rest) 55%, transparent), " +
                    "inset 0 1px 0 rgba(255,255,255,0.25);\">" +
                    "<img src=\"/static/NodeTypeIcons/chat.svg\" alt=\"\" " +
                    "style=\"width: 32px; height: 32px; object-fit: contain; " +
                    "filter: brightness(0) invert(1) drop-shadow(0 1px 2px rgba(0,0,0,0.25));\" />" +
                    "</div>"))
                .WithView(Controls.Stack
                    .WithStyle("gap: 4px; min-width: 0; flex: 1;")
                    // Title + description: inline auto-saving editors for users with Update
                    // permission; read-only display otherwise. Bound DIRECTLY to MeshNode.Name /
                    // MeshNode.Description (fields-mode node-bound DataContext) — one source of
                    // truth, no /data replica, no save subscription. See Doc/GUI/DataBinding.
                    .WithView((h, _) => h.Hub.GetEffectivePermissions(hubPath)
                        .Select(p => p.HasFlag(Permission.Update))
                        .DistinctUntilChanged()
                        .Select(canEdit => (UiControl?)BuildTitleEditor(hubPath, canEdit)))
                    .WithView(Controls.Html(new JsonPointerReference(LayoutAreaReference.GetDataPointer("subtitle")))))
                // Mark Done / Reopen toggle — reactive. Hidden while the
                // thread is executing (MarkThreadDone's CAS guard would
                // refuse anyway, but hiding the button is cleaner UX).
                .WithView((h, _) => h.Workspace.GetMeshNodeStream()
                    .Select(node =>
                    {
                        var t = node.ContentAs<MeshThread>(h.Hub.JsonSerializerOptions);
                        if (t is null || t.IsExecuting) return (UiControl?)null;
                        var isDone = t.Status == ThreadExecutionStatus.Done;
                        var label = isDone ? "Reopen" : "Mark Done";
                        var icon = isDone
                            ? FluentIcons.ArrowUndo(IconSize.Size16)
                            : FluentIcons.Checkmark(IconSize.Size16);
                        return (UiControl?)Controls.Button(label)
                            .WithAppearance(isDone ? Appearance.Neutral : Appearance.Accent)
                            .WithIconStart(icon)
                            .WithClickAction(_ =>
                                h.Hub.MarkThreadDone(hubPath, !isDone));
                    })));
    }

    /// <summary>
    /// Builds the <see cref="ThreadViewModel"/> snapshot pushed to the data section
    /// from the thread's own MeshNode. Shared by <see cref="ThreadView"/>,
    /// <see cref="ThreadChatView"/>, and <see cref="FullHeaderView"/>.
    /// </summary>
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
            InitialContext = contextPath,
            InitialContextDisplayName = contextDisplayName,
            IsExecuting = threadContent?.IsExecuting ?? false,
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

        var vmStream = ownNodeStream.Select(node => BuildThreadViewModel(node, hubPath, host.Hub.JsonSerializerOptions));
        host.RegisterForDisposal(vmStream.DistinctUntilChanged().Subscribe(vm => host.UpdateData(ThreadDataKey, vm)));

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
    /// The title + description block in the thread hero header.
    /// <para>For users with <see cref="Permission.Update"/> both render as inline,
    /// auto-saving editors bound DIRECTLY to the thread node's <see cref="MeshNode.Name"/>
    /// and <see cref="MeshNode.Description"/> (fields-mode node-bound DataContext) —
    /// one source of truth, no <c>/data</c> replica, no save subscription. Editability
    /// is gated, not just the write: read-only viewers get the gradient HTML title plus
    /// the description (pushed to the data section), the description hidden when unset.</para>
    /// </summary>
    private static UiControl BuildTitleEditor(string nodePath, bool canEdit)
    {
        if (!canEdit)
            return Controls.Stack
                .WithStyle("gap: 4px; min-width: 0;")
                .WithView(Controls.Html(new JsonPointerReference(LayoutAreaReference.GetDataPointer("title")))
                    .WithStyle("margin: 0; font-size: 1.85rem; font-weight: 600; " +
                               "letter-spacing: -0.01em; line-height: 1.15; " +
                               "background: linear-gradient(135deg, var(--neutral-foreground-rest), " +
                               "color-mix(in srgb, var(--accent-fill-rest) 80%, var(--neutral-foreground-rest))); " +
                               "-webkit-background-clip: text; background-clip: text; " +
                               "-webkit-text-fill-color: transparent; color: transparent;"))
                .WithView(Controls.Html(new JsonPointerReference(LayoutAreaReference.GetDataPointer("description"))));

        var fieldsContext = LayoutAreaReference.GetMeshNodeDataContext(nodePath, bindContent: false);

        var titleField = new TextFieldControl(new JsonPointerReference(nameof(MeshNode.Name)))
        {
            Immediate = true,
            Placeholder = "Untitled thread",
            DataContext = fieldsContext
        }.WithStyle("font-size: 1.5rem; font-weight: 600; letter-spacing: -0.01em;")
         .WithClass("thread-title-field");

        var descriptionField = new TextAreaControl(new JsonPointerReference(nameof(MeshNode.Description)))
        {
            Immediate = true,
            Placeholder = "Add a description…",
            DataContext = fieldsContext
        }.WithRows(2).WithClass("thread-description-field");

        return Controls.Stack
            .WithStyle("gap: 6px; min-width: 0;")
            .WithView(titleField)
            .WithView(descriptionField);
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
                                ? (gdr.Data as MeshNode)?.Content as ThreadMessage
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
