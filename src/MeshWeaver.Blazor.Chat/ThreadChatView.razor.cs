using System.Text.Json;
using MeshWeaver.AI;
using MeshWeaver.AI.Completion;
using MeshWeaver.AI.Persistence;
using MeshWeaver.Blazor.Components.Monaco;
using MeshWeaver.Data;
using MeshWeaver.Messaging;
using MeshWeaver.Data.Completion;
using MeshWeaver.Layout;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.ShortGuid;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MeshThread = MeshWeaver.AI.Thread;
using TextContent = Microsoft.Extensions.AI.TextContent;

namespace MeshWeaver.Blazor.Chat;

public partial class ThreadChatView : BlazorView<ThreadChatControl, ThreadChatView>
{
    [Inject] private INavigationService NavigationService { get; set; } = null!;
    [Inject] private SidePanelStateService SidePanelState { get; set; } = null!;

    private bool _isDisposed;
    private readonly string _instanceId = Guid.NewGuid().ToString("N")[..8];

    // Thread state
    private string? threadPath;
    private string? initialContext; // Backing field for agent initialization
    private string? lastContextUrl; // Track URL for context change detection
    private bool isLoadingThread;
    private bool isGeneratingResponse;

    // Chat state
    private IAgentChat? chat;
    private CancellationTokenSource currentResponseCancellation = new();
    private ChatMessage? currentResponseMessage;
    private readonly List<ChatMessage> messages = new();
    private MonacoEditorView? monacoEditor;
    private string? MessageText;

    // Unified attachments (context + @references)
    private readonly List<AttachmentInfo> attachments = new();
    private const string placeholderText = "Type a message... Use @ to reference nodes";

    // Agent/model selection
    private AgentDisplayInfo? selectedAgentInfo;
    private ModelInfo? selectedModelInfo;
    private IReadOnlyList<AgentDisplayInfo> agentDisplayInfos = [];
    private IReadOnlyList<ModelInfo> availableModels = [];
    private readonly Dictionary<string, string> agentModelPreferences = new();

    private IEnumerable<IChatClientFactory> ChatClientFactories => Hub.ServiceProvider.GetServices<IChatClientFactory>();

    protected override async Task OnInitializedAsync()
    {
        Logger.LogDebug("[ThreadChat:{InstanceId}] OnInitializedAsync started", _instanceId);

        // Capture initial URL context
        lastContextUrl = NavigationManager.Uri;
        if (string.IsNullOrEmpty(initialContext))
        {
            var path = NavigationManager.ToBaseRelativePath(NavigationManager.Uri);
            if (!string.IsNullOrEmpty(path) && path != "chat")
            {
                // Resolve satellite content to its primary node path
                path = await ResolvePrimaryContextPathAsync(path);
                initialContext = path;
                var displayName = await ResolveContextDisplayNameAsync(path);
                attachments.Add(new AttachmentInfo(path, displayName, IsContext: true));
            }
        }
        else
        {
            // initialContext was set via data binding; add as context attachment
            var displayName = await ResolveContextDisplayNameAsync(initialContext);
            attachments.Add(new AttachmentInfo(initialContext, displayName, IsContext: true));
        }

        try
        {
            await InitializeAgentAndModelSelectionsAsync();
            Logger.LogDebug("[ThreadChat:{InstanceId}] Agent and model selections initialized", _instanceId);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "[ThreadChat:{InstanceId}] Failed to initialize agent/model selections", _instanceId);
        }

