using System.Reactive.Linq;
using System.Text;
using MeshWeaver.Application.Styles;
using MeshWeaver.Data;
using MeshWeaver.Data.Serialization;
using MeshWeaver.Domain;
using MeshWeaver.Mesh;
using MeshWeaver.Graph;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
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
            .WithHandler<SubmitMessageRequest>(HandleSubmitMessage)
            .WithHandler<CancelThreadStreamRequest>(HandleCancelStream)
            .WithHandler<ResubmitMessageRequest>(HandleResubmitMessage)
            .WithHandler<DeleteFromMessageRequest>(HandleDeleteFromMessage)
            .WithHandler<EditMessageRequest>(HandleEditMessage)
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
    public static UiControl? ThreadView(LayoutAreaHost host, RenderingContext _)
    {
        var hubPath = host.Hub.Address.ToString();

        // Node stream — drives the observable title and chat control context
        var stream = host.Workspace.GetStream<MeshNode>();

        // Push ThreadViewModel to data section — contains all thread state for the Blazor view.
        // Wrapped in an object (not raw array) so GetStream<object> can deserialize it.
        var vmStream = stream!.Select(nodes =>
        {
            var node = nodes!.First(n => n.Path == hubPath);
            var threadContent = node?.Content as MeshThread;
            var contextPath = !string.IsNullOrEmpty(threadContent?.ParentPath)
                ? threadContent.ParentPath : hubPath;
            var contextDisplayName = !string.IsNullOrEmpty(threadContent?.ParentPath)
                ? GetContextDisplayName(threadContent.ParentPath) : GetThreadTitle(node);
            return new ThreadViewModel
            {
                Messages = threadContent?.ThreadMessages ?? [],
                ThreadPath = hubPath,
                InitialContext = contextPath,
                InitialContextDisplayName = contextDisplayName
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

        // Static container — never rebuilt
        return Controls.Stack
            .WithWidth("100%")
            .WithHeight("100%")
            .WithStyle("display: flex; flex-direction: column;")
            // 1. Title — data-bound, not observable control rebuild
            .WithView(Controls.Html(new JsonPointerReference(LayoutAreaReference.GetDataPointer("title")))
                .WithStyle("margin: 0; padding: 12px 16px; border-bottom: 1px solid var(--neutral-stroke-rest); flex-shrink: 0; font-size: 1.5rem; font-weight: bold;"))
            // 2. ThreadChatControl — all state data-bound via ThreadViewModel
            .WithView(new ThreadChatControl()
                .WithThreadViewModel(new JsonPointerReference(LayoutAreaReference.GetDataPointer(ThreadDataKey)))
                .WithStyle("flex: 1; overflow: hidden;"));
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
    /// Handles SubmitMessageRequest on the thread hub.
    /// 1) Create input cell with ContinueWith
    /// 2) Create output cell with ContinueWith
    /// 3) Update Thread.ThreadMessages as creations succeed
    /// 4) Start execution on hosted hub
    /// </summary>
    private static IMessageDelivery HandleSubmitMessage(
        IMessageHub hub,
        IMessageDelivery<SubmitMessageRequest> delivery)
    {
        var request = delivery.Message;
        var threadPath = request.ThreadPath;
        var meshService = hub.ServiceProvider.GetRequiredService<IMeshService>();

        var userMsgId = Guid.NewGuid().ToString("N")[..8];
        var responseMsgId = Guid.NewGuid().ToString("N")[..8];
        var responsePath = $"{threadPath}/{responseMsgId}";

        // 1+2) Create both cells concurrently
        var inputTask = meshService.CreateNodeAsync(new MeshNode(userMsgId, threadPath)
        {
            NodeType = ThreadMessageNodeType.NodeType,
            Content = new ThreadMessage
            {
                Id = userMsgId,
                Role = "user",
                Text = request.UserMessageText,
                Timestamp = DateTime.UtcNow,
                Type = ThreadMessageType.ExecutedInput
            }
        });

        var outputTask = meshService.CreateNodeAsync(new MeshNode(responseMsgId, threadPath)
        {
            NodeType = ThreadMessageNodeType.NodeType,
            Content = new ThreadMessage
            {
                Id = responseMsgId,
                Role = "assistant",
                Text = "",
                Timestamp = DateTime.UtcNow,
                Type = ThreadMessageType.AgentResponse
            }
        });

        Task.WhenAll(inputTask, outputTask).ContinueWith(_ =>
        {
            // 3) Update Thread.ThreadMessages (data-bound to ThreadViewModel)
            hub.InvokeAsync(() =>
            {
                hub.ServiceProvider.GetRequiredService<IWorkspace>()
                    .GetStream<MeshNode>()?.Take(1).Subscribe(nodes =>
                {
                    var threadNode = nodes?.FirstOrDefault(n => n.Path == threadPath);
                    if (threadNode == null) return;
                    var currentContent = threadNode.Content as MeshThread ?? new MeshThread();
                    var updatedMsgList = currentContent.ThreadMessages
                        .Concat([userMsgId, responseMsgId]).ToList();
                    hub.Post(new DataChangeRequest
                    {
                        Updates = [threadNode with
                        {
                            Content = currentContent with { ThreadMessages = updatedMsgList }
                        }]
                    });
                });
            });

            // 4) Get remote stream for response node and start execution
            var responseStream = hub.ServiceProvider.GetRequiredService<IWorkspace>()
                .GetRemoteStream<EntityStore, CollectionsReference>(
                    new Address(responsePath),
                    new CollectionsReference(nameof(MeshNode)));

            var executionHub = hub.GetHostedHub(
                new Address($"{hub.Address}/_Exec"),
                config => config
                    .WithServices(services =>
                    {
                        services.AddSingleton(responseStream);
                        return services;
                    })
                    .WithHandler<SubmitMessageRequest>(ExecuteMessageAsync),
                HostedHubCreation.Always);

            executionHub!.Post(request with { ResponsePath = responsePath });
        });

        hub.Post(new SubmitMessageResponse { Success = true }, o => o.ResponseFor(delivery));
        return delivery.Processed();
    }

    /// <summary>
    /// Async handler on the _Exec hosted hub.
    /// Prepares agent and await-streams the response.
    /// Uses stream.Update on the response node's remote stream (passed via DI).
    /// Cancellation: CancelCurrentExecution() on the _Exec hub cancels ct.
    /// </summary>
    private static async Task<IMessageDelivery> ExecuteMessageAsync(
        IMessageHub hub,
        IMessageDelivery<SubmitMessageRequest> delivery,
        CancellationToken ct)
    {
        var request = delivery.Message;
        var parentHub = hub.Configuration.ParentHub!;
        var threadPath = request.ThreadPath;
        var logger = parentHub.ServiceProvider.GetRequiredService<ILogger<AgentChatClient>>();
        var stream = hub.ServiceProvider.GetRequiredService<ISynchronizationStream<EntityStore>>();
        var responseMsgId = request.ResponsePath!.Split('/').Last();

        try
        {
            // 1. Prepare agent
            var chatClient = new AgentChatClient(parentHub.ServiceProvider);
            chatClient.SetThreadId(threadPath);
            await chatClient.InitializeAsync(request.ContextPath, request.ModelName);

            if (!string.IsNullOrEmpty(request.AgentName))
                chatClient.SetSelectedAgent(request.AgentName);

            if (request.Attachments is { Count: > 0 })
                chatClient.SetAttachments(request.Attachments);

            // 2. await streaming — update response node via stream.Update
            var responseText = new StringBuilder();
            var lastUpdate = DateTimeOffset.MinValue;
            var chatMessage = new ChatMessage(ChatRole.User, request.UserMessageText);

            await foreach (var update in chatClient.GetStreamingResponseAsync([chatMessage], ct))
            {
                if (!string.IsNullOrEmpty(update.Text))
                {
                    responseText.Append(update.Text);

                    if (DateTimeOffset.UtcNow - lastUpdate > TimeSpan.FromMilliseconds(200))
                    {
                        UpdateResponseNode(stream, responseMsgId, threadPath, responseText.ToString());
                        lastUpdate = DateTimeOffset.UtcNow;
                    }
                }
            }

            // 3. Final update
            UpdateResponseNode(stream, responseMsgId, threadPath, responseText.ToString());
        }
        catch (OperationCanceledException)
        {
            logger.LogInformation("ExecuteMessageAsync: cancelled for {ThreadPath}", threadPath);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "ExecuteMessageAsync: error for {ThreadPath}", threadPath);
        }

        return delivery.Processed();
    }

    private static void UpdateResponseNode(
        ISynchronizationStream<EntityStore> stream,
        string responseMsgId, string threadPath, string text)
    {
        var updatedNode = new MeshNode(responseMsgId, threadPath)
        {
            NodeType = ThreadMessageNodeType.NodeType,
            Content = new ThreadMessage
            {
                Id = responseMsgId,
                Role = "assistant",
                Text = text,
                Timestamp = DateTime.UtcNow,
                Type = ThreadMessageType.AgentResponse
            }
        };
        stream.Update(state =>
        {
            var store = WorkspaceOperations.Update(
                state ?? new(), nameof(MeshNode),
                c => c.Update(responseMsgId, updatedNode));
            return stream.ApplyChanges(new EntityStoreAndUpdates(store,
                [new EntityUpdate(nameof(MeshNode), responseMsgId, updatedNode)],
                stream.StreamId));
        });
    }

    /// <summary>
    /// Handles CancelThreadStreamRequest — cancels the _Exec sub-hub's execution token,
    /// which breaks the streaming loop in HandleExecStreaming.
    /// </summary>
    private static IMessageDelivery HandleCancelStream(
        IMessageHub hub, IMessageDelivery<CancelThreadStreamRequest> delivery)
    {
        var execHub = hub.GetHostedHub(new Address($"{hub.Address}/_Exec"), HostedHubCreation.Never);
        execHub?.CancelCurrentExecution();
        return delivery.Processed();
    }

    /// <summary>
    /// Gets the thread title from node name or falls back to default.
    /// </summary>
    private static string GetThreadTitle(MeshNode? node)
        => !string.IsNullOrEmpty(node?.Name) ? node.Name : "Thread";

    /// <summary>
    /// Handles DeleteFromMessageRequest — truncates ThreadMessages from the given message onwards.
    /// </summary>
    private static IMessageDelivery HandleDeleteFromMessage(
        IMessageHub hub,
        IMessageDelivery<DeleteFromMessageRequest> delivery)
    {
        var request = delivery.Message;
        var logger = hub.ServiceProvider.GetRequiredService<ILogger<AgentChatClient>>();
        logger.LogInformation("HandleDeleteFromMessage: threadPath={ThreadPath}, messageId={MessageId}",
            request.ThreadPath, request.MessageId);

        hub.InvokeAsync(() =>
        {
            hub.ServiceProvider.GetRequiredService<IWorkspace>()
                .GetStream<MeshNode>()?.Take(1).Subscribe(nodes =>
            {
                var threadNode = nodes?.FirstOrDefault(n => n.Path == request.ThreadPath);
                var currentContent = threadNode?.Content as MeshThread ?? new MeshThread();

                var msgList = currentContent.ThreadMessages.ToList();
                var msgIndex = msgList.IndexOf(request.MessageId);
                if (msgIndex < 0) return;

                var updatedMsgList = msgList.Take(msgIndex).ToList();
                var newContent = currentContent with { ThreadMessages = updatedMsgList };
                var updatedNode = (threadNode ?? new MeshNode(request.ThreadPath)) with { Content = newContent };
                hub.Post(new DataChangeRequest { Updates = [updatedNode] });
            });
        });

        return delivery.Processed();
    }

    /// <summary>
    /// Handles ResubmitMessageRequest — truncates ThreadMessages from the given message onwards,
    /// then posts a new SubmitMessageRequest with the provided text.
    /// </summary>
    private static IMessageDelivery HandleResubmitMessage(
        IMessageHub hub,
        IMessageDelivery<ResubmitMessageRequest> delivery)
    {
        var request = delivery.Message;
        var logger = hub.ServiceProvider.GetRequiredService<ILogger<AgentChatClient>>();
        logger.LogInformation("HandleResubmitMessage: threadPath={ThreadPath}, messageId={MessageId}",
            request.ThreadPath, request.MessageId);

        hub.InvokeAsync(() =>
        {
            hub.ServiceProvider.GetRequiredService<IWorkspace>()
                .GetStream<MeshNode>()?.Take(1).Subscribe(nodes =>
            {
                var threadNode = nodes?.FirstOrDefault(n => n.Path == request.ThreadPath);
                var currentContent = threadNode?.Content as MeshThread ?? new MeshThread();

                var msgList = currentContent.ThreadMessages.ToList();
                var msgIndex = msgList.IndexOf(request.MessageId);
                if (msgIndex < 0) return;

                // Truncate messages from the clicked one onwards
                var updatedMsgList = msgList.Take(msgIndex).ToList();
                var newContent = currentContent with { ThreadMessages = updatedMsgList };
                var updatedNode = (threadNode ?? new MeshNode(request.ThreadPath)) with { Content = newContent };
                hub.Post(new DataChangeRequest { Updates = [updatedNode] });

                // Resubmit the message
                hub.Post(new SubmitMessageRequest
                {
                    ThreadPath = request.ThreadPath,
                    UserMessageText = request.UserMessageText
                }, o => o.WithTarget(hub.Address));
            });
        });

        return delivery.Processed();
    }

    /// <summary>
    /// Handles EditMessageRequest — truncates ThreadMessages from the given message onwards,
    /// creates a new EditingPrompt message node, and appends it to ThreadMessages.
    /// </summary>
    private static IMessageDelivery HandleEditMessage(
        IMessageHub hub,
        IMessageDelivery<EditMessageRequest> delivery)
    {
        var request = delivery.Message;
        var logger = hub.ServiceProvider.GetRequiredService<ILogger<AgentChatClient>>();
        logger.LogInformation("HandleEditMessage: threadPath={ThreadPath}, messageId={MessageId}",
            request.ThreadPath, request.MessageId);

        var editMsgId = Guid.NewGuid().ToString("N")[..8];
        var meshService = hub.ServiceProvider.GetRequiredService<IMeshService>();

        // Create editing prompt node first
        meshService.CreateNodeAsync(new MeshNode(editMsgId, request.ThreadPath)
        {
            NodeType = ThreadMessageNodeType.NodeType,
            Content = new ThreadMessage
            {
                Id = editMsgId,
                Role = "user",
                Text = request.MessageText,
                Timestamp = DateTime.UtcNow,
                Type = ThreadMessageType.EditingPrompt
            }
        }).ContinueWith(t =>
        {
            if (t.IsFaulted)
            {
                logger.LogError(t.Exception, "HandleEditMessage: node creation FAILED for {ThreadPath}", request.ThreadPath);
                return;
            }

            // Update ThreadMessages: truncate from the clicked message, append editing prompt
            hub.InvokeAsync(() =>
            {
                hub.ServiceProvider.GetRequiredService<IWorkspace>()
                    .GetStream<MeshNode>()?.Take(1).Subscribe(nodes =>
                {
                    var threadNode = nodes?.FirstOrDefault(n => n.Path == request.ThreadPath);
                    var currentContent = threadNode?.Content as MeshThread ?? new MeshThread();

                    var msgList = currentContent.ThreadMessages.ToList();
                    var msgIndex = msgList.IndexOf(request.MessageId);
                    if (msgIndex < 0) return;

                    var updatedMsgList = msgList.Take(msgIndex).Concat([editMsgId]).ToList();
                    var newContent = currentContent with { ThreadMessages = updatedMsgList };
                    var updatedNode = (threadNode ?? new MeshNode(request.ThreadPath)) with { Content = newContent };
                    hub.Post(new DataChangeRequest { Updates = [updatedNode] });
                });
            });
        });

        return delivery.Processed();
    }
}
