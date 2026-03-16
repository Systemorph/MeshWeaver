using System.Reactive.Linq;
using System.Text;
using MeshWeaver.AI;
using MeshWeaver.Application.Styles;
using MeshWeaver.Data;
using MeshWeaver.Domain;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MeshThread = MeshWeaver.AI.Thread;

namespace MeshWeaver.Graph;

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
    /// Registers ThreadSession as a per-hub singleton for persistent agent chat state.
    /// </summary>
    public static MessageHubConfiguration AddThreadLayoutAreas(this MessageHubConfiguration configuration)
        => configuration
            .WithServices(services =>
            {
                services.AddSingleton<ThreadSession>();
                return services;
            })
            .WithHandler<SubmitMessageRequest>(HandleSubmitMessage)
            .WithHandler<CancelThreadStreamRequest>(HandleCancelStream)
            .AddDefaultMeshMenu()
            .AddNodeMenuItems("SidePanel", SidePanelMenuProvider)
            .AddNodeMenuItems(DelegationsMenuProvider)
            .AddLayout(layout => layout
                .WithDefaultArea(ThreadNodeType.ThreadArea)
                .WithView(ThreadNodeType.ThreadArea, ThreadView)
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
            Href: MeshNodeLayoutAreas.BuildContentUrl(hubPath, ThreadNodeType.HistoryArea));
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
                .WithHiddenQuery($"namespace:{hubPath} nodeType:{ThreadNodeType.NodeType}")
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
    public static IObservable<UiControl?> ThreadView(LayoutAreaHost host, RenderingContext _)
    {
        var hubPath = host.Hub.Address.ToString();

        // Node stream — drives the observable title and chat control context
        var stream = host.Workspace.GetStream<MeshNode>();
        var nodeStream = stream!.Select(nodes => nodes ?? Array.Empty<MeshNode>());

        // Push Thread content to data section — Blazor view reads ThreadMessages from it.
        var threadStream = nodeStream.Select(nodes =>
        {
            var node = nodes.FirstOrDefault(n => n.Path == hubPath);
            return node?.Content as MeshThread;
        });
        host.RegisterForDisposal(ThreadNodeType.ThreadArea,
            threadStream.Subscribe(thread => host.UpdateData(ThreadDataKey, thread)));

        // Static container — emits once, not rebuilt on every node update
        var container = Controls.Stack
            .WithWidth("100%")
            .WithHeight("100%")
            .WithStyle("display: flex; flex-direction: column;");

        // 1. Title — observable sub-view bound to meshNode.Name
        container = container.WithView(nodeStream.Select(nodes =>
        {
            var node = nodes.FirstOrDefault(n => n.Path == hubPath);
            var title = GetThreadTitle(node);
            return (UiControl?)Controls.Html(
                $"<h2 style=\"margin: 0; padding: 12px 16px; border-bottom: 1px solid var(--neutral-stroke-rest); flex-shrink: 0;\">{System.Web.HttpUtility.HtmlEncode(title)}</h2>");
        }));

        // 2. ThreadChatControl — emitted once with context from first node emission.
        // Thread content is data-bound via JsonPointerReference to ThreadDataKey.
        container = container.WithView(nodeStream.Take(1).Select(nodes =>
        {
            var node = nodes.FirstOrDefault(n => n.Path == hubPath);
            var threadContent = node?.Content as MeshThread;

            var contextPath = !string.IsNullOrEmpty(threadContent?.ParentPath)
                ? threadContent.ParentPath
                : hubPath;
            var contextDisplayName = !string.IsNullOrEmpty(threadContent?.ParentPath)
                ? GetContextDisplayName(threadContent.ParentPath)
                : GetThreadTitle(node);

            return (UiControl?)new ThreadChatControl()
                .WithThreadPath(hubPath)
                .WithInitialContext(contextPath)
                .WithInitialContextDisplayName(contextDisplayName)
                .WithThreadViewModel(new JsonPointerReference(LayoutAreaReference.GetDataPointer(ThreadDataKey, "threadMessages")))
                .WithStyle("flex: 1; overflow: hidden;");
        }));

        return Observable.Return<UiControl?>(container);
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
#pragma warning disable CS0618 // Type or member is obsolete
        if (messages.Count == 0 && content?.Messages?.Count > 0)
        {
            messages = content.Messages
                .Where(m => m.Type != ThreadMessageType.EditingPrompt)
                .ToList()!;
        }
#pragma warning restore CS0618

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
    /// Handles SubmitMessageRequest in the Thread hub's execution block.
    /// Creates message nodes and updates Thread.MessagePaths (triggers GUI update),
    /// then delegates streaming to the _Exec sub-hub which has its own execution queue.
    /// No Task.Run — all operations use hub.Post (fire-and-forget) to avoid deadlocks.
    /// </summary>
    private static IMessageDelivery HandleSubmitMessage(
        IMessageHub hub,
        IMessageDelivery<SubmitMessageRequest> delivery)
    {
        var request = delivery.Message;
        var threadPath = request.ThreadPath;
        var logger = hub.ServiceProvider.GetRequiredService<ILogger<ThreadSession>>();
        logger.LogInformation("HandleSubmitMessage: threadPath={ThreadPath}", threadPath);

        // Compute next message number from Thread.ThreadMessages
        var workspace = hub.ServiceProvider.GetRequiredService<IWorkspace>();
        var nodes = workspace.GetStream<MeshNode>()?.FirstAsync().GetAwaiter().GetResult();
        var threadNode = nodes?.FirstOrDefault(n => n.Path == threadPath);
        var threadContent = threadNode?.Content as MeshThread ?? new MeshThread();
        var nextNumber = threadContent.ThreadMessages.Count + 1;
        var responseNumber = nextNumber + 1;
        var userMsgId = Guid.NewGuid().ToString("N")[..8];
        var responseMsgId = Guid.NewGuid().ToString("N")[..8];

        var userNodePath = $"{threadPath}/{userMsgId}";
        var responsePath = $"{threadPath}/{responseMsgId}";

        // 1. Create message nodes via IMeshService.CreateNodeAsync (no await, ContinueWith for errors)
        logger.LogInformation("HandleSubmitMessage: creating nodes userMsgId={UserMsgId}, responseMsgId={ResponseMsgId}", userMsgId, responseMsgId);
        var meshService = hub.ServiceProvider.GetRequiredService<IMeshService>();

        var userNode = new MeshNode(userMsgId, threadPath)
        {
            NodeType = ThreadMessageNodeType.NodeType,
            Order = nextNumber,
            Content = new ThreadMessage
            {
                Id = userMsgId,
                Role = "user",
                Text = request.UserMessageText,
                Timestamp = DateTime.UtcNow,
                Type = ThreadMessageType.ExecutedInput
            }
        };
        meshService.CreateNodeAsync(userNode).ContinueWith(t =>
        {
            if (t.IsFaulted)
                logger.LogError(t.Exception, "Failed to create user message node at {Path}", userNodePath);
        }, TaskContinuationOptions.OnlyOnFaulted);

        var responseNode = new MeshNode(responseMsgId, threadPath)
        {
            NodeType = ThreadMessageNodeType.NodeType,
            Order = responseNumber,
            Content = new ThreadMessage
            {
                Id = responseMsgId,
                Role = "assistant",
                Text = "",
                Timestamp = DateTime.UtcNow,
                Type = ThreadMessageType.AgentResponse
            }
        };
        meshService.CreateNodeAsync(responseNode).ContinueWith(t =>
        {
            if (t.IsFaulted)
                logger.LogError(t.Exception, "Failed to create response node at {Path}", responsePath);
        }, TaskContinuationOptions.OnlyOnFaulted);

        // 2. Update Thread.ThreadMessages on the MeshNode — DataChangeRequest to own hub
        if (threadNode != null)
        {
            var updatedContent = threadContent with
            {
                ThreadMessages = threadContent.ThreadMessages
                    .Concat([userMsgId, responseMsgId]).ToList()
            };
            var updatedNode = threadNode with { Content = updatedContent };
            hub.Post(new DataChangeRequest { Updates = [updatedNode] });
        }

        // 3. Send response immediately
        hub.Post(new SubmitMessageResponse { Success = true }, o => o.ResponseFor(delivery));

        // 4. Delegate streaming to _Exec sub-hub (has its own execution queue).
        // The sub-hub owns the ThreadSession and can safely await streaming.
        // Updates are posted to thread message node addresses via hub routing.
        var session = hub.ServiceProvider.GetRequiredService<ThreadSession>();
        var execHub = hub.GetHostedHub(
            new Address($"{threadPath}/_Exec"),
            config => config
                .WithServices(services =>
                {
                    services.AddSingleton(session);
                    return services;
                })
                .WithHandler<StartStreamingRequest>(HandleExecStreaming),
            HostedHubCreation.Always);

        execHub!.Post(new StartStreamingRequest
        {
            ThreadPath = threadPath,
            UserMessageText = request.UserMessageText,
            UserMessagePath = userNodePath,
            ResponsePath = responsePath,
            ResponseOrder = responseNumber
        });

        return delivery.Processed();
    }

    /// <summary>
    /// Async handler running in the _Exec sub-hub's execution queue.
    /// Can safely await streaming without blocking the Thread hub.
    /// Updates are posted to the thread message node address (routed through hub hierarchy).
    /// </summary>
    private static async Task<IMessageDelivery> HandleExecStreaming(
        IMessageHub hub,
        IMessageDelivery<StartStreamingRequest> delivery,
        CancellationToken ct)
    {
        var session = hub.ServiceProvider.GetRequiredService<ThreadSession>();
        var request = delivery.Message;
        var logger = hub.ServiceProvider.GetRequiredService<ILogger<ThreadSession>>();

        try
        {
            // Initialize agent
            await session.EnsureInitializedAsync(request.ThreadPath, null, null, null);

            // Stream and post throttled updates to the response message node address
            var responseText = new StringBuilder();
            var lastUpdate = DateTimeOffset.MinValue;
            var chatMessage = new ChatMessage(ChatRole.User, request.UserMessageText);

            await foreach (var update in session.GetStreamingResponseAsync([chatMessage], ct))
            {
                if (!string.IsNullOrEmpty(update.Text))
                {
                    responseText.Append(update.Text);

                    if (DateTimeOffset.UtcNow - lastUpdate > TimeSpan.FromMilliseconds(200))
                    {
                        PostResponseUpdate(hub, request.ThreadPath, request.ResponsePath, responseText.ToString());
                        lastUpdate = DateTimeOffset.UtcNow;
                    }
                }
            }

            // Final update with complete text
            PostResponseUpdate(hub, request.ThreadPath, request.ResponsePath, responseText.ToString());
        }
        catch (OperationCanceledException)
        {
            logger.LogInformation("Stream cancelled for thread {ThreadPath}", request.ThreadPath);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error streaming for thread {ThreadPath}", request.ThreadPath);
        }

        return delivery.Processed();
    }

    /// <summary>
    /// Posts a throttled response text update to the ThreadMessage node hub.
    /// Routes through the hub hierarchy to reach the correct ThreadMessage hub.
    /// </summary>
    private static void PostResponseUpdate(IMessageHub hub, string threadPath, string responsePath, string text)
    {
        var nodeId = responsePath.Split('/').Last();
        var updatedMessage = new ThreadMessage
        {
            Id = nodeId,
            Role = "assistant",
            Text = text,
            Timestamp = DateTime.UtcNow,
            Type = ThreadMessageType.AgentResponse
        };
        hub.Post(new DataChangeRequest { Updates = [updatedMessage] },
            o => o.WithTarget(new Address(responsePath)));
    }

    /// <summary>
    /// Handles CancelThreadStreamRequest — cancels the _Exec sub-hub's execution token,
    /// which breaks the streaming loop in HandleExecStreaming.
    /// </summary>
    private static IMessageDelivery HandleCancelStream(
        IMessageHub hub, IMessageDelivery<CancelThreadStreamRequest> delivery)
    {
        var threadPath = delivery.Message.ThreadPath;
        var execHub = hub.GetHostedHub(new Address($"{threadPath}/_Exec"), HostedHubCreation.Never);
        execHub?.CancelCurrentExecution();
        return delivery.Processed();
    }

    /// <summary>
    /// Gets the thread title from node name or falls back to default.
    /// </summary>
    private static string GetThreadTitle(MeshNode? node)
        => !string.IsNullOrEmpty(node?.Name) ? node.Name : "Thread";
}