        // Create chat client
        try
        {
            chat = await CreateChatAsync();
            Logger.LogDebug("[ThreadChat:{InstanceId}] Chat initialized", _instanceId);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "[ThreadChat:{InstanceId}] Failed to create chat", _instanceId);
        }

        // Load thread if path is specified
        if (!string.IsNullOrEmpty(threadPath))
        {
            await LoadThreadAsync();
        }

        Logger.LogDebug("[ThreadChat:{InstanceId}] OnInitializedAsync completed", _instanceId);
    }

    private async Task<string?> ResolveContextDisplayNameAsync(string? path)
    {
        if (string.IsNullOrEmpty(path))
            return null;

        try
        {
            var meshCatalog = Hub.ServiceProvider.GetService<IMeshCatalog>();
            if (meshCatalog == null)
                return null;

            var resolution = await meshCatalog.ResolvePathAsync(path);
            if (resolution == null)
                return null;

            var node = await meshCatalog.GetNodeAsync((Address)resolution.Prefix);
            return node?.Name ?? node?.Id;
        }
        catch (Exception ex)
        {
            Logger.LogDebug(ex, "Error resolving context display name for path: {Path}", path);
            return null;
        }
    }

    /// <summary>
    /// Resolves a path to its primary context path, handling ISatelliteContent.
    /// If the node at the path is a satellite, returns its PrimaryNodePath instead.
    /// </summary>
    private async Task<string> ResolvePrimaryContextPathAsync(string path)
    {
        try
        {
            var meshCatalog = Hub.ServiceProvider.GetService<IMeshCatalog>();
            if (meshCatalog == null)
                return path;

            var node = await meshCatalog.GetNodeAsync(new Address(path));
            if (node?.Content is ISatelliteContent satellite && !string.IsNullOrEmpty(satellite.PrimaryNodePath))
                return satellite.PrimaryNodePath;
        }
        catch (Exception ex)
        {
            Logger.LogDebug(ex, "Error resolving primary context path for: {Path}", path);
        }

        return path;
    }

    protected override void BindData()
    {
        base.BindData();
        DataBind(ViewModel.ThreadPath, x => x.threadPath);
        DataBind(ViewModel.InitialContext, x => x.initialContext);
    }

    private async Task InitializeAgentAndModelSelectionsAsync()
    {
        // Log factory resolution
        var factories = ChatClientFactories.ToList();
        Logger.LogInformation("[ThreadChat:{InstanceId}] IChatClientFactory instances resolved: {Count}", _instanceId, factories.Count);
        foreach (var f in factories)
        {
            Logger.LogInformation("[ThreadChat:{InstanceId}] Factory: {Name}, DisplayOrder: {Order}, Models ({ModelCount}): [{Models}]",
                _instanceId, f.Name, f.DisplayOrder, f.Models.Count, string.Join(", ", f.Models));
        }

        // Get available models from all factories
        availableModels = factories
            .OrderBy(f => f.DisplayOrder)
            .SelectMany(f => f.Models.Select(m => new ModelInfo
            {
                Name = m,
                Provider = f.Name,
                DisplayOrder = f.DisplayOrder
            }))
            .ToList();

        Logger.LogInformation("[ThreadChat:{InstanceId}] Available models ({Count}): [{Models}]",
            _instanceId, availableModels.Count, string.Join(", ", availableModels.Select(m => $"{m.Name} ({m.Provider})")));

        // Create a temporary chat to get ordered agents
        var tempChat = new AgentChatClient(Hub.ServiceProvider);
        await tempChat.InitializeAsync(initialContext, availableModels.FirstOrDefault()?.Name);

        // Get ordered agents from the chat
        agentDisplayInfos = await tempChat.GetOrderedAgentsAsync();

        // First agent in the ordered list is the default
        selectedAgentInfo = agentDisplayInfos.FirstOrDefault();

        // Set initial model based on agent preference or default
        if (selectedAgentInfo != null)
        {
            selectedModelInfo = GetPreferredModelInfoForAgent(selectedAgentInfo.Name);
        }
        else
        {
            selectedModelInfo = availableModels.FirstOrDefault();
        }

        StateHasChanged();
    }

    private ModelInfo? GetPreferredModelInfoForAgent(string agentName)
    {
        if (agentModelPreferences.TryGetValue(agentName, out var preferredModelName))
            return availableModels.FirstOrDefault(m => m.Name == preferredModelName);

        var agentConfig = agentDisplayInfos.FirstOrDefault(a => a.Name == agentName)?.AgentConfiguration;
        if (!string.IsNullOrEmpty(agentConfig?.PreferredModel))
        {
            var configuredModel = availableModels.FirstOrDefault(m => m.Name == agentConfig.PreferredModel);
            if (configuredModel != null)
                return configuredModel;
        }

        return availableModels.FirstOrDefault();
    }

    private async Task<IAgentChat> CreateChatAsync()
    {
        var model = selectedModelInfo?.Name ?? availableModels.FirstOrDefault()?.Name ?? string.Empty;

        // Ensure we have context path — initialContext may be cleared by data binding
        var contextPath = initialContext;
        if (string.IsNullOrEmpty(contextPath))
        {
            var path = NavigationManager.ToBaseRelativePath(NavigationManager.Uri);
            if (!string.IsNullOrEmpty(path) && path != "chat")
                contextPath = path;
        }

        var chatClient = new AgentChatClient(Hub.ServiceProvider);
        await chatClient.InitializeAsync(contextPath, model);

        // Set the explicitly selected agent from the dropdown
        if (selectedAgentInfo != null)
        {
            chatClient.SetSelectedAgent(selectedAgentInfo.Name);
        }

        return chatClient;
    }

    private async Task LoadThreadAsync()
    {
        if (string.IsNullOrEmpty(threadPath) || isLoadingThread)
            return;

        try
        {
            isLoadingThread = true;
            StateHasChanged();

            // Load thread node via IMeshCatalog
            var meshCatalog = Hub.ServiceProvider.GetService<IMeshCatalog>();
            var meshQuery = Hub.ServiceProvider.GetService<IMeshQuery>();
            var node = meshCatalog != null ? await meshCatalog.GetNodeAsync(new Address(threadPath)) : null;
            var threadContent = node?.Content as MeshThread;

            messages.Clear();

            // Try to load messages from child ThreadMessage nodes first
            if (meshQuery != null)
            {
                var messageNodes = await meshQuery.QueryAsync<MeshNode>(
                    $"path:{threadPath} nodeType:{ThreadMessageNodeType.NodeType} scope:children sort:Timestamp-asc"
                ).ToListAsync();

                var threadMessages = messageNodes
                    .Select(n => n.Content as ThreadMessage)
                    .Where(m => m != null && m.Type != ThreadMessageType.EditingPrompt)
                    .Select(m => m!) // Remove nullability after filtering
                    .OrderBy(m => m.Timestamp)
                    .ToList();

                if (threadMessages.Count > 0)
                {
                    // Load from child nodes
                    foreach (var msg in threadMessages.ToChatMessages())
                    {
                        messages.Add(msg);
                    }
                    Logger.LogDebug("[ThreadChat:{InstanceId}] Loaded {Count} messages from child nodes", _instanceId, threadMessages.Count);
                }
            }

            // Fall back to legacy inline messages if no child nodes found
#pragma warning disable CS0618 // Type or member is obsolete
            if (messages.Count == 0 && threadContent?.Messages?.Count > 0)
            {
                var loadedMessages = threadContent.ToChatMessages();
                foreach (var msg in loadedMessages)
                {
                    messages.Add(msg);
                }
                Logger.LogDebug("[ThreadChat:{InstanceId}] Loaded {Count} legacy inline messages", _instanceId, loadedMessages.Count);
            }
#pragma warning restore CS0618

            // Resume chat with loaded messages
            if (chat is AgentChatClient agentChatClient && messages.Count > 0)
            {
                var conversation = new ChatConversation
                {
                    Id = threadPath,
                    Title = node?.Name ?? attachments.FirstOrDefault(a => a.IsContext)?.DisplayName ?? "Thread",
                    CreatedAt = DateTime.UtcNow,
                    LastModifiedAt = DateTime.UtcNow,
                    Messages = messages.ToList()
                };
                await agentChatClient.ResumeAsync(conversation);
            }

            chat?.SetThreadId(threadPath);

            // Pass persistent thread ID if the thread has one
            if (!string.IsNullOrEmpty(threadContent?.PersistentThreadId))
            {
                chat?.SetPersistentThreadId(threadContent.PersistentThreadId);
            }

            Logger.LogDebug("[ThreadChat:{InstanceId}] Loaded thread: {Path} (PersistentThreadId={PersistentThreadId})",
                _instanceId, threadPath, threadContent?.PersistentThreadId ?? "(none)");
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "[ThreadChat:{InstanceId}] Error loading thread: {Path}", _instanceId, threadPath);
        }
        finally
        {
            isLoadingThread = false;
            StateHasChanged();
        }
    }

    /// <summary>
    /// Saves the thread by updating its LastModified timestamp.
    /// Individual messages are saved as child nodes via SaveMessageAsChildNodeAsync.
    /// </summary>
    private async Task SaveThreadAsync()
    {
        if (string.IsNullOrEmpty(threadPath))
            return;

        try
        {
            // Load existing node to update LastModified
            var meshCatalog = Hub.ServiceProvider.GetService<IMeshCatalog>();
            var existingNode = meshCatalog != null ? await meshCatalog.GetNodeAsync(new Address(threadPath)) : null;

            if (existingNode != null)
            {
                // Touch the thread to update LastModified
                var updatedNode = existingNode with { LastModified = DateTime.UtcNow };
                var nodeJson = JsonSerializer.SerializeToElement(updatedNode, Hub.JsonSerializerOptions);
                Hub.Post(new DataChangeRequest { Updates = [nodeJson] }, o => o.WithTarget(new Address(threadPath)));
            }

            Logger.LogDebug("[ThreadChat:{InstanceId}] Updated thread timestamp: {Path}", _instanceId, threadPath);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "[ThreadChat:{InstanceId}] Error saving thread", _instanceId);
        }
    }

    /// <summary>
    /// Saves a message as a child node of the thread.
    /// </summary>
    private async Task SaveMessageAsChildNodeAsync(ChatMessage message, ThreadMessageType messageType)
    {
        if (string.IsNullOrEmpty(threadPath))
            return;

        try
        {
            var meshCatalog = Hub.ServiceProvider.GetRequiredService<IMeshCatalog>();
            var messageId = Guid.NewGuid().AsString();
            var messagePath = $"{threadPath}/{messageId}";

            var threadMessage = new ThreadMessage
            {
                Id = messageId,
                Role = message.Role.Value,
                AuthorName = message.AuthorName,
                Text = message.Text ?? string.Empty,
                Timestamp = DateTime.UtcNow,
                Type = messageType
            };

            var messageNode = new MeshNode(messagePath)
            {
                NodeType = ThreadMessageNodeType.NodeType,
                Content = threadMessage
            };

            await meshCatalog.CreateNodeAsync(messageNode, GetCurrentUserId());
            Logger.LogDebug("[ThreadChat:{InstanceId}] Saved message as child node: {Path}", _instanceId, messagePath);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "[ThreadChat:{InstanceId}] Error saving message as child node", _instanceId);
        }
    }

    private string? GetThreadTitle()
    {
        var firstUserMessage = messages.FirstOrDefault(m =>
            m.Role.Value.Equals("User", StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(m.Text));

        if (firstUserMessage != null)
        {
            return firstUserMessage.Text!.Length > 50
                ? firstUserMessage.Text[..50] + "..."
                : firstUserMessage.Text;
        }

        return null;
    }

    /// <summary>
    /// Checks if the navigation context has changed since the last message and updates the context attachment if needed.
    /// </summary>
    private async Task CheckAndUpdateContextAsync()
    {
        var currentUrl = NavigationManager.Uri;

        // Check if URL has changed
        if (lastContextUrl != currentUrl)
        {
            var newPath = NavigationManager.ToBaseRelativePath(currentUrl);

            // Only update if we have a meaningful path (not root or chat)
            if (!string.IsNullOrEmpty(newPath) && newPath != "chat")
            {
                // Resolve satellite content to its primary node path
                newPath = await ResolvePrimaryContextPathAsync(newPath);

                // Only update if path is actually different from current context
                if (newPath != initialContext)
                {
                    Logger.LogDebug("[ThreadChat:{InstanceId}] Context changed from {OldContext} to {NewContext}",
                        _instanceId, initialContext, newPath);

                    initialContext = newPath;
                    var displayName = await ResolveContextDisplayNameAsync(newPath);

                    // Replace or add context attachment
                    attachments.RemoveAll(a => a.IsContext);
                    attachments.Insert(0, new AttachmentInfo(newPath, displayName, IsContext: true));
                    StateHasChanged();
                }
            }

            lastContextUrl = currentUrl;
        }
    }

    /// <summary>
    /// Creates a new thread lazily on first message.
    /// Thread is created under {parentPath}/{threadId}
    /// Messages are stored as child nodes, not inline.
    /// </summary>
    private void CreateThreadAsync()
    {
        var threadId = Guid.NewGuid().AsString();
        var parentPath = initialContext;

        threadPath = string.IsNullOrEmpty(parentPath) ? threadId : $"{parentPath}/{threadId}";

        // Generate title from first user message
        var title = GetThreadTitle() ?? $"Chat {DateTime.UtcNow:yyyy-MM-dd HH:mm}";

        // Create empty thread content (messages stored as child nodes)
        var threadContent = new MeshThread
        {
            ParentPath = parentPath
        };

        var newNode = new MeshNode(threadPath)
        {
            Name = title,
            NodeType = ThreadNodeType.NodeType,
            Content = threadContent
        };

        var meshCatalog = Hub.ServiceProvider.GetRequiredService<IMeshCatalog>();

        meshCatalog.CreateNodeAsync(newNode, GetCurrentUserId())
            .ContinueWith(async task =>
            {
                if (task.IsCompletedSuccessfully)
                {
                    threadPath = task.Result.Path;
                    chat?.SetThreadId(threadPath);
                    SidePanelState.SetContentPath(threadPath);
                    Logger.LogDebug("[ThreadChat:{InstanceId}] Created thread: {Path}", _instanceId, threadPath);

                    // Save existing messages as child nodes
                    foreach (var msg in messages)
                    {
                        var messageType = msg.Role.Value.Equals("user", StringComparison.OrdinalIgnoreCase)
                            ? ThreadMessageType.ExecutedInput
                            : ThreadMessageType.AgentResponse;
                        await SaveMessageAsChildNodeAsync(msg, messageType);
                    }

                    await InvokeAsync(StateHasChanged);
                }
                else if (task.IsFaulted)
                {
                    Logger.LogWarning(task.Exception, "[ThreadChat:{InstanceId}] Failed to create thread", _instanceId);
                    await InvokeAsync(StateHasChanged);
                }
            });
    }

    private string GetCurrentUserId()
    {
        var accessService = Hub.ServiceProvider.GetService<AccessService>();
        return accessService?.Context?.ObjectId ?? "anonymous";
    }

    private void OnMessageTextChanged(string? newText)
    {
        MessageText = newText;
        UpdateExtractedReferences();
    }

    private void UpdateExtractedReferences()
    {
        var currentRefs = MarkdownReferenceExtractor.GetUniquePaths(MessageText);

        // Remove stale reference attachments (not in current refs, and not context)
        attachments.RemoveAll(a => !a.IsContext && !currentRefs.Contains(a.Path, StringComparer.OrdinalIgnoreCase));

        // Add new reference attachments (dedup by path against existing)
        var existingPaths = attachments.Select(a => a.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var refPath in currentRefs)
        {
            if (!existingPaths.Contains(refPath))
            {
                attachments.Add(new AttachmentInfo(refPath, DisplayName: null, IsContext: false));
            }
        }

        StateHasChanged();
    }

    private async Task OnAttachmentRemoved(string path)
    {
        var attachment = attachments.FirstOrDefault(a => a.Path.Equals(path, StringComparison.OrdinalIgnoreCase));
        if (attachment == null)
            return;

        attachments.Remove(attachment);

        if (attachment.IsContext)
        {
            // Clear context backing field so it's not sent to the agent
            initialContext = null;
        }
        else if (!string.IsNullOrEmpty(MessageText))
        {
            // Remove @reference from message text
            var updatedMarkdown = MarkdownReferenceExtractor.RemoveReferenceByPath(MessageText, path);
            MessageText = updatedMarkdown;

            if (monacoEditor != null)
            {
                await monacoEditor.SetValueAsync(updatedMarkdown);
            }
        }

        StateHasChanged();
    }

    private void OnChipClicked(string path)
    {
        // Navigate to the referenced node
        NavigationManager.NavigateTo($"/{path}");
    }

    private void OnAgentChanged(AgentDisplayInfo? newAgent)
    {
        if (newAgent == null || newAgent.Name == selectedAgentInfo?.Name)
            return;

        selectedAgentInfo = newAgent;

        // Set the selected agent on the current chat instance
        chat?.SetSelectedAgent(newAgent.Name);

        var preferredModel = GetPreferredModelInfoForAgent(newAgent.Name);
        if (preferredModel != null && preferredModel.Name != selectedModelInfo?.Name)
        {
            selectedModelInfo = preferredModel;
            _ = ReinstantiateAgentAsync();
        }

        StateHasChanged();
    }

    private void OnModelChanged(ModelInfo? newModel)
    {
        if (newModel?.Name == selectedModelInfo?.Name || newModel == null)
            return;

        selectedModelInfo = newModel;

        if (selectedAgentInfo != null)
        {
            agentModelPreferences[selectedAgentInfo.Name] = newModel.Name;
        }

        _ = ReinstantiateAgentAsync();
        StateHasChanged();
    }

    private async Task ReinstantiateAgentAsync()
    {
        chat = await CreateChatAsync();
    }

    private async Task SendMessageAsync()
    {
        if (string.IsNullOrWhiteSpace(MessageText) || chat == null || _isDisposed)
            return;

        CancelAnyCurrentResponse();

        // Check for context change before sending
        await CheckAndUpdateContextAsync();

        var userMessageText = MessageText;
        MessageText = null;

        if (monacoEditor != null)
        {
            await monacoEditor.ClearAsync();
        }

        UpdateExtractedReferences();

        var userMessage = new ChatMessage(ChatRole.User, userMessageText);
        messages.Add(userMessage);

        // Create thread lazily on first message if no threadPath
        var isNewThread = string.IsNullOrEmpty(threadPath);
        if (isNewThread)
        {
            CreateThreadAsync();
        }
        else
        {
            // Save user message as child node (for existing threads)
            await SaveMessageAsChildNodeAsync(userMessage, ThreadMessageType.ExecutedInput);
        }

        currentResponseCancellation = new CancellationTokenSource();
        isGeneratingResponse = true;
        StateHasChanged();

        try
        {
            // Pass current attachments to the chat client
            chat.SetAttachments(attachments.Select(a => a.Path).ToList());

            var lastRole = "Assistant";
            var responseText = new TextContent(string.Empty);
            currentResponseMessage = new ChatMessage(new ChatRole(lastRole), [responseText]);

            await foreach (var update in chat.GetStreamingResponseAsync([userMessage], currentResponseCancellation.Token))
            {
                var currentAuthor = update.AuthorName ?? "Assistant";

                // Check for non-text content (layout areas, delegations, function calls)
                var nonTextContents = update.Contents
                    .Where(c => c is ChatLayoutAreaContent or ChatDelegationContent or FunctionCallContent)
                    .ToList();

                if (nonTextContents.Count > 0)
                {
                    // Flush any accumulated text message before inserting non-text content
                    if (!string.IsNullOrWhiteSpace(currentResponseMessage?.Text))
                    {
                        messages.Add(currentResponseMessage!);
                        await SaveMessageAsChildNodeAsync(currentResponseMessage!, ThreadMessageType.AgentResponse);
                    }

                    // Add each non-text content as its own message
                    foreach (var content in nonTextContents)
                    {
                        var contentMessage = new ChatMessage(new ChatRole(currentAuthor), [content])
                        {
                            AuthorName = currentAuthor
                        };
                        messages.Add(contentMessage);
                        // Do NOT save non-text content as child nodes (non-serializable)
                    }

                    // Reset text accumulator for subsequent text
                    lastRole = currentAuthor;
                    responseText = new TextContent(string.Empty);
                    currentResponseMessage = new ChatMessage(new ChatRole(lastRole), [responseText])
                    {
                        AuthorName = currentAuthor
                    };
                }
                else if (lastRole == currentAuthor)
                {
                    responseText.Text += update.Text;
                }
                else
                {
                    if (!string.IsNullOrWhiteSpace(currentResponseMessage?.Text))
                    {
                        messages.Add(currentResponseMessage!);
                        await SaveMessageAsChildNodeAsync(currentResponseMessage!, ThreadMessageType.AgentResponse);
                    }

                    lastRole = currentAuthor;
                    responseText = new TextContent(update.Text);
                    currentResponseMessage = new ChatMessage(new ChatRole(lastRole), [responseText])
                    {
                        AuthorName = currentAuthor
                    };
                }

                if (currentResponseMessage != null)
                {
                    ChatMessageItem.NotifyChanged(currentResponseMessage);
                }

                StateHasChanged();
            }
        }
        catch (OperationCanceledException)
        {
            Logger.LogDebug("[ThreadChat:{InstanceId}] Response cancelled", _instanceId);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "[ThreadChat:{InstanceId}] Error during streaming", _instanceId);

            if (!string.IsNullOrWhiteSpace(currentResponseMessage?.Text))
            {
                messages.Add(currentResponseMessage!);
                await SaveMessageAsChildNodeAsync(currentResponseMessage!, ThreadMessageType.AgentResponse);
            }

            var errorText = new TextContent($"An error occurred: {ex.Message}");
            currentResponseMessage = new ChatMessage(ChatRole.Assistant, [errorText]);
            ChatMessageItem.NotifyChanged(currentResponseMessage);
        }
        finally
        {
            if (!string.IsNullOrWhiteSpace(currentResponseMessage?.Text))
            {
                messages.Add(currentResponseMessage!);
                // Save final agent response as child node
                await SaveMessageAsChildNodeAsync(currentResponseMessage!, ThreadMessageType.AgentResponse);
                currentResponseMessage = null;
                await SaveThreadAsync();
            }

            isGeneratingResponse = false;
            StateHasChanged();
        }
    }

    private void CancelAnyCurrentResponse()
    {
        if (currentResponseMessage != null)
        {
            messages.Add(currentResponseMessage);
        }

        currentResponseCancellation.Cancel();
        currentResponseCancellation = new CancellationTokenSource();
        currentResponseMessage = null;
        isGeneratingResponse = false;
        StateHasChanged();
    }

    private Task CancelCurrentResponse()
    {
        CancelAnyCurrentResponse();
        return Task.CompletedTask;
    }

    private Task OnNewMessageReceived(ChatMessage message)
    {
        return Task.CompletedTask;
    }

    private CompletionProviderConfig GetCompletionConfig()
    {
        return new CompletionProviderConfig
        {
            TriggerCharacters = ["@"],
            Items = []
        };
    }

    private async Task<CompletionItem[]> GetCompletionsAsync(string query)
    {
        try
        {
            var meshCatalog = Hub.ServiceProvider.GetService<IMeshCatalog>();

            var client = new AutocompleteClient(
                Hub,
                _ => [AI.Application.ApplicationAddress.Agents]);

            var contextPath = attachments.FirstOrDefault(a => a.IsContext)?.Path ?? initialContext;
            var agentContext = new AgentContext
            {
                Address = contextPath != null ? new MeshWeaver.Messaging.Address(contextPath) : null
            };

            var response = await client.GetCompletionsAsync(query, agentContext);

            var fuzzyScorer = new FuzzyScorer();
            var scored = fuzzyScorer.Score(response.Items, query, i => i.Label);

            return scored
                .OrderByDescending(s => s.Item.Priority)
                .ThenByDescending(s => s.Score)
                .Take(20)
                .Select(s => new CompletionItem
                {
                    Label = s.Item.Label,
                    InsertText = s.Item.InsertText,
                    Description = s.Item.Description,
                    Category = s.Item.Category,
                    Path = s.Item.InsertText,
                    Kind = MapAutocompleteKindToCompletionKind(s.Item.Kind)
                })
                .ToArray();
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error getting completions for query: {Query}", query);
            return [];
        }
    }

    private static CompletionItemKind MapAutocompleteKindToCompletionKind(AutocompleteKind kind) => kind switch
    {
        AutocompleteKind.Agent => CompletionItemKind.Module,
        AutocompleteKind.File => CompletionItemKind.File,
        AutocompleteKind.Command => CompletionItemKind.Function,
        _ => CompletionItemKind.Text
    };

    private static string TruncateText(string text, int maxLength)
    {
        if (string.IsNullOrEmpty(text) || text.Length <= maxLength)
            return text;
        return text[..maxLength] + "...";
    }

    public override ValueTask DisposeAsync()
    {
        if (!_isDisposed)
        {
            _isDisposed = true;
            currentResponseCancellation.Cancel();
        }

        return base.DisposeAsync();
    }
}
