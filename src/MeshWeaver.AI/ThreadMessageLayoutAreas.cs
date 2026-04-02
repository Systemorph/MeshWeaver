using System.Reactive.Linq;
using Humanizer;
using MeshWeaver.Application.Styles;
using MeshWeaver.Data;
using MeshWeaver.Domain;
using MeshWeaver.Graph;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.AI;

/// <summary>
/// Provides dedicated views for ThreadMessage nodes.
/// Renders based on ThreadMessageType:
/// - EditingPrompt: MarkdownEditorControl for editing
/// - ExecutedInput: MarkdownControl readonly, styled as user message
/// - AgentResponse: MarkdownControl readonly, styled as assistant message
/// </summary>
public static class ThreadMessageLayoutAreas
{
    /// <summary>
    /// Adds the thread message views to the hub's layout.
    /// </summary>
    public static MessageHubConfiguration AddThreadMessageViews(this MessageHubConfiguration configuration)
        => configuration
            .WithHandler<UpdateThreadMessageContent>(HandleUpdateContent)
            .AddLayout(layout => layout
                .WithDefaultArea(ThreadMessageNodeType.OverviewArea)
                .WithView(ThreadMessageNodeType.OverviewArea, Overview)
                .WithView("Streaming", StreamingCompact)
                .WithView(MeshNodeLayoutAreas.SettingsArea, SettingsLayoutArea.Settings)
                .WithView(MeshNodeLayoutAreas.MetadataArea, MeshNodeLayoutAreas.Metadata)
                .WithView(MeshNodeLayoutAreas.ThumbnailArea, Thumbnail));

    private const string MessageDataKey = "msg";

    /// <summary>
    /// Compact streaming view for parent thread consumption.
    /// Shows last 3 lines of text + tool call chips + delegation links.
    /// Subscribes to the MeshNodeReference sync stream for live updates.
    /// </summary>
    public static IObservable<UiControl?> StreamingCompact(LayoutAreaHost host, RenderingContext _)
    {
        var hubPath = host.Hub.Address.ToString();
        var syncStream = host.Workspace.GetStream(new MeshNodeReference());

        return syncStream!
            .Select(change =>
            {
                var msg = change.Value?.Content as ThreadMessage;
                if (msg == null) return (UiControl?)null;

                var stack = Controls.Stack
                    .WithStyle("font-size:0.75rem; color:var(--neutral-foreground-hint); line-height:1.3; gap:2px;");

                // Row 1: Last 3 lines of text
                var text = msg.Text ?? "";
                if (text.Length > 0)
                {
                    var lines = text.Split('\n');
                    var last = lines.Length > 3 ? lines[^3..] : lines;
                    var preview = string.Join("\n", last).Trim();
                    if (preview.Length > 200) preview = "..." + preview[^197..];
                    stack = stack.WithView(Controls.Html(
                        $"<pre style=\"margin:0; white-space:pre-wrap; max-height:45px; overflow:hidden; font-size:0.72rem;\">{System.Web.HttpUtility.HtmlEncode(preview)}</pre>"));
                }

                // Row 2: Tool call chips (non-delegation)
                var regularCalls = msg.ToolCalls.Where(tc => string.IsNullOrEmpty(tc.DelegationPath)).ToList();
                if (regularCalls.Count > 0)
                {
                    var chips = string.Join(" ", regularCalls.Select(tc =>
                    {
                        var icon = tc.Result != null ? "&#10003;" : "&#9679;";
                        var color = tc.Result != null ? "var(--neutral-foreground-hint)" : "var(--accent-fill-rest)";
                        var name = (tc.DisplayName ?? tc.Name);
                        if (name.Length > 25) name = name[..22] + "...";
                        return $"<span style=\"font-size:0.68rem; color:{color};\">{icon} {System.Web.HttpUtility.HtmlEncode(name)}</span>";
                    }));
                    stack = stack.WithView(Controls.Html($"<div style=\"display:flex; flex-wrap:wrap; gap:3px;\">{chips}</div>"));
                }

                // Row 3: Delegation sub-threads (recursive — embed their StreamingArea)
                foreach (var tc in msg.ToolCalls.Where(tc => !string.IsNullOrEmpty(tc.DelegationPath)))
                {
                    var icon = tc.Result != null ? "&#10003;" : "&#10041;";
                    var name = (tc.DisplayName ?? tc.Name);
                    if (name.Length > 30) name = name[..27] + "...";

                    var delStack = Controls.Stack
                        .WithStyle("border-left:2px solid var(--accent-fill-rest); padding-left:6px; margin-top:2px;");

                    delStack = delStack.WithView(Controls.Html(
                        $"<a href=\"/{tc.DelegationPath}\" style=\"font-size:0.7rem; color:var(--accent-fill-rest); text-decoration:none; font-weight:500;\">{icon} {System.Web.HttpUtility.HtmlEncode(name)}</a>"));

                    if (tc.Result == null)
                    {
                        // Recurse: embed sub-thread's StreamingArea
                        delStack = delStack.WithView(
                            new LayoutAreaControl(tc.DelegationPath!, new LayoutAreaReference(ThreadNodeType.StreamingArea)));
                    }

                    stack = stack.WithView(delStack);
                }

                return (UiControl?)stack;
            });
    }

