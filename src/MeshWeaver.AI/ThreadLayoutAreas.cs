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
            .WithHandler<ResubmitMessageRequest>(HandleResubmitMessage)
            .WithHandler<DeleteFromMessageRequest>(HandleDeleteFromMessage)
            .WithHandler<EditMessageRequest>(HandleEditMessage)
            .AddDefaultMeshMenu()
            .AddNodeMenuItems("SidePanel", SidePanelMenuProvider)
            .AddNodeMenuItems(DelegationsMenuProvider)
            .AddLayout(layout => layout
                .WithDefaultArea(ThreadNodeType.ThreadArea)
                .WithView(ThreadNodeType.ThreadArea, ThreadView)
                .WithView(ThreadNodeType.ThreadChatArea, ThreadChatView)
                .WithView(ThreadNodeType.StreamingArea, StreamingView)
                .WithView(ThreadNodeType.HistoryArea, HistoryView)
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
    /// Handles DeleteFromMessageRequest — truncates Messages from the given message onwards.
    /// Uses workspace.UpdateMeshNode (non-blocking, no stream subscription).
    /// </summary>
    private static IMessageDelivery HandleDeleteFromMessage(
        IMessageHub hub,
        IMessageDelivery<DeleteFromMessageRequest> delivery)
    {
        var request = delivery.Message;
        // Safe: handler runs on grain scheduler, workspace.UpdateMeshNode is inline.
        hub.GetWorkspace().UpdateMeshNode(node =>
        {
            var thread = node.Content as MeshThread ?? new MeshThread();
            var msgIndex = thread.Messages.IndexOf(request.MessageId);
            if (msgIndex < 0) return node;
            return node with
            {
                Content = thread with { Messages = thread.Messages.Take(msgIndex).ToImmutableList() }
            };
        });
        return delivery.Processed();
    }

    /// <summary>
    /// Handles ResubmitMessageRequest — keeps the user message, removes the response
    /// (and anything after it), then creates only a new output cell and starts execution.
    /// Uses meshService.CreateNode (observable) + workspace.UpdateMeshNode (non-blocking).
    /// No stream subscription — avoids hub scheduler deadlock.
    /// </summary>
    private static IMessageDelivery HandleResubmitMessage(
        IMessageHub hub,
        IMessageDelivery<ResubmitMessageRequest> delivery)
    {
        var request = delivery.Message;
        var logger = hub.ServiceProvider.GetRequiredService<ILogger<AgentChatClient>>();
        var meshService = hub.ServiceProvider.GetRequiredService<IMeshService>();

        var responseMsgId = Guid.NewGuid().ToString("N")[..8];
        var responsePath = $"{request.ThreadPath}/{responseMsgId}";

        // Update user message text if changed (Post to the message hub — non-blocking)
        hub.Post(new UpdateThreadMessageContent { Text = request.UserMessageText },
            o => o.WithTarget(new Address($"{request.ThreadPath}/{request.MessageId}")));

        // Create response cell — fire and forget (no callback needed)
        meshService.CreateNode(new MeshNode(responseMsgId, request.ThreadPath)
        {
            NodeType = ThreadMessageNodeType.NodeType,
            Content = new ThreadMessage
            {
                Role = "assistant",
                Text = "",
                Timestamp = DateTime.UtcNow,
                Type = ThreadMessageType.AgentResponse
            }
        }).Subscribe(
            _ => { },
            ex => logger.LogError(ex, "HandleResubmitMessage: CreateNode failed for {ThreadPath}", request.ThreadPath));

        // Update thread state — runs on grain scheduler (handler body), safe.
        // Don't wait for CreateNode: execution will find the node by the time it streams.
        hub.GetWorkspace().UpdateMeshNode(node =>
        {
            var thread = node.Content as MeshThread ?? new MeshThread();
            var msgIndex = thread.Messages.IndexOf(request.MessageId);
            if (msgIndex < 0) return node;

            return node with
            {
                Content = thread with
                {
                    Messages = thread.Messages.Take(msgIndex + 1).ToImmutableList().Add(responseMsgId),
                    IsExecuting = true,
                    ActiveMessageId = responseMsgId,
                    ExecutionStatus = null,
                    TokensUsed = 0,
                    ExecutionStartedAt = DateTime.UtcNow,
                    StreamingText = null,
                    StreamingToolCalls = null
                }
            };
        });

        // Start execution on hosted hub
        var executionHub = hub.GetHostedHub(
            new Address($"{hub.Address}/_Exec"),
            config => config.WithHandler<SubmitMessageRequest>(ThreadExecution.ExecuteMessageAsync),
            HostedHubCreation.Always);

        executionHub!.Post(new SubmitMessageRequest
        {
            ThreadPath = request.ThreadPath,
            UserMessageText = request.UserMessageText,
            ResponsePath = responsePath,
            ContextPath = request.ThreadPath
        }, o => delivery.AccessContext != null ? o.WithAccessContext(delivery.AccessContext) : o);

        return delivery.Processed();
    }

    /// <summary>
    /// Handles EditMessageRequest — no-op on the server side.
    /// Editing is handled purely in the UI: the Overview layout area toggles between
    /// message view and editor view via a data section flag.
    /// </summary>
    private static IMessageDelivery HandleEditMessage(
        IMessageHub hub,
        IMessageDelivery<EditMessageRequest> delivery)
        => delivery.Processed();
}
