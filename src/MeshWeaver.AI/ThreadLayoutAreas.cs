using System.Collections.Immutable;
using System.Reactive.Linq;
using MeshWeaver.Application.Styles;
using MeshWeaver.Data;
using MeshWeaver.Domain;
using MeshWeaver.Graph;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Mesh;
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
            .WithHandler<ResubmitMessageRequest>(ThreadMessageHandlers.HandleResubmitMessage)
            .WithHandler<DeleteFromMessageRequest>(ThreadMessageHandlers.HandleDeleteFromMessage)
            .AddDefaultMeshMenu()
            .AddNodeMenuItems("SidePanel", SidePanelMenuProvider)
            .AddNodeMenuItems(DelegationsMenuProvider)
            .AddLayout(layout => layout
                .WithDefaultArea(ThreadNodeType.ThreadArea)
                .WithView(ThreadNodeType.ThreadArea, ThreadView)
                .WithView(MeshNodeLayoutAreas.OverviewArea, ThreadProgressView)
                .WithView(ThreadNodeType.ThreadChatArea, ThreadChatView)
                .WithView(ThreadNodeType.StreamingArea, StreamingView)
                .WithView(ThreadNodeType.HistoryArea, HistoryView)
                .WithView(ThreadNodeType.HeaderArea, HeaderView)
                .WithView(MeshNodeLayoutAreas.ThumbnailArea, Thumbnail)
                .WithView(MeshNodeLayoutAreas.ThreadsArea, ThreadsCatalog));

    /// <summary>
    /// Side panel menu items (New Chat, History, Full Screen).
    /// </summary>
    private static async IAsyncEnumerable<NodeMenuItemDefinition> SidePanelMenuProvider(
        LayoutAreaHost host, RenderingContext ctx)
    {
        await Task.CompletedTask;
        yield return new("New Chat", "new-chat", Order: 0);
        yield return new("History", "history", Order: 10);
        yield return new("Full Screen", "fullscreen", Order: 20);
    }

    /// <summary>
    /// Main menu item: Delegations (sub-thread history).
    /// </summary>
    private static async IAsyncEnumerable<NodeMenuItemDefinition> DelegationsMenuProvider(
        LayoutAreaHost host, RenderingContext ctx)
    {
        await Task.CompletedTask;
        var hubPath = host.Hub.Address.ToString();
        yield return new("Delegations", ThreadNodeType.HistoryArea, Order: 12,
            Href: MeshNodeLayoutAreas.BuildUrl(hubPath, ThreadNodeType.HistoryArea));
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
                .WithHiddenQuery($"namespace:{hubPath}/{ThreadNodeType.ThreadPartition} nodeType:{ThreadNodeType.NodeType} sort:lastModified-desc")
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
    /// Shows the thread title (observable, bound to meshNode.Name) and a
    /// ThreadChatControl with data-bound Thread content.
    ///
    /// IMPORTANT: The ThreadChatControl is emitted ONCE. Thread content is pushed
    /// separately via host.UpdateData() and data-bound on the control to avoid
    /// re-creating it (and thus re-rendering the Monaco editor) on every change.
    /// </summary>
    public static UiControl? ThreadView(LayoutAreaHost host, RenderingContext _)
    {
        var hubPath = host.Hub.Address.ToString();

        // Node stream — drives the observable title and chat control context
        var stream = host.Workspace.GetStream<MeshNode>();

        // Push ThreadViewModel to data section — contains all thread state for the Blazor view.
        var vmStream = stream!.Select(nodes =>
        {
            var node = nodes!.First(n => n.Path == hubPath);
            var threadContent = node?.Content as MeshThread;
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
                TokensUsed = threadContent?.TokensUsed ?? 0,
                ExecutionStartedAt = threadContent?.ExecutionStartedAt,
            };
        });
        host.RegisterForDisposal(vmStream.DistinctUntilChanged().Subscribe(vm => host.UpdateData(ThreadDataKey, vm)));

        // Push title to data section — data-bound, no observable control rebuild
        var titleStream = stream!.Select(nodes =>
        {
            var node = nodes!.First(n => n.Path == hubPath);
            return GetThreadTitle(node);
        }).DistinctUntilChanged();
        host.RegisterForDisposal(titleStream.Subscribe(title => host.UpdateData("title", title)));

        // Push context link HTML to data section
        host.RegisterForDisposal(vmStream.DistinctUntilChanged().Subscribe(vm =>
        {
            if (!string.IsNullOrEmpty(vm.InitialContext))
            {
                var displayName = System.Web.HttpUtility.HtmlEncode(vm.InitialContextDisplayName ?? vm.InitialContext);
                host.UpdateData("contextLink",
                    $"<a href=\"/{vm.InitialContext}\" style=\"font-size: 0.85rem; color: var(--accent-fill-rest); " +
                    $"text-decoration: none; display: inline-flex; align-items: center; gap: 4px;\">" +
                    $"<span style=\"font-size: 12px;\">&larr;</span> {displayName}</a>");
            }
        }));

        // Header: chat icon + context link + h1 title (hidden in side panel via CSS)
        var header = Controls.Stack
            .WithClass("thread-full-header")
            .WithWidth("100%")
            .WithStyle("padding: 16px 24px 24px 24px; margin-bottom: 24px; border-bottom: 1px solid var(--neutral-stroke-rest);")
            .WithView(Controls.Html(new JsonPointerReference(LayoutAreaReference.GetDataPointer("contextLink"))))
            .WithView(Controls.Stack
                .WithOrientation(Orientation.Horizontal)
                .WithStyle("align-items: center; gap: 16px;")
                .WithView(Controls.Html(
                    "<img src=\"/static/NodeTypeIcons/chat.svg\" alt=\"\" style=\"width: 48px; height: 48px; border-radius: 8px; object-fit: contain;\" />"))
                .WithView(Controls.Html(new JsonPointerReference(LayoutAreaReference.GetDataPointer("title")))
                    .WithStyle("margin: 0; font-size: 2rem; font-weight: bold;")));

        // Static container — never rebuilt
        return Controls.Stack
            .WithWidth("100%")
            .WithStyle("flex: 1; min-height: 0; display: flex; flex-direction: column;")
            .WithView(header)
            .WithView(new ThreadChatControl()
                .WithThreadViewModel(new JsonPointerReference(LayoutAreaReference.GetDataPointer(ThreadDataKey)))
                .WithStyle("flex: 1; min-height: 0; overflow: hidden;"));
    }

    /// <summary>
    /// Renders just the ThreadChatControl without the full-page header.
    /// Used by the side panel — the title is pushed to the data section
    /// so the side panel header can display it.
    /// </summary>
    public static UiControl? ThreadChatView(LayoutAreaHost host, RenderingContext _)
    {
        var hubPath = host.Hub.Address.ToString();
        var stream = host.Workspace.GetStream<MeshNode>();

        var vmStream = stream!.Select(nodes =>
        {
            var node = nodes!.First(n => n.Path == hubPath);
            var threadContent = node?.Content as MeshThread;
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
                TokensUsed = threadContent?.TokensUsed ?? 0,
                ExecutionStartedAt = threadContent?.ExecutionStartedAt,
            };
        });
        host.RegisterForDisposal(vmStream.DistinctUntilChanged().Subscribe(vm => host.UpdateData(ThreadDataKey, vm)));

        // Push title to data section so the side panel header can read it
        var titleStream = stream!.Select(nodes =>
        {
            var node = nodes!.First(n => n.Path == hubPath);
            return GetThreadTitle(node);
        }).DistinctUntilChanged();
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
        var stream = host.Workspace.GetStream<MeshNode>();

        return stream!
            .Select(nodes =>
            {
                var node = nodes!.FirstOrDefault(n => n.Path == hubPath);
                var thread = node?.Content as MeshThread;
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
        var stream = host.Workspace.GetStream<MeshNode>();

        return stream!
            .Select(nodes =>
            {
                var node = nodes!.FirstOrDefault(n => n.Path == hubPath);
                var thread = node?.Content as MeshThread;
                return (IsExecuting: thread?.IsExecuting ?? false, thread?.ActiveMessageId);
            })
            .DistinctUntilChanged()
            .Select(state =>
            {
                if (!state.IsExecuting || string.IsNullOrEmpty(state.ActiveMessageId))
                    return (UiControl?)null;

                var responsePath = $"{hubPath}/{state.ActiveMessageId}";
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

        // Get the node from the workspace stream
        var nodeStream = host.Workspace.GetStream<MeshNode>()?.Select(nodes => nodes ?? Array.Empty<MeshNode>())
            ?? Observable.Return<MeshNode[]>(Array.Empty<MeshNode>());

        // Query for child Thread nodes (delegations)
        var childrenStream = Observable.FromAsync(async () =>
        {
            try
            {
                return await meshQuery.QueryAsync<MeshNode>($"namespace:{hubPath} nodeType:{ThreadNodeType.NodeType}").ToListAsync() as IReadOnlyList<MeshNode>;
            }
            catch
            {
                return Array.Empty<MeshNode>() as IReadOnlyList<MeshNode>;
            }
        });

        return nodeStream.CombineLatest(childrenStream, (nodes, children) =>
        {
            var node = nodes.FirstOrDefault(n => n.Path == hubPath);
            return BuildHistoryView(host, node, hubPath, children ?? Array.Empty<MeshNode>());
        });
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
    /// Shows title, last activity time, and message preview.
    /// Queries child ThreadMessage nodes for message count and preview.
    /// </summary>
    public static IObservable<UiControl?> Thumbnail(LayoutAreaHost host, RenderingContext _)
    {
        var hubPath = host.Hub.Address.ToString();
        var meshQuery = host.Hub.ServiceProvider.GetService<IMeshService>();

        var nodeStream = host.Workspace.GetStream<MeshNode>()?.Select(nodes => nodes ?? Array.Empty<MeshNode>())
            ?? Observable.Return<MeshNode[]>(Array.Empty<MeshNode>());

        // Query for child ThreadMessage nodes
        var messagesStream = Observable.FromAsync(async () =>
        {
            if (meshQuery == null)
                return Array.Empty<MeshNode>() as IReadOnlyList<MeshNode>;

            try
            {
                return await meshQuery.QueryAsync<MeshNode>(
                    $"namespace:{hubPath} nodeType:{ThreadMessageNodeType.NodeType} sort:Timestamp-asc"
                ).ToListAsync() as IReadOnlyList<MeshNode>;
            }
            catch
            {
                return Array.Empty<MeshNode>() as IReadOnlyList<MeshNode>;
            }
        });

        return nodeStream.CombineLatest(messagesStream, (nodes, messageNodes) =>
        {
            var node = nodes.FirstOrDefault(n => n.Path == hubPath);
            return BuildThumbnail(node, hubPath, messageNodes ?? Array.Empty<MeshNode>());
        });
    }

    private static UiControl BuildThumbnail(MeshNode? node, string hubPath, IReadOnlyList<MeshNode> messageNodes)
    {
        var content = node?.Content as MeshThread;
        var title = node?.Name ?? "Thread";
        var lastActivity = node?.LastModified.ToString("g") ?? "";

        // Extract messages from child nodes
        var messages = messageNodes
            .Select(n => n.Content as ThreadMessage)
            .Where(m => m != null && m.Type != ThreadMessageType.EditingPrompt)
            .OrderBy(m => m!.Timestamp)
            .ToList();

        // Fall back to legacy inline messages
        var messageCount = messages.Count;

        // Get preview from last message
        var preview = "";
        var lastMessage = messages.LastOrDefault();
        if (lastMessage != null)
        {
            preview = lastMessage.Text.Length > 60
                ? lastMessage.Text[..57] + "..."
                : lastMessage.Text;
        }

        return Controls.Stack
            .WithStyle("padding: 16px; background: var(--neutral-layer-card-container); border: 1px solid var(--neutral-stroke-rest); border-radius: 8px;")
            .WithView(Controls.Stack
                .WithOrientation(Orientation.Horizontal)
                .WithStyle("align-items: center; gap: 12px; margin-bottom: 8px;")
                .WithView(Controls.Icon(FluentIcons.Chat(IconSize.Size24)).WithStyle("color: var(--accent-fill-rest);"))
                .WithView(Controls.Stack
                    .WithView(Controls.Html($"<strong style=\"display: block;\">{System.Web.HttpUtility.HtmlEncode(title)}</strong>"))
                    .WithView(Controls.Html($"<span style=\"font-size: 0.85rem; color: var(--neutral-foreground-hint);\">{lastActivity}</span>"))))
            .WithView(!string.IsNullOrEmpty(preview)
                ? Controls.Html($"<p style=\"margin: 8px 0 0 0; font-size: 0.9rem; color: var(--neutral-foreground-hint); overflow: hidden; text-overflow: ellipsis; white-space: nowrap;\">{System.Web.HttpUtility.HtmlEncode(preview)}</p>")
                : Controls.Html($"<p style=\"margin: 8px 0 0 0; font-size: 0.9rem; color: var(--neutral-foreground-hint);\">{messageCount} messages</p>"))
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
            .WithHiddenQuery($"nodeType:Thread namespace:{nodePath}/{ThreadNodeType.ThreadPartition}")
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
            .Select(change => (change.Value?.Content as MeshThread)?.Messages ?? ImmutableList<string>.Empty)
            .Where(ids => ids.Count > 0)
            .Select(ids => (ids, key: string.Join("|", ids)))
            .DistinctUntilChanged(p => p.key)
            .Select(p => CollectUpdatedNodes(host.Hub, threadPath, p.ids))
            .Switch()
            .Select(updates => BuildHeader(parentLink, updates, threadPath));

        return initial.Concat(aggregated);
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
                hub.RegisterCallback((IMessageDelivery)del, resp =>
                {
                    var msg = resp is IMessageDelivery<GetDataResponse> gdr
                        ? (gdr.Message.Data as MeshNode)?.Content as ThreadMessage
                        : null;
                    subject.OnNext(msg?.UpdatedNodes ?? ImmutableList<NodeChangeEntry>.Empty);
                    subject.OnCompleted();
                    return resp;
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
}