    /// <summary>
    /// Handles content updates from thread execution.
    /// Runs ON the response message grain — updates local workspace → sync stream → clients.
    /// </summary>
    private static IMessageDelivery HandleUpdateContent(
        IMessageHub hub, IMessageDelivery<UpdateThreadMessageContent> delivery)
    {
        var msg = delivery.Message;
        var logger = hub.ServiceProvider.GetService<ILoggerFactory>()
            ?.CreateLogger("MeshWeaver.AI.MsgLayout");
        logger?.LogInformation("[MsgLayout] HANDLE_UPDATE: hub={Hub}, textLen={TextLen}, toolCalls={ToolCalls}",
            hub.Address, msg.Text?.Length ?? -1, msg.ToolCalls?.Count ?? -1);
        hub.GetWorkspace().UpdateMeshNode(node =>
        {
            var current = node.Content as ThreadMessage ?? new ThreadMessage { Id = node.Id, Role = "assistant", Text = "" };
            return node with
            {
                Content = current with
                {
                    Text = msg.Text ?? current.Text,
                    ToolCalls = msg.ToolCalls ?? current.ToolCalls,
                    AgentName = msg.AgentName ?? current.AgentName,
                    ModelName = msg.ModelName ?? current.ModelName,
                    DelegationPath = msg.DelegationPath ?? current.DelegationPath
                }
            };
        });
        return delivery.Processed();
    }

    /// <summary>
    /// Renders the Overview area for a ThreadMessage node.
    /// Emits control once from first node emission. Text and tool calls are data-bound
    /// via JsonPointerReference — updates flow through host.UpdateData, no control rebuilds.
    /// </summary>
    public static IObservable<UiControl?> Overview(LayoutAreaHost host, RenderingContext _)
    {
        var hubPath = host.Hub.Address.ToString();
        var lastSlash = hubPath.LastIndexOf('/');
        var threadPath = lastSlash > 0 ? hubPath[..lastSlash] : hubPath;
        var messageId = lastSlash > 0 ? hubPath[(lastSlash + 1)..] : hubPath;

        // Subscribe to the MeshNodeReference sync stream — receives updates from
        // responseStream.Update() / PatchDataChangeRequest. Map to ThreadMessageViewModel
        // and push to data section. The bubble binds to the view model via JsonPointerReference.
        var syncStream = host.Workspace.GetStream(new MeshNodeReference());

        host.SubscribeToDataStream(MessageDataKey, syncStream!
            .Select(change => change.Value?.Content as ThreadMessage)
            .Where(m => m != null)
            .Select(m => (ThreadMessageViewModel.FromMessage(m!) with
            {
                Text = ConvertReferencesToLinks(m!.Text ?? "")
            }))
            .DistinctUntilChanged()
            .Select(vm => (object)vm));

        // Emit control once — role/author are static, text/toolCalls are data-bound.
        return syncStream!
            .Select(change => change.Value?.Content as ThreadMessage)
            .Where(m => m != null)
            .Take(1)
            .Select(msg =>
            {
                if (msg!.Type == ThreadMessageType.EditingPrompt)
                    return (UiControl?)BuildEditingOverview(host, msg, threadPath, messageId);

                return (UiControl?)BuildMessageOverview(host, msg, threadPath, messageId);
            });
    }

