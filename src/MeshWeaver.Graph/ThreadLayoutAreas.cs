using System.Reactive.Linq;
using System.Text;
using System.Text.Json;
using MeshWeaver.Application.Styles;
using MeshWeaver.Data;
using MeshWeaver.Domain;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using MeshWeaver.AI;
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
    /// </summary>
    public static MessageHubConfiguration AddThreadViews(this MessageHubConfiguration configuration)
        => configuration
            .WithHandler<ExecuteThreadMessageRequest>(HandleExecuteThreadMessage)
            .AddNodeMenuItems("SidePanel", SidePanelMenuProvider)
            .AddNodeMenuItems(DelegationsMenuProvider)
            .AddLayout(layout => layout
                .WithDefaultArea(ThreadNodeType.ThreadArea)
                .WithView(ThreadNodeType.ThreadArea, ThreadView)
                .WithView(ThreadNodeType.HistoryArea, HistoryView)
                .WithView(MeshNodeLayoutAreas.CreateNodeArea, CreateView)
                .WithView(MeshNodeLayoutAreas.SettingsArea, SettingsLayoutArea.Settings)
                .WithView(MeshNodeLayoutAreas.MetadataArea, MeshNodeLayoutAreas.Metadata)
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
        yield return new("Delegations", ThreadNodeType.HistoryArea, Order: 12);
    }

    private static string GetContextDisplayName(string path)
    {
        var segments = path.Split('/');
        return segments.Length > 0 ? segments[^1] : path;
    }

    /// <summary>
    /// Renders the Create area for Thread nodes.
    /// Confirms the transient node and redirects to the default area.
    /// </summary>
    public static IObservable<UiControl?> CreateView(LayoutAreaHost host, RenderingContext _)
    {
        var currentPath = host.Hub.Address.ToString();

        return Observable.FromAsync(async () =>
        {
            var meshCatalog = host.Hub.ServiceProvider.GetRequiredService<IMeshCatalog>();
            var existingNode = await meshCatalog.GetNodeAsync(new Address(currentPath));

            if (existingNode == null)
            {
                return (UiControl?)Controls.Html(
                    "<p style=\"color: var(--error-foreground); padding: 24px;\">Thread node not found.</p>");
            }

            // Ensure ParentPath is set on the thread content
            var content = existingNode.Content as MeshThread ?? new MeshThread();
            var parentPath = existingNode.GetParentPath();
            if (string.IsNullOrEmpty(content.ParentPath) && !string.IsNullOrEmpty(parentPath))
            {
                content = content with { ParentPath = parentPath };
            }

            var confirmedNode = existingNode with
            {
                Content = content,
                State = MeshNodeState.Active
            };

            try
            {
                var createdNode = await meshCatalog.CreateNodeAsync(confirmedNode).ConfigureAwait(false);
                return (UiControl?)new RedirectControl(MeshNodeLayoutAreas.BuildContentUrl(createdNode.Path!, ThreadNodeType.ThreadArea));
            }
            catch (Exception ex)
            {
                return (UiControl?)Controls.Html($"<p style=\"color: var(--error-foreground);\">Failed to create thread: {ex.Message}</p>");
            }
        });
    }

    /// <summary>
    /// Overrides the default Threads catalog view to add a "Create Thread" button.
    /// Injected via AddThreadViews configuration.
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
                .WithHiddenQuery($"path:{hubPath} scope:children nodeType:{ThreadNodeType.NodeType}")
                .WithPlaceholder("Search threads...")
                .WithRenderMode(MeshSearchRenderMode.Flat)
                .WithMaxColumns(3));
    }

    /// <summary>
    /// Renders the Thread area — the default view for threads.
    /// Shows the thread title (observable, bound to meshNode.Name) and a
    /// ThreadChatControl that handles both message display and chat input.
    /// </summary>
    public static IObservable<UiControl?> ThreadView(LayoutAreaHost host, RenderingContext _)
    {
        var hubPath = host.Hub.Address.ToString();

        // Node stream — drives the observable title and chat control context
        var nodeStream = host.Workspace.GetStream<MeshNode>()?.Select(nodes => nodes ?? Array.Empty<MeshNode>())
            ?? Observable.Return<MeshNode[]>(Array.Empty<MeshNode>());

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

        // 2. ThreadChatControl — observable for context resolution, handles messages + input
        container = container.WithView(nodeStream.Select(nodes =>
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
        var meshQuery = host.Hub.ServiceProvider.GetService<IMeshQuery>();

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
                return await meshQuery.QueryAsync<MeshNode>($"path:{hubPath} nodeType:{ThreadNodeType.NodeType} scope:children").ToListAsync() as IReadOnlyList<MeshNode>;
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

    private static UiControl BuildHistoryView(LayoutAreaHost host, MeshNode? node, string threadPath, IReadOnlyList<MeshNode> delegations)
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
        var meshQuery = host.Hub.ServiceProvider.GetService<IMeshQuery>();

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
                    $"path:{hubPath} nodeType:{ThreadMessageNodeType.NodeType} scope:children sort:Timestamp-asc"
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
    /// Computes the next message number by querying existing ThreadMessage children.
    /// </summary>
    private static async Task<int> ComputeNextMessageNumberAsync(IMessageHub hub, string threadPath)
    {
        var meshQuery = hub.ServiceProvider.GetService<IMeshQuery>();
        if (meshQuery == null)
            return 1;

        var messageNodes = await meshQuery.QueryAsync<MeshNode>(
            $"path:{threadPath} nodeType:{ThreadMessageNodeType.NodeType} scope:children"
        ).ToListAsync();

        if (messageNodes.Count == 0)
            return 1;

        return messageNodes
            .Select(n => n.Path?.Split('/').LastOrDefault())
            .Where(id => id != null && int.TryParse(id, out _))
            .Select(id => int.Parse(id!))
            .DefaultIfEmpty(0)
            .Max() + 1;
    }

    /// <summary>
    /// Creates a ThreadMessage child node under the thread.
    /// </summary>
    private static async Task<string> CreateMessageNodeAsync(
        IMessageHub hub, string threadPath, int messageNumber, ThreadMessage message)
    {
        var meshCatalog = hub.ServiceProvider.GetRequiredService<IMeshCatalog>();
        var messagePath = $"{threadPath}/{messageNumber}";

        var messageNode = new MeshNode(messagePath)
        {
            NodeType = ThreadMessageNodeType.NodeType,
            Order = messageNumber,
            Content = message
        };

        var accessService = hub.ServiceProvider.GetService<AccessService>();
        var userId = accessService?.Context?.ObjectId;
        await meshCatalog.CreateNodeAsync(messageNode, userId);
        return messagePath;
    }

    /// <summary>
    /// Handles ExecuteThreadMessageRequest by creating both user and response nodes,
    /// running the agent on the hub side, and streaming updates to the response node.
    /// This decouples agent execution from the GUI component lifecycle.
    /// </summary>
    private static async Task<IMessageDelivery> HandleExecuteThreadMessage(
        IMessageHub hub,
        IMessageDelivery<ExecuteThreadMessageRequest> delivery,
        CancellationToken ct)
    {
        var request = delivery.Message;
        var logger = hub.ServiceProvider.GetRequiredService<ILogger<AgentChatClient>>();

        try
        {
            // 1. Compute next message number from existing children
            var nextNumber = await ComputeNextMessageNumberAsync(hub, request.ThreadPath);

            // 2. Create user message node
            var userMessage = new ThreadMessage
            {
                Id = nextNumber.ToString(),
                Role = "user",
                Text = request.UserMessageText,
                Timestamp = DateTime.UtcNow,
                Type = ThreadMessageType.ExecutedInput
            };
            await CreateMessageNodeAsync(hub, request.ThreadPath, nextNumber, userMessage);

            // 3. Create empty response node
            var responseNumber = nextNumber + 1;
            var responseMessage = new ThreadMessage
            {
                Id = responseNumber.ToString(),
                Role = "assistant",
                Text = "",
                Timestamp = DateTime.UtcNow,
                Type = ThreadMessageType.AgentResponse
            };
            var responsePath = await CreateMessageNodeAsync(hub, request.ThreadPath, responseNumber, responseMessage);

            // 4. Initialize agent chat client
            var chatClient = new AgentChatClient(hub.ServiceProvider);
            chatClient.SetThreadId(request.ThreadPath);
            await chatClient.InitializeAsync(request.ContextPath, request.ModelName);

            if (!string.IsNullOrEmpty(request.AgentName))
                chatClient.SetSelectedAgent(request.AgentName);
            if (request.Attachments is { Count: > 0 })
                chatClient.SetAttachments(request.Attachments);

            // 5. Load persistent thread ID from thread content if present
            var meshCatalog = hub.ServiceProvider.GetService<IMeshCatalog>();
            if (meshCatalog != null)
            {
                var threadNode = await meshCatalog.GetNodeAsync(new Address(request.ThreadPath));
                if (threadNode?.Content is MeshThread threadContent
                    && !string.IsNullOrEmpty(threadContent.PersistentThreadId))
                {
                    chatClient.SetPersistentThreadId(threadContent.PersistentThreadId);
                }
            }

            // 6. Stream response, throttle-update response node every 200ms
            var chatMessage = new ChatMessage(ChatRole.User, request.UserMessageText);
            var responseText = new StringBuilder();
            var lastUpdate = DateTimeOffset.MinValue;

            await foreach (var update in chatClient.GetStreamingResponseAsync([chatMessage], ct))
            {
                if (!string.IsNullOrEmpty(update.Text))
                {
                    responseText.Append(update.Text);

                    if (DateTimeOffset.UtcNow - lastUpdate > TimeSpan.FromMilliseconds(200))
                    {
                        UpdateResponseNode(hub, responsePath, responseText.ToString());
                        lastUpdate = DateTimeOffset.UtcNow;
                    }
                }
            }

            // 7. Final update with complete text + agent/model info + touch thread LastModified
            UpdateResponseNode(hub, responsePath, responseText.ToString(), request.AgentName, request.ModelName);
            await TouchThreadLastModifiedAsync(hub, request.ThreadPath);

            hub.Post(new ExecuteThreadMessageResponse { Success = true },
                o => o.ResponseFor(delivery));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error handling ExecuteThreadMessageRequest for thread {ThreadPath}", request.ThreadPath);

            hub.Post(new ExecuteThreadMessageResponse { Success = false, Error = ex.Message },
                o => o.ResponseFor(delivery));
        }

        return delivery.Processed();
    }

    private static void UpdateResponseNode(IMessageHub hub, string responsePath, string text, string? agentName = null, string? modelName = null)
    {
        var nodeId = responsePath.Split('/').Last();
        var updatedMessage = new ThreadMessage
        {
            Id = nodeId,
            Role = "assistant",
            Text = text,
            Timestamp = DateTime.UtcNow,
            Type = ThreadMessageType.AgentResponse,
            AgentName = agentName,
            ModelName = modelName
        };
        var node = new MeshNode(responsePath)
        {
            NodeType = ThreadMessageNodeType.NodeType,
            Content = updatedMessage
        };
        var nodeJson = JsonSerializer.SerializeToElement(node, hub.JsonSerializerOptions);
        hub.Post(new DataChangeRequest { Updates = [nodeJson] },
            o => o.WithTarget(new Address(responsePath)));
    }

    private static async Task TouchThreadLastModifiedAsync(IMessageHub hub, string threadPath)
    {
        try
        {
            var meshCatalog = hub.ServiceProvider.GetService<IMeshCatalog>();
            var existingNode = meshCatalog != null
                ? await meshCatalog.GetNodeAsync(new Address(threadPath))
                : null;

            if (existingNode != null)
            {
                var updatedNode = existingNode with { LastModified = DateTime.UtcNow };
                var nodeJson = JsonSerializer.SerializeToElement(updatedNode, hub.JsonSerializerOptions);
                hub.Post(new DataChangeRequest { Updates = [nodeJson] },
                    o => o.WithTarget(new Address(threadPath)));
            }
        }
        catch
        {
            // Best effort — don't fail the whole request for a timestamp update
        }
    }

    /// <summary>
    /// Gets the thread title from node name or falls back to default.
    /// </summary>
    private static string GetThreadTitle(MeshNode? node)
        => !string.IsNullOrEmpty(node?.Name) ? node.Name : "Thread";
}
