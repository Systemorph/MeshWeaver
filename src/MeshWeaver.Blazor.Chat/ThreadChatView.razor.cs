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
    [Inject] private ChatWindowStateService ChatWindowState { get; set; } = null!;

    private bool _isDisposed;
    private readonly string _instanceId = Guid.NewGuid().ToString("N")[..8];

    // Thread state
    private string? threadPath;
    private string? initialContext;
    private string? initialContextDisplayName;
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

    // Reference extraction
    private IReadOnlyList<string> extractedReferences = Array.Empty<string>();
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
                initialContext = path;
                initialContextDisplayName = await ResolveContextDisplayNameAsync(path);
            }
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

    protected override void BindData()
    {
        base.BindData();
        DataBind(ViewModel.ThreadPath, x => x.threadPath);
        DataBind(ViewModel.InitialContext, x => x.initialContext);
        DataBind(ViewModel.InitialContextDisplayName, x => x.initialContextDisplayName);
    }

    private async Task InitializeAgentAndModelSelectionsAsync()
    {
        // Get available models from all factories
        availableModels = ChatClientFactories
            .OrderBy(f => f.DisplayOrder)
            .SelectMany(f => f.Models.Select(m => new ModelInfo
            {
                Name = m,
                Provider = f.Name,
                DisplayOrder = f.DisplayOrder
            }))
            .ToList();

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
        var chatClient = new AgentChatClient(Hub.ServiceProvider);
        await chatClient.InitializeAsync(initialContext, model);
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
            var node = meshCatalog != null ? await meshCatalog.GetNodeAsync(new Address(threadPath)) : null;
            var threadContent = node?.Content as MeshThread;

            if (threadContent != null)
            {
                messages.Clear();
                var loadedMessages = threadContent.ToChatMessages();
                foreach (var msg in loadedMessages)
                {
                    messages.Add(msg);
                }

                // Resume chat with loaded messages
                if (chat is AgentChatClient agentChatClient)
                {
                    var conversation = new ChatConversation
                    {
                        Id = threadPath,
                        Title = node?.Name ?? initialContextDisplayName ?? "Thread",
                        CreatedAt = DateTime.UtcNow,
                        LastModifiedAt = DateTime.UtcNow,
                        Messages = messages.ToList()
                    };
                    await agentChatClient.ResumeAsync(conversation);
                }

                chat?.SetThreadId(threadPath);
                Logger.LogDebug("[ThreadChat:{InstanceId}] Loaded thread: {Path}", _instanceId, threadPath);
            }
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

    private async Task SaveThreadAsync()
    {
        if (!messages.Any() || string.IsNullOrEmpty(threadPath))
            return;

        try
        {
            // Load existing node to preserve metadata and ParentPath
            var meshCatalog = Hub.ServiceProvider.GetService<IMeshCatalog>();
            var existingNode = meshCatalog != null ? await meshCatalog.GetNodeAsync(new Address(threadPath)) : null;
            var existingContent = existingNode?.Content as MeshThread;

            // Create thread content from messages, preserving the ParentPath
            var parentPath = existingContent?.ParentPath ?? initialContext;
            var threadContent = MeshThread.FromChatMessages(messages, parentPath);

            var updatedNode = existingNode != null
                ? existingNode with { Content = threadContent }
                : new MeshNode(threadPath) { NodeType = ThreadNodeType.NodeType, Content = threadContent };

            // Update via DataChangeRequest
            var nodeJson = JsonSerializer.SerializeToElement(updatedNode, Hub.JsonSerializerOptions);
            Hub.Post(new DataChangeRequest { Updates = [nodeJson] }, o => o.WithTarget(new Address(threadPath)));

            Logger.LogDebug("[ThreadChat:{InstanceId}] Saved thread: {Path}", _instanceId, threadPath);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "[ThreadChat:{InstanceId}] Error saving thread", _instanceId);
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
    /// Checks if the navigation context has changed since the last message and updates the context if needed.
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
                // Only update if path is actually different from current context
                if (newPath != initialContext)
                {
                    Logger.LogDebug("[ThreadChat:{InstanceId}] Context changed from {OldContext} to {NewContext}",
                        _instanceId, initialContext, newPath);

                    initialContext = newPath;
                    initialContextDisplayName = await ResolveContextDisplayNameAsync(newPath);
                    StateHasChanged();
                }
            }

            lastContextUrl = currentUrl;
        }
    }

    /// <summary>
    /// Creates a new thread lazily on first message.
    /// Thread is created under {parentPath}/Threads/{threadId}
    /// </summary>
    private async Task CreateThreadAsync()
    {
        try
        {
            var threadId = Guid.NewGuid().AsString();
            var parentPath = initialContext;

            // Construct the thread path: {parentPath}/Threads/{threadId}
            var threadNamespace = string.IsNullOrEmpty(parentPath) ? "Threads" : $"{parentPath}/Threads";
            threadPath = $"{threadNamespace}/{threadId}";

            // Generate title from first user message
            var title = GetThreadTitle() ?? $"Chat {DateTime.UtcNow:yyyy-MM-dd HH:mm}";

            // Create thread content
            var threadContent = MeshThread.FromChatMessages(messages, parentPath);

            var newNode = new MeshNode(threadPath)
            {
                Name = title,
                NodeType = ThreadNodeType.NodeType,
                Content = threadContent
            };

            var request = new CreateNodeRequest(newNode) { CreatedBy = GetCurrentUserId() };
            var response = await Hub.AwaitResponse(request, o => o.WithTarget(Hub.Address));

            if (response.Message.Success && response.Message.Node != null)
            {
                threadPath = response.Message.Node.Path;
                chat?.SetThreadId(threadPath);

                // Sync with ChatWindowStateService
                ChatWindowState.SetCurrentThread(threadPath);

                Logger.LogDebug("[ThreadChat:{InstanceId}] Created thread: {Path}", _instanceId, threadPath);
            }
            else
            {
                Logger.LogWarning("[ThreadChat:{InstanceId}] Failed to create thread: {Error}", _instanceId, response.Message.Error);
            }
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "[ThreadChat:{InstanceId}] Error creating thread", _instanceId);
        }
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
        extractedReferences = MarkdownReferenceExtractor.GetUniquePaths(MessageText);
        StateHasChanged();
    }

    private async Task OnReferenceRemoved(string reference)
    {
        if (string.IsNullOrEmpty(MessageText))
            return;

        var updatedMarkdown = MarkdownReferenceExtractor.RemoveReferenceByPath(MessageText, reference);
        MessageText = updatedMarkdown;

        if (monacoEditor != null)
        {
            await monacoEditor.SetValueAsync(updatedMarkdown);
        }

        UpdateExtractedReferences();
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
        if (string.IsNullOrEmpty(threadPath))
        {
            await CreateThreadAsync();
        }

        currentResponseCancellation = new CancellationTokenSource();
        isGeneratingResponse = true;
        StateHasChanged();

        try
        {
            var lastRole = "Assistant";
            var responseText = new TextContent(string.Empty);
            currentResponseMessage = new ChatMessage(new ChatRole(lastRole), [responseText]);

            await foreach (var update in chat.GetStreamingResponseAsync([userMessage], currentResponseCancellation.Token))
            {
                var currentAuthor = update.AuthorName ?? "Assistant";

                if (lastRole == currentAuthor)
                {
                    responseText.Text += update.Text;
                }
                else
                {
                    if (!string.IsNullOrWhiteSpace(currentResponseMessage?.Text))
                    {
                        messages.Add(currentResponseMessage!);
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
                messages.Add(currentResponseMessage!);

            var errorText = new TextContent($"An error occurred: {ex.Message}");
            currentResponseMessage = new ChatMessage(ChatRole.Assistant, [errorText]);
            ChatMessageItem.NotifyChanged(currentResponseMessage);
        }
        finally
        {
            if (!string.IsNullOrWhiteSpace(currentResponseMessage?.Text))
            {
                messages.Add(currentResponseMessage!);
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

            var agentContext = new AgentContext
            {
                Address = initialContext != null ? new MeshWeaver.Messaging.Address(initialContext) : null
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