    /// <summary>
    /// Builds the Overview for messages. Role/author are static from the initial message.
    /// Text and tool calls are data-bound via JsonPointerReference.
    /// </summary>
    private static UiControl BuildMessageOverview(
        LayoutAreaHost host, ThreadMessage msg, string threadPath, string messageId)
    {
        var isUser = msg.Role.Equals("user", StringComparison.OrdinalIgnoreCase);
        var authorName = msg.AuthorName ?? (isUser ? "You" : msg.AgentName ?? "Assistant");

        // Bind to ThreadMessageViewModel in data section
        var dataPointer = LayoutAreaReference.GetDataPointer(MessageDataKey);
        var bubble = new ThreadMessageBubbleControl()
            .WithRole(msg.Role)
            .WithAuthorName(authorName)
            .WithModelName(msg.ModelName)
            .WithTimestamp(msg.Timestamp)
            .WithText(new JsonPointerReference($"{dataPointer}/text"))
            .WithToolCalls(new JsonPointerReference($"{dataPointer}/toolCalls"))
            .WithThreadPath(threadPath);

        // Action buttons — small stealth icon buttons, right-aligned
        // Hidden during execution via CSS :has() on the parent container
        var actionRow = Controls.Stack
            .WithOrientation(Orientation.Horizontal)
            .WithClass("thread-msg-actions")
            .WithStyle("gap: 2px; justify-content: flex-end; margin-top: -4px; margin-bottom: 4px; opacity: 0.6;");

        if (isUser)
        {
            actionRow = actionRow
                .WithView(Controls.Button("")
                    .WithIconStart(FluentIcons.Edit(IconSize.Size16))
                    .WithAppearance(Appearance.Stealth)
                    .WithLabel("Edit")
                    .WithClickAction(_ =>
                    {
                        host.Hub.Post(new EditMessageRequest
                        {
                            ThreadPath = threadPath,
                            MessageId = messageId,
                            MessageText = msg.Text
                        }, o => o.WithTarget(new Address(threadPath)));
                    }))
                .WithView(Controls.Button("")
                    .WithIconStart(FluentIcons.ArrowSync(IconSize.Size16))
                    .WithAppearance(Appearance.Stealth)
                    .WithLabel("Resubmit")
                    .WithClickAction(_ =>
                    {
                        host.Hub.Post(new ResubmitMessageRequest
                        {
                            ThreadPath = threadPath,
                            MessageId = messageId,
                            UserMessageText = msg.Text
                        }, o => o.WithTarget(new Address(threadPath)));
                    }));
        }

        actionRow = actionRow
            .WithView(Controls.Button("")
                .WithIconStart(FluentIcons.Delete(IconSize.Size16))
                .WithAppearance(Appearance.Stealth)
                .WithLabel("Delete from here")
                .WithClickAction(_ =>
                {
                    host.Hub.Post(new DeleteFromMessageRequest
                    {
                        ThreadPath = threadPath,
                        MessageId = messageId
                    }, o => o.WithTarget(new Address(threadPath)));
                }));

        var container = Controls.Stack
            .WithClass("thread-msg-container")
            .WithStyle(isUser ? "align-items: flex-end;" : "")
            .WithView(bubble);

        // For assistant messages: show delegation sub-threads as clickable links
        if (!isUser)
        {
            var messagePath = $"{threadPath}/{messageId}";
            container = container.WithView((h, c) =>
            {
                var meshService = h.Hub.ServiceProvider.GetService<IMeshService>();
                if (meshService == null) return Observable.Return<UiControl?>(null);

                return Observable.FromAsync(async () =>
                {
                    try
                    {
                        var subs = await meshService
                            .QueryAsync<MeshNode>($"namespace:{messagePath} nodeType:{ThreadNodeType.NodeType}")
                            .ToListAsync();
                        if (subs.Count == 0) return (UiControl?)null;
                        return (UiControl?)BuildDelegationLinks(subs);
                    }
                    catch { return (UiControl?)null; }
                });
            });
        }

        container = container.WithView(actionRow);
        return container;
    }

    /// <summary>
    /// Builds simple navigation links for delegation sub-threads.
    /// Each sub-thread is rendered as a clickable link showing its name.
    /// </summary>
    private static UiControl BuildDelegationLinks(IReadOnlyList<MeshNode> subThreads)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append("<div style=\"margin: 4px 0 8px 0;\">");

        foreach (var st in subThreads)
        {
            var name = System.Web.HttpUtility.HtmlEncode(
                st.Name?.Length > 80 ? st.Name[..77] + "..." : st.Name ?? st.Id);
            var href = $"/{st.Path}";

            sb.Append($"<a href=\"{href}\" style=\"display: flex; align-items: center; gap: 6px; padding: 2px 0; " +
                       $"font-size: 0.8rem; color: var(--accent-fill-rest); text-decoration: none;\">" +
                       $"<span style=\"font-size: 10px;\">&#8618;</span> {name}</a>");
        }

        sb.Append("</div>");
        return Controls.Html(sb.ToString());
    }

    /// <summary>
    /// Converts inline @path references in text to clickable markdown links.
    /// @path/to/node → [@path/to/node](@path/to/node)
    /// The href uses @prefix so LinkUrlCleanupExtension resolves it
    /// (strips @, resolves relative paths against current node).
    /// Skips references already inside markdown links or code blocks.
    /// </summary>
    private static string ConvertReferencesToLinks(string text)
    {
        if (string.IsNullOrEmpty(text) || !text.Contains('@'))
            return text;

        // Match @path references not already inside markdown link syntax
        return System.Text.RegularExpressions.Regex.Replace(
            text,
            @"(?<!\[)(?<!\()@([a-zA-Z0-9_/\-.:]+[a-zA-Z0-9_/\-])(?!\])(?!\))",
            match =>
            {
                var path = match.Groups[1].Value;
                // Don't convert email addresses
                if (path.Contains('@'))
                    return match.Value;
                // Use @prefix in href — LinkUrlCleanupExtension will strip @ and resolve
                return $"[`@{path}`](@{path})";
            });
    }

    /// <summary>
    /// Builds the Overview for EditingPrompt messages:
    /// inline MarkdownEditorControl with Submit and Cancel buttons.
    /// </summary>
    private static UiControl BuildEditingOverview(
        LayoutAreaHost host, ThreadMessage msg, string threadPath, string messageId)
    {
        const string textDataId = "editText";
        host.UpdateData(textDataId, msg.Text ?? "");

        var editor = new MarkdownEditorControl()
            { Value = new JsonPointerReference(LayoutAreaReference.GetDataPointer(textDataId)) }
            .WithDocumentId($"{threadPath}/{messageId}")
            .WithHeight("120px")
            .WithMaxHeight("200px")
            .WithPlaceholder("Edit your message...");

        var buttonRow = Controls.Stack
            .WithOrientation(Orientation.Horizontal)
            .WithStyle("gap: 8px; justify-content: flex-end; margin-top: 8px;")
            .WithView(Controls.Button("Cancel")
                .WithAppearance(Appearance.Neutral)
                .WithClickAction(_ =>
                {
                    host.Hub.Post(new DeleteFromMessageRequest
                    {
                        ThreadPath = threadPath,
                        MessageId = messageId
                    }, o => o.WithTarget(new Address(threadPath)));
                }))
            .WithView(Controls.Button("Submit")
                .WithAppearance(Appearance.Accent)
                .WithIconStart(FluentIcons.Send(IconSize.Size16))
                .WithClickAction(async actx =>
                {
                    var editedText = await actx.Host.Stream.GetDataStream<string>(textDataId).FirstAsync();
                    actx.Hub.Post(new ResubmitMessageRequest
                    {
                        ThreadPath = threadPath,
                        MessageId = messageId,
                        UserMessageText = editedText ?? msg.Text ?? ""
                    }, o => o.WithTarget(new Address(threadPath)));
                }));

        return Controls.Stack
            .WithStyle("max-width: 80%; padding: 12px 16px; border-radius: 12px; border-bottom-right-radius: 4px; " +
                        "background: var(--neutral-layer-4); border-inline-end: 3px solid var(--accent-fill-rest); " +
                        "margin-bottom: 12px; align-self: flex-end; margin-left: auto;")
            .WithView(Controls.Html("<div style=\"font-weight: 600; font-size: 0.85rem; color: var(--accent-fill-rest); margin-bottom: 8px;\">Edit message</div>"))
            .WithView(editor)
            .WithView(buttonRow);
    }

    /// <summary>
    /// Builds the appropriate view for a ThreadMessage based on its Type.
    /// </summary>
    private static UiControl BuildMessageView(LayoutAreaHost host, MeshNode? node, string messagePath)
    {
        var message = node?.Content as ThreadMessage;
        if (message == null)
        {
            // Node not yet loaded — show loading skeleton as progress indicator
            return Controls.Html(
                "<div style=\"padding: 12px 16px; margin-bottom: 12px;\">" +
                "<div style=\"height: 14px; width: 80px; background: var(--neutral-stroke-rest); border-radius: 4px; margin-bottom: 8px; animation: agent-skeleton-pulse 1.5s ease-in-out infinite;\"></div>" +
                "<div style=\"height: 14px; width: 60%; background: var(--neutral-stroke-rest); border-radius: 4px; margin-bottom: 6px; animation: agent-skeleton-pulse 1.5s ease-in-out infinite; animation-delay: 0.1s;\"></div>" +
                "<div style=\"height: 14px; width: 40%; background: var(--neutral-stroke-rest); border-radius: 4px; animation: agent-skeleton-pulse 1.5s ease-in-out infinite; animation-delay: 0.2s;\"></div>" +
                "</div>" +
                "<style>@keyframes agent-skeleton-pulse { 0%, 100% { opacity: 0.4; } 50% { opacity: 1; } }</style>");
        }

        return message.Type switch
        {
            ThreadMessageType.EditingPrompt => BuildEditingPromptView(host, message, messagePath),
            ThreadMessageType.ExecutedInput => BuildUserMessageView(message),
            ThreadMessageType.AgentResponse => BuildAgentResponseView(message),
            _ => BuildUserMessageView(message) // Default fallback
        };
    }

    /// <summary>
    /// Builds an editing view with MarkdownEditorControl for EditingPrompt messages.
    /// When isLast is true, adds a Submit button to execute the prompt.
    /// </summary>
    private static UiControl BuildEditingPromptView(LayoutAreaHost host, ThreadMessage message, string messagePath)
    {
        var container = Controls.Stack
            .WithWidth("100%")
            .WithStyle("padding: 12px;");

        // Author header
        var authorName = message.AuthorName ?? "You";
        container = container.WithView(Controls.Html(
            $"<div style=\"font-weight: 600; font-size: 0.85rem; color: var(--accent-fill-rest); margin-bottom: 8px;\">{System.Web.HttpUtility.HtmlEncode(authorName)} (editing)</div>"));

        // Store text in host data for two-way binding with the editor
        var textDataId = $"editingText_{messagePath.Replace("/", "_")}";
        host.UpdateData(textDataId, message.Text ?? "");

        // MarkdownEditorControl for editing, bound to host data
        var editor = new MarkdownEditorControl() { Value = new JsonPointerReference(LayoutAreaReference.GetDataPointer(textDataId)) }
            .WithDocumentId(messagePath)
            .WithHeight("200px")
            .WithMaxHeight("300px")
            .WithPlaceholder("Type your message...");

        container = container.WithView(editor);

        return container;
    }

    /// <summary>
    /// Builds a readonly view for ExecutedInput (user) messages.
    /// Outer flex container right-aligns; inner bubble has max-width and accent background.
    /// </summary>
    private static UiControl BuildUserMessageView(ThreadMessage message)
    {
        var authorName = message.AuthorName ?? "You";

        var bubble = Controls.Stack
            .WithStyle("max-width: 80%; padding: 12px 16px; border-radius: 12px; border-bottom-right-radius: 4px; background: var(--neutral-layer-4); border-inline-end: 3px solid var(--accent-fill-rest); color: var(--neutral-foreground-rest);")
            .WithView(Controls.Html($"<div style=\"font-weight: 600; font-size: 0.85rem; color: var(--accent-fill-rest); margin-bottom: 4px;\">{System.Web.HttpUtility.HtmlEncode(authorName)}</div>"))
            .WithView(new MarkdownControl(message.Text) { ShowReferences = false }.WithStyle("background: transparent;"))
            .WithView(BuildReferenceChips(message.Text))
            .WithView(Controls.Html($"<div style=\"font-size: 0.75rem; color: var(--neutral-foreground-hint); margin-top: 4px;\">{message.Timestamp.Humanize()}</div>"));

        // Add delegation link if present
        if (!string.IsNullOrEmpty(message.DelegationPath))
        {
            bubble = bubble.WithView(Controls.Stack
                .WithOrientation(Orientation.Horizontal)
                .WithStyle("margin-top: 8px; padding-top: 8px; border-top: 1px solid var(--neutral-stroke-rest);")
                .WithView(Controls.Icon(FluentIcons.ArrowRight(IconSize.Size16)).WithStyle("font-size: 14px;"))
                .WithView(new NavLinkControl("View delegation", null, $"/{message.DelegationPath}")));
        }

        return Controls.Stack
            .WithWidth("100%")
            .WithStyle("align-items: flex-end; margin-bottom: 12px;")
            .WithView(bubble);
    }

    /// <summary>
    /// Builds compact inline reference chips from @references found in message text.
    /// </summary>
    private static UiControl BuildReferenceChips(string? text)
    {
        var paths = MarkdownReferenceExtractor.GetUniquePaths(text);
        if (paths.Count == 0)
            return Controls.Html("");

        var chipHtml = string.Join(" ", paths.Select(path =>
        {
            var displayName = path.Contains('/') ? path[(path.LastIndexOf('/') + 1)..] : path;
            var encoded = System.Web.HttpUtility.HtmlEncode(displayName);
            var encodedPath = System.Web.HttpUtility.HtmlEncode(path);
            return $"<a href=\"/{encodedPath}\" title=\"{encodedPath}\" style=\"display: inline-flex; align-items: center; gap: 4px; padding: 2px 10px; background: var(--neutral-layer-2); border: 1px solid var(--neutral-stroke-rest); border-radius: 16px; font-size: 0.8rem; color: var(--accent-fill-rest); text-decoration: none; white-space: nowrap;\">" +
                   $"<span style=\"max-width: 120px; overflow: hidden; text-overflow: ellipsis;\">{encoded}</span></a>";
        }));

        return Controls.Html($"<div style=\"display: flex; flex-wrap: wrap; gap: 6px; margin-top: 6px;\">{chipHtml}</div>");
    }

    /// <summary>
    /// Builds a readonly view for AgentResponse (assistant) messages.
    /// Outer flex container left-aligns; inner bubble has max-width and neutral background.
    /// Shows animated dots when text is empty (agent generating).
    /// </summary>
    private static UiControl BuildAgentResponseView(ThreadMessage message)
    {
        var authorName = message.AuthorName ?? message.AgentName ?? "Assistant";
        var isSystem = message.Role.Equals("system", StringComparison.OrdinalIgnoreCase);
        var bgColor = isSystem ? "var(--neutral-layer-3)" : "var(--neutral-layer-2)";

        // Build subtitle with model info
        var subtitle = !string.IsNullOrEmpty(message.ModelName)
            ? $"<div style=\"font-size: 0.75rem; color: var(--neutral-foreground-hint); margin-bottom: 4px;\">{System.Web.HttpUtility.HtmlEncode(message.ModelName)}</div>"
            : "";

        // Show animated dots when response text is empty (agent is generating)
        // Progress status is shown separately above the chat input
        UiControl contentView;
        if (string.IsNullOrEmpty(message.Text))
        {
            contentView = Controls.Html(
                "<div style=\"display: flex; align-items: center; gap: 8px; padding: 8px 0;\">" +
                "<span style=\"display: inline-flex; gap: 4px;\">" +
                "<span style=\"width: 5px; height: 5px; border-radius: 50%; background: var(--neutral-foreground-hint); animation: agent-dots-blink 1.4s infinite both; animation-delay: 0s;\"></span>" +
                "<span style=\"width: 5px; height: 5px; border-radius: 50%; background: var(--neutral-foreground-hint); animation: agent-dots-blink 1.4s infinite both; animation-delay: 0.2s;\"></span>" +
                "<span style=\"width: 5px; height: 5px; border-radius: 50%; background: var(--neutral-foreground-hint); animation: agent-dots-blink 1.4s infinite both; animation-delay: 0.4s;\"></span>" +
                "</span>" +
                "</div>" +
                "<style>@keyframes agent-dots-blink { 0%, 80%, 100% { opacity: 0.3; } 40% { opacity: 1; } }</style>");
        }
        else
        {
            contentView = new MarkdownControl(message.Text).WithStyle("background: transparent;");
        }

        var bubble = Controls.Stack
            .WithStyle($"max-width: 80%; padding: 12px 16px; border-radius: 12px; border-bottom-left-radius: 4px; background: {bgColor}; color: var(--neutral-foreground-rest);")
            .WithView(Controls.Html($"<div style=\"font-weight: 600; font-size: 0.85rem; margin-bottom: 4px;\">{System.Web.HttpUtility.HtmlEncode(authorName)}</div>"))
            .WithView(Controls.Html(subtitle))
            .WithView(contentView)
            .WithView(Controls.Html($"<div style=\"font-size: 0.75rem; color: var(--neutral-foreground-hint); margin-top: 4px;\">{message.Timestamp.Humanize()}</div>"));

        // Add delegation link if present
        if (!string.IsNullOrEmpty(message.DelegationPath))
        {
            bubble = bubble.WithView(Controls.Stack
                .WithOrientation(Orientation.Horizontal)
                .WithStyle("margin-top: 8px; padding-top: 8px; border-top: 1px solid var(--neutral-stroke-rest);")
                .WithView(Controls.Icon(FluentIcons.ArrowRight(IconSize.Size16)).WithStyle("font-size: 14px;"))
                .WithView(new NavLinkControl("View delegation", null, $"/{message.DelegationPath}")));
        }

        return Controls.Stack
            .WithWidth("100%")
            .WithStyle("align-items: flex-start; margin-bottom: 12px;")
            .WithView(bubble);
    }

    /// <summary>
    /// Renders a compact thumbnail for thread message nodes.
    /// </summary>
    public static IObservable<UiControl?> Thumbnail(LayoutAreaHost host, RenderingContext _)
    {
        var hubPath = host.Hub.Address.ToString();

        var nodeStream = host.Workspace.GetStream<MeshNode>()?.Select(nodes => nodes ?? Array.Empty<MeshNode>())
            ?? Observable.Return<MeshNode[]>(Array.Empty<MeshNode>());

        return nodeStream.Select(nodes =>
        {
            var node = nodes.FirstOrDefault(n => n.Path == hubPath);
            return BuildThumbnail(node);
        });
    }

    private static UiControl BuildThumbnail(MeshNode? node)
    {
        var message = node?.Content as ThreadMessage;
        var role = message?.Role ?? "unknown";
        var text = message?.Text ?? "";
        var preview = text.Length > 50 ? text[..47] + "..." : text;
        var icon = role.Equals("user", StringComparison.OrdinalIgnoreCase) ? "Person" : "Bot";
        var typeLabel = message?.Type switch
        {
            ThreadMessageType.EditingPrompt => "Editing",
            ThreadMessageType.ExecutedInput => "User",
            ThreadMessageType.AgentResponse => message?.AgentName ?? "Assistant",
            _ => role
        };

        return Controls.Stack
            .WithStyle("padding: 8px 12px; background: var(--neutral-layer-card-container); border: 1px solid var(--neutral-stroke-rest); border-radius: 6px;")
            .WithView(Controls.Stack
                .WithOrientation(Orientation.Horizontal)
                .WithStyle("align-items: center; gap: 8px;")
                .WithView(Controls.Icon(icon).WithStyle("font-size: 16px; color: var(--accent-fill-rest);"))
                .WithView(Controls.Html($"<span style=\"font-size: 0.85rem; font-weight: 500;\">{typeLabel}</span>")))
            .WithView(Controls.Html($"<p style=\"margin: 4px 0 0 0; font-size: 0.85rem; color: var(--neutral-foreground-hint); overflow: hidden; text-overflow: ellipsis; white-space: nowrap;\">{System.Web.HttpUtility.HtmlEncode(preview)}</p>"));
    }
}
