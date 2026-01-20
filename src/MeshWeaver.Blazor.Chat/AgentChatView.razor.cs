using MeshWeaver.AI;
using MeshWeaver.AI.Commands;
using MeshWeaver.AI.Completion;
using MeshWeaver.AI.Parsing;
using MeshWeaver.AI.Persistence;
using MeshWeaver.Blazor.Components.Monaco;
using MeshWeaver.Data;
using MeshWeaver.Data.Completion;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Activity;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using MeshWeaver.ShortGuid;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TextContent = Microsoft.Extensions.AI.TextContent;

namespace MeshWeaver.Blazor.Chat;

public enum ChatPosition
{
    Right,
    Left,
    Bottom
}

public partial class AgentChatView : BlazorView<AgentChatControl, AgentChatView>
{
    private bool _isDisposed;
    private readonly string _instanceId = Guid.NewGuid().ToString("N")[..8];
    private Lazy<Task<IAgentChat>>? lazyChat;
    private CancellationTokenSource currentResponseCancellation = new();
    private ChatMessage? currentResponseMessage;
    private ChatInput? chatInput;
    private ChatHistorySelector? chatHistorySelector;
    private readonly List<ChatMessage> messages = new();
    // Chat persistence properties
    private string? currentConversationId;
    private ChatConversation? currentConversation;
    private bool isLoadingConversation;
    private bool isGeneratingResponse;
    private IAgentChatFactoryProvider AgentChatFactoryProvider => Hub.ServiceProvider.GetRequiredService<IAgentChatFactoryProvider>();
    // Bound context from the control
    private readonly AgentContext? boundContext;
    // Track last navigation context for agent reselection
    private AgentContext? lastNavigationContext;

    // Bound title from the control
    private readonly string chatTitle = "AI Chat";

    [Parameter] public bool UseStreaming { get; set; } = true;
    // Chat history panel state
    private bool showChatHistory;

    // Chat position state
    private ChatPosition currentPosition = ChatPosition.Right;
    private bool positionMenuVisible = false;
    [Parameter] public EventCallback<ChatPosition> OnPositionChanged { get; set; }
    [Parameter] public EventCallback<ChatMessage> OnMessageAdded { get; set; }

    // Agent, model, and context selection state
    private AgentDisplayInfo? selectedAgentInfo;
    private string? selectedModel;
    private string? selectedContextPath;
    private string? selectedContextDisplayName;
    private IReadOnlyList<AgentDisplayInfo> agentDisplayInfos = [];
    private IReadOnlyList<string> availableModels = [];
    private bool pendingModelChange;

    // Pre-parser and command system
    private readonly ChatPreParser chatPreParser = new();
    private ChatCommandRegistry? commandRegistry;

    private async Task OnNewMessageReceived(ChatMessage message)
    {
        // Handle new message events (e.g., auto-scroll, notifications, analytics)
        if (OnMessageAdded.HasDelegate)
        {
            await OnMessageAdded.InvokeAsync(message);
        }
    }


    protected override async Task OnInitializedAsync()
    {
        Logger.LogDebug("[Chat:{InstanceId}] OnInitializedAsync started", _instanceId);

        // Initialize lazyChat now that Hub is available
        lazyChat = GetLazyChat();
        Logger.LogInformation("[Chat:{InstanceId}] lazyChat initialized", _instanceId);

        // Subscribe to navigation changes to update agent selection
        NavigationManager.LocationChanged += OnLocationChanged;
        Logger.LogInformation("[Chat:{InstanceId}] Subscribed to NavigationManager.LocationChanged", _instanceId);

        // Initialize agent and model selections
        await InitializeAgentAndModelSelectionsAsync();
        Logger.LogInformation("[Chat:{InstanceId}] Agent and model selections initialized", _instanceId);

        // Store the initial navigation context and path
        lastNavigationContext = GetCurrentAgentContext();
        var initialPath = NavigationManager.ToBaseRelativePath(NavigationManager.Uri);
        selectedContextPath = string.IsNullOrEmpty(initialPath) ? null : initialPath;
        selectedContextDisplayName = await ResolveContextDisplayNameAsync(selectedContextPath);

        // Initialize command system
        InitializeCommands();

        // Try to load the most recent conversation on startup
        try
        {
            var conversations = await ChatPersistenceService.GetConversationsAsync();
            var mostRecent = conversations.OrderByDescending(c => c.LastModifiedAt).FirstOrDefault();

            if (mostRecent != null)
            {
                await LoadConversation(mostRecent.Id);
            }
            else
            {
                await StartNewConversationAsync();
            }
        }
        catch (Exception ex)
        {
            // If there's an error loading conversations, start fresh
            Logger.LogWarning(ex, "[Chat:{InstanceId}] Error loading conversations, starting fresh", _instanceId);
            try
            {
                await StartNewConversationAsync();
            }
            catch (ArgumentException argEx) when (argEx.Message.Contains("No factory can serve model"))
            {
                // Handle missing model configuration
                Logger.LogError(argEx, "[Chat:{InstanceId}] No AI model factory configured", _instanceId);
                ShowConfigurationError("No AI model is configured. Please configure an AI service provider (e.g., OpenAI, Azure OpenAI, or Anthropic) in your application settings.");
            }
            catch (Exception initEx)
            {
                // Handle other initialization errors
                Logger.LogError(initEx, "[Chat:{InstanceId}] Failed to initialize chat", _instanceId);
                ShowConfigurationError($"Failed to initialize chat: {initEx.Message}");
            }
        }

        Logger.LogInformation("[Chat:{InstanceId}] OnInitializedAsync completed. IsDisposed={IsDisposed}", _instanceId, _isDisposed);
    }

    private async Task InitializeAgentAndModelSelectionsAsync()
    {
        // Get the initial context path from URL
        var initialPath = NavigationManager.ToBaseRelativePath(NavigationManager.Uri);
        var contextPath = string.IsNullOrEmpty(initialPath) ? null : initialPath;

        // Get available agents with display info for the current context (searches namespace hierarchy)
        agentDisplayInfos = await AgentChatFactoryProvider.GetAgentsWithDisplayInfoAsync(contextPath);

        // Get available models (union from all factories, sorted by factory order)
        availableModels = AgentChatFactoryProvider.AllModels;

        // Select agent using IMeshQuery-based hierarchical search
        var context = GetCurrentAgentContext();
        selectedAgentInfo = await FindClosestDefaultAgentAsync(contextPath, context);

        // Set initial model based on agent preference or default
        if (selectedAgentInfo != null)
        {
            selectedModel = AgentChatFactoryProvider.GetPreferredModelForAgent(selectedAgentInfo.Name);
        }
        else
        {
            selectedModel = availableModels.FirstOrDefault();
        }

        StateHasChanged();
    }

    /// <summary>
    /// Handles navigation changes to update context display and agent selection.
    /// When navigating to a new node, selects the best agent for that context.
    /// </summary>
    private async void OnLocationChanged(object? sender, Microsoft.AspNetCore.Components.Routing.LocationChangedEventArgs e)
    {
        Logger.LogInformation("[Chat:{InstanceId}] OnLocationChanged fired. IsDisposed={IsDisposed}, NewUrl={Url}",
            _instanceId, _isDisposed, e.Location);

        if (_isDisposed)
        {
            Logger.LogDebug("[Chat:{InstanceId}] OnLocationChanged called but component is disposed!", _instanceId);
            return;
        }

        try
        {
            await InvokeAsync(async () =>
            {
                Logger.LogDebug("[Chat:{InstanceId}] Inside InvokeAsync callback", _instanceId);

                var newContext = GetCurrentAgentContext();

                // Check if the context has actually changed (compare address and layout area)
                var contextChanged = !ContextsAreEqual(lastNavigationContext, newContext);

                Logger.LogDebug("[Chat:{InstanceId}] Context changed: {Changed}", _instanceId, contextChanged);

                if (contextChanged)
                {
                    Logger.LogInformation("[Chat:{InstanceId}] Navigation context changed from {OldContext} to {NewContext}",
                        _instanceId,
                        lastNavigationContext?.Address?.ToString() ?? "null",
                        newContext?.Address?.ToString() ?? "null");

                    lastNavigationContext = newContext;

                    // Update the context path
                    var newPath = NavigationManager.ToBaseRelativePath(NavigationManager.Uri);
                    if (newPath != selectedContextPath)
                    {
                        selectedContextPath = string.IsNullOrEmpty(newPath) ? null : newPath;
                        selectedContextDisplayName = await ResolveContextDisplayNameAsync(selectedContextPath);
                    }

                    // Update agent selection for the new context
                    // This finds the best agent based on context pattern matching,
                    // node type namespace, or default agent for the hierarchy
                    await UpdateAgentSelectionForContextAsync(newContext);

                    StateHasChanged();
                }
            });
        }
        catch (ObjectDisposedException)
        {
            Logger.LogDebug("[Chat:{InstanceId}] ObjectDisposedException in OnLocationChanged - component was disposed", _instanceId);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "[Chat:{InstanceId}] Error handling navigation change in chat", _instanceId);
        }
    }

    /// <summary>
    /// Compares two AgentContext instances for equality.
    /// </summary>
    private static bool ContextsAreEqual(AgentContext? a, AgentContext? b)
    {
        if (a == null && b == null) return true;
        if (a == null || b == null) return false;

        var addressEqual = a.Address?.ToString() == b.Address?.ToString();
        var layoutAreaEqual = a.LayoutArea?.Area == b.LayoutArea?.Area;

        return addressEqual && layoutAreaEqual;
    }

    /// <summary>
    /// Initializes the chat command system.
    /// </summary>
    private void InitializeCommands()
    {
        commandRegistry = new ChatCommandRegistry(Logger);
        commandRegistry.Register(new AgentCommand());
        commandRegistry.Register(new ModelCommand());
        commandRegistry.Register(new HelpCommand());
    }

    /// <summary>
    /// Selects an agent based on the current context using ContextMatchPattern.
    /// </summary>
    private AgentDisplayInfo? SelectAgentByContext()
    {
        var context = GetCurrentAgentContext();
        if (context == null)
            return null;

        // Find the first agent with a ContextMatchPattern that could match the context
        foreach (var agentInfo in agentDisplayInfos)
        {
            var pattern = agentInfo.AgentConfiguration.ContextMatchPattern;
            if (string.IsNullOrEmpty(pattern))
                continue;

            // Simple pattern matching: check if address type matches the pattern
            // Pattern examples: "address.type==pricing", "address=like=*Todo*"
            if (MatchesContextPattern(pattern, context))
            {
                Logger.LogDebug("Context-based agent selection: {AgentName} matches context {Context}",
                    agentInfo.Name, context.Address);
                return agentInfo;
            }
        }

        return null;
    }

    /// <summary>
    /// Simple RSQL-like pattern matching for context selection.
    /// </summary>
    private static bool MatchesContextPattern(string pattern, AgentContext context)
    {
        // Handle address.type==value patterns
        if (pattern.StartsWith("address.type=="))
        {
            var expectedType = pattern["address.type==".Length..];
            return context.Address?.Type?.Equals(expectedType, StringComparison.OrdinalIgnoreCase) == true;
        }

        // Handle address=like=*value* patterns
        if (pattern.StartsWith("address=like="))
        {
            var likePattern = pattern["address=like=".Length..].Trim('*');
            var addressString = context.Address?.ToString() ?? string.Empty;
            return addressString.Contains(likePattern, StringComparison.OrdinalIgnoreCase);
        }

        return false;
    }

    /// <summary>
    /// Finds the closest default agent for the given context path.
    /// Search order:
    /// 1. Agent with ContextMatchPattern matching the current context
    /// 2. Default agent from agentDisplayInfos (which already includes NodeType namespace and path hierarchy)
    /// 3. Closest agent by path depth
    /// 4. First available agent
    /// </summary>
    private Task<AgentDisplayInfo?> FindClosestDefaultAgentAsync(string? contextPath, AgentContext? context)
    {
        // 1. Try to find an agent matching by ContextMatchPattern
        if (context != null)
        {
            var matchingAgent = SelectAgentByContext();
            if (matchingAgent != null)
            {
                Logger.LogInformation("[Chat:{InstanceId}] Selected agent by context pattern: {Agent}", _instanceId, matchingAgent.Name);
                return Task.FromResult<AgentDisplayInfo?>(matchingAgent);
            }
        }

        // 2. Find default agent from the already-loaded agent list
        // The agentDisplayInfos is populated from GetAgentsWithPathsAsync which searches
        // both NodeType namespace and path namespace using scope:myselfAndAncestors
        var defaultAgent = agentDisplayInfos
            .Where(a => a.AgentConfiguration.IsDefault)
            .OrderByDescending(a => a.Path?.Split('/').Length ?? 0)  // Closest (longest path) wins
            .FirstOrDefault();

        if (defaultAgent != null)
        {
            Logger.LogInformation("[Chat:{InstanceId}] Found closest default agent: {Agent} at path {Path}",
                _instanceId, defaultAgent.Name, defaultAgent.Path);
            return Task.FromResult<AgentDisplayInfo?>(defaultAgent);
        }

        // 3. Find closest agent by path depth (even if not marked as default)
        var closestAgent = agentDisplayInfos
            .OrderByDescending(a => a.Path?.Split('/').Length ?? 0)
            .FirstOrDefault();

        if (closestAgent != null)
        {
            Logger.LogInformation("[Chat:{InstanceId}] Falling back to closest agent: {Agent} at path {Path}",
                _instanceId, closestAgent.Name, closestAgent.Path);
            return Task.FromResult<AgentDisplayInfo?>(closestAgent);
        }

        // 4. Fall back to first available agent
        var fallbackAgent = agentDisplayInfos.FirstOrDefault();
        if (fallbackAgent != null)
        {
            Logger.LogInformation("[Chat:{InstanceId}] Falling back to first available agent: {Agent}", _instanceId, fallbackAgent.Name);
        }
        return Task.FromResult(fallbackAgent);
    }

    /// <summary>
    /// Updates agent selection based on the current navigation context.
    /// Uses IMeshQuery to find the closest default agent in the namespace hierarchy.
    /// </summary>
    private async Task UpdateAgentSelectionForContextAsync(AgentContext? context)
    {
        var contextPath = selectedContextPath;

        Logger.LogDebug("[Chat:{InstanceId}] Updating agent selection for context path: {ContextPath}",
            _instanceId, contextPath);

        // Refresh agent list for the new context path (searches namespace hierarchy)
        agentDisplayInfos = await AgentChatFactoryProvider.GetAgentsWithDisplayInfoAsync(contextPath);

        Logger.LogDebug("[Chat:{InstanceId}] Found {Count} agents for context",
            _instanceId, agentDisplayInfos.Count);

        // Find the closest default agent using IMeshQuery
        var newAgent = await FindClosestDefaultAgentAsync(contextPath, context);

        // Update selection if changed
        if (newAgent != null && newAgent.Name != selectedAgentInfo?.Name)
        {
            selectedAgentInfo = newAgent;
            selectedModel = AgentChatFactoryProvider.GetPreferredModelForAgent(newAgent.Name);

            Logger.LogInformation("[Chat:{InstanceId}] Agent changed to {Agent} with model {Model}",
                _instanceId, newAgent.Name, selectedModel);

            // Reinstantiate the agent with the new selection
            ScheduleAgentReinstantiation();
        }
        else if (newAgent == null)
        {
            Logger.LogWarning("[Chat:{InstanceId}] No agents available for context path: {ContextPath}",
                _instanceId, contextPath);
        }
    }

    private void OnAgentInfoChanged(AgentDisplayInfo? newAgentInfo)
    {
        if (newAgentInfo == null || newAgentInfo.Name == selectedAgentInfo?.Name)
            return;

        selectedAgentInfo = newAgentInfo;

        // Update model to agent's preferred model
        var preferredModel = AgentChatFactoryProvider.GetPreferredModelForAgent(newAgentInfo.Name);
        if (!string.IsNullOrEmpty(preferredModel) && preferredModel != selectedModel)
        {
            selectedModel = preferredModel;
            ScheduleAgentReinstantiation();
        }

        StateHasChanged();
    }

    private void OnModelChanged(string? newModel)
    {
        if (newModel == selectedModel || string.IsNullOrEmpty(newModel))
            return;

        selectedModel = newModel;

        // Update the agent's model preference
        if (selectedAgentInfo != null)
        {
            AgentChatFactoryProvider.SetModelPreferenceForAgent(selectedAgentInfo.Name, newModel);
        }

        ScheduleAgentReinstantiation();
        StateHasChanged();
    }

    private async Task OnContextPathChanged(string? newContext)
    {
        Logger.LogDebug("[Chat:{InstanceId}] OnContextPathChanged called with: {NewContext}, current: {Current}",
            _instanceId, newContext, selectedContextPath);

        if (newContext == selectedContextPath)
        {
            Logger.LogDebug("[Chat:{InstanceId}] Context unchanged, skipping", _instanceId);
            return;
        }

        selectedContextPath = newContext;
        selectedContextDisplayName = await ResolveContextDisplayNameAsync(newContext);

        Logger.LogDebug("[Chat:{InstanceId}] Context changed to: {Context} (display: {Display}) - NOT reinstantiating agent to preserve chat",
            _instanceId, newContext, selectedContextDisplayName);

        // NOTE: Do NOT call ScheduleAgentReinstantiation here!
        // The chat should persist across context changes. The context is just metadata.
        // The user can manually change agents if needed.
        StateHasChanged();
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

    private async Task<CompletionItem[]> GetContextAutocompleteAsync(string query)
    {
        try
        {
            // Strip @ prefix if present (from UCR-style autocomplete)
            var searchPrefix = query?.TrimStart('@') ?? "";

            // If no query, return recent items from user activity
            if (string.IsNullOrWhiteSpace(searchPrefix))
            {
                return await GetRecentContextItemsAsync();
            }

            var meshQuery = Hub.ServiceProvider.GetService<IMeshQuery>();
            if (meshQuery == null)
            {
                Logger.LogDebug("IMeshQuery not available for context autocomplete");
                return [];
            }

            // Use AutocompleteAsync with RelevanceFirst mode for context selection
            // This orders by: name matches first, then path matches, then other matches
            var suggestions = await meshQuery.AutocompleteAsync(
                basePath: "",
                prefix: searchPrefix,
                mode: AutocompleteMode.RelevanceFirst,
                limit: 15
            ).ToArrayAsync();

            return suggestions.Select(s => new CompletionItem
            {
                Label = s.Name,                          // Node name (line 1)
                InsertText = s.Path,                     // Full path to insert
                Path = s.Path,                           // Full path (line 2)
                Description = s.NodeType ?? "",          // Node type for detail pane
                Category = s.NodeType ?? ""
            }).ToArray();
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Error getting context autocomplete suggestions");
            return [];
        }
    }

    private async Task<CompletionItem[]> GetRecentContextItemsAsync()
    {
        try
        {
            var accessService = Hub.ServiceProvider.GetService<AccessService>();
            var persistence = Hub.ServiceProvider.GetService<IPersistenceService>();

            if (accessService?.Context == null || persistence == null)
            {
                Logger.LogDebug("AccessService or IPersistenceService not available for recent items");
                return [];
            }

            var userId = accessService.Context.ObjectId;
            if (string.IsNullOrEmpty(userId))
            {
                return [];
            }

            // Query activity records for this user
            var activityPath = $"_activity/{userId}";
            var recentItems = new List<(UserActivityRecord Record, DateTimeOffset LastAccess)>();

            await foreach (var obj in persistence.GetPartitionObjectsAsync(activityPath))
            {
                if (obj is UserActivityRecord record)
                {
                    recentItems.Add((record, record.LastAccessedAt));
                }
            }

            // Order by last accessed (most recent first) and take top 15
            return recentItems
                .OrderByDescending(x => x.LastAccess)
                .Take(15)
                .Select(x => new CompletionItem
                {
                    Label = x.Record.NodeName ?? x.Record.NodePath,  // Node name
                    InsertText = x.Record.NodePath,                   // Full path
                    Path = x.Record.NodePath,                         // Full path (line 2)
                    Description = x.Record.NodeType ?? "",            // Node type
                    Category = "Recent"
                })
                .ToArray();
        }
        catch (Exception ex)
        {
            Logger.LogDebug(ex, "Error getting recent context items");
            return [];
        }
    }

    private void ScheduleAgentReinstantiation()
    {
        // If currently generating a response, mark that we need to reinstantiate after it finishes
        if (isGeneratingResponse)
        {
            pendingModelChange = true;
        }
        else
        {
            // Immediately reinstantiate the agent with the new model
            ReinstantiateAgent();
        }
    }

    private void ReinstantiateAgent()
    {
        pendingModelChange = false;
        lazyChat = GetLazyChat();
    }


    protected override void BindData()
    {
        base.BindData();
        DataBind(ViewModel.Context, x => x.boundContext);
        DataBind(ViewModel.Title, x => x.chatTitle, defaultValue: "AI Chat");
    }

    private Lazy<Task<IAgentChat>> GetLazyChat()
    {
        // Use the selected model, or default if none selected
        var model = selectedModel ?? AgentChatFactoryProvider.AllModels.FirstOrDefault() ?? string.Empty;
        // Get context path for hierarchical agent resolution
        var contextPath = GetCurrentAgentContext()?.ToUnifiedPath();
        return new(() => AgentChatFactoryProvider.CreateAsync(model, contextPath));
    }

    // Public method to allow parent components to reset the conversation
    public async Task ResetConversationAsync()
    {
        await StartNewConversationAsync();
    }
    private async Task StartNewConversationAsync()
    {
        CancelAnyCurrentResponse();
        // Clear current state
        currentConversationId = null;
        currentConversation = null;
        messages.Clear();
        lazyChat = GetLazyChat();

        try
        {
            // Set a new thread ID for the new conversation
            var chat = await lazyChat.Value;
            chat.SetThreadId(Guid.NewGuid().AsString());
        }
        catch (ArgumentException ex) when (ex.Message.Contains("No factory can serve model"))
        {
            // Handle missing model configuration
            Logger.LogError(ex, "[Chat:{InstanceId}] No AI model factory configured", _instanceId);
            ShowConfigurationError("No AI model is configured. Please configure an AI service provider (e.g., OpenAI, Azure OpenAI, or Anthropic) in your application settings.");
        }
        catch (Exception ex)
        {
            // Handle other initialization errors
            Logger.LogError(ex, "[Chat:{InstanceId}] Failed to initialize chat", _instanceId);
            ShowConfigurationError($"Failed to initialize chat: {ex.Message}");
        }

        if (chatInput != null)
        {
            await chatInput.FocusAsync();
        }

        // Close chat history when starting new conversation
        showChatHistory = false;
        StateHasChanged();
    }

    private void ToggleChatHistory()
    {
        showChatHistory = !showChatHistory;
        StateHasChanged();
    }

    private void CloseChatHistory()
    {
        showChatHistory = false;
        StateHasChanged();
    }
    private async Task OnConversationSelectionChanged(string conversationId)
    {
        if (conversationId != currentConversationId)
        {
            if (string.IsNullOrEmpty(conversationId))
            {
                await StartNewConversationAsync();
            }
            else
            {
                await LoadConversation(conversationId);
            }

            // Close the chat history panel after selection
            showChatHistory = false;
            StateHasChanged();
        }
    }
    private async Task LoadConversation(string conversationId)
    {
        if (isLoadingConversation) return; try
        {
            isLoadingConversation = true;
            StateHasChanged(); // Show loading spinner immediately
            CancelAnyCurrentResponse();

            var conversation = await ChatPersistenceService.LoadConversationAsync(conversationId);
            if (conversation != null)
            {
                currentConversationId = conversationId;
                currentConversation = conversation;

                // Load messages from the conversation
                messages.Clear();
                foreach (var persistedMessage in conversation.Messages)
                {
                    messages.Add(persistedMessage);
                } // Restore AgentChat with proper conversation history using the new persistence method
                var restoredChat = await ChatPersistenceService.RestoreAgentChatAsync(conversationId);

                // Set the thread ID to match the conversation ID
                restoredChat.SetThreadId(conversationId);

                lazyChat = new Lazy<Task<IAgentChat>>(() => Task.FromResult(restoredChat));

                StateHasChanged();
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error loading conversation: {ex.Message}");
            await StartNewConversationAsync();
        }
        finally
        {
            isLoadingConversation = false;
            StateHasChanged();
        }
    }
    private async Task SaveCurrentConversation()
    {
        if (messages.Any())
        {

            try
            {
                if (currentConversation == null)
                {
                    AgentContext? agentContext = null;

                    // Try to extract AgentContext from current conversation if there are any messages
                    if (lazyChat?.IsValueCreated == true)
                    {
                        // Use async version to include MeshNode name in the saved context
                        agentContext = await GetCurrentAgentContextAsync();
                    }

                    currentConversation = new ChatConversation
                    {
                        Id = Guid.NewGuid().ToString(),
                        Messages = messages,
                        CreatedAt = DateTime.UtcNow,
                        LastModifiedAt = DateTime.UtcNow,
                        AgentContext = agentContext
                    };
                    currentConversationId = currentConversation.Id;
                }
                else
                {
                    AgentContext? agentContext = null;

                    // Try to extract AgentContext from current conversation if there are any messages
                    if (lazyChat?.IsValueCreated == true)
                    {
                        // Use async version to include MeshNode name in the saved context
                        agentContext = await GetCurrentAgentContextAsync();
                    }
                    currentConversation = currentConversation with
                    {
                        Messages = messages,
                        LastModifiedAt = DateTime.UtcNow,
                        AgentContext = agentContext
                    };
                }

                // Get the current AgentChat instance
                var agentChat = lazyChat?.IsValueCreated == true ? await lazyChat.Value : null;

                await ChatPersistenceService.SaveConversationAsync(currentConversation, agentChat);

                // Refresh the conversation list in the sidebar
                if (chatHistorySelector != null)
                {
                    await chatHistorySelector.RefreshConversations();
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error saving conversation: {ex.Message}");
            }
        }
    }
    private async Task AddUserMessageAsync(ChatMessage userMessage)
    {
        Logger.LogInformation("[Chat:{InstanceId}] AddUserMessageAsync called. IsDisposed={IsDisposed}, lazyChat={LazyChat}",
            _instanceId, _isDisposed, lazyChat != null ? "set" : "null");

        if (_isDisposed)
        {
            Logger.LogDebug("[Chat:{InstanceId}] AddUserMessageAsync called but component is disposed!", _instanceId);
            return;
        }

        // Cancel any existing response
        CancelAnyCurrentResponse();

        // Pre-parse the message for agent references and commands
        var messageText = ExtractTextFromChatMessage(userMessage);
        var parsed = chatPreParser.Parse(messageText);

        // Handle agent reference (set as current agent for this and future messages)
        if (!string.IsNullOrEmpty(parsed.AgentReference))
        {
            var agentInfo = agentDisplayInfos.FirstOrDefault(
                a => a.Name.Equals(parsed.AgentReference, StringComparison.OrdinalIgnoreCase));
            if (agentInfo != null)
            {
                OnAgentInfoChanged(agentInfo);
            }
        }

        // Handle model reference (set as current model for this and future messages)
        if (!string.IsNullOrEmpty(parsed.ModelReference))
        {
            var modelName = availableModels.FirstOrDefault(
                m => m.Equals(parsed.ModelReference, StringComparison.OrdinalIgnoreCase));
            if (modelName != null)
            {
                OnModelChanged(modelName);
            }
        }

        // Handle commands
        if (parsed.Command != null)
        {
            await HandleCommandAsync(parsed.Command, messageText);

            // If command doesn't send to AI, we're done
            if (!parsed.ShouldSendToAI)
            {
                // Re-enable the input and focus it
                if (chatInput != null)
                {
                    await chatInput.FocusAsync();
                }
                return;
            }
        }

        IAgentChat chat;
        try
        {
            Logger.LogDebug("[Chat:{InstanceId}] Getting chat from lazyChat...", _instanceId);
            if (lazyChat == null)
            {
                throw new InvalidOperationException("Chat service not initialized");
            }
            chat = await lazyChat.Value;
            Logger.LogDebug("[Chat:{InstanceId}] Got chat successfully", _instanceId);
        }
        catch (ArgumentException ex) when (ex.Message.Contains("No factory can serve model"))
        {
            // Handle missing model configuration
            var errorMessage = "⚠️ **Configuration Error**\n\n" +
                             "No AI model is configured. Please configure an AI service provider (e.g., OpenAI, Azure OpenAI, or Anthropic) in your application settings.\n\n" +
                             "Please contact your administrator to configure the AI service.";

            var errorResponseText = new TextContent(errorMessage);
            var errorResponseMessage = new ChatMessage(new ChatRole("Assistant"), [errorResponseText])
            {
                AuthorName = "System"
            };

            // Add the user message to show what they tried to ask
            messages.Add(userMessage);
            messages.Add(errorResponseMessage);

            StateHasChanged();
            return;
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("API key") || ex.Message.Contains("endpoint URL"))
        {
            // Handle configuration errors (missing API key, endpoint, etc.)
            var errorMessage = "⚠️ **Configuration Error**\n\n" +
                             "The AI service is not properly configured. Please check that:\n" +
                             "• The API key is set\n" +
                             "• The Endpoint URL is configured\n" +
                             "• The configuration section is properly loaded\n\n" +
                             "Please contact your administrator to configure the AI service.";

            var errorResponseText = new TextContent(errorMessage);
            var errorResponseMessage = new ChatMessage(new ChatRole("Assistant"), [errorResponseText])
            {
                AuthorName = "System"
            };

            // Add the user message to show what they tried to ask
            messages.Add(userMessage);
            messages.Add(errorResponseMessage);

            StateHasChanged();
            return;
        }
        catch (Exception ex)
        {
            // Handle other initialization errors
            var errorMessage = $"⚠️ **Service Error**\n\nFailed to initialize AI service: {ex.Message}\n\nPlease try again or contact your administrator if the problem persists.";

            var errorResponseText = new TextContent(errorMessage);
            var errorResponseMessage = new ChatMessage(new ChatRole("Assistant"), [errorResponseText])
            {
                AuthorName = "System"
            };

            // Add the user message to show what they tried to ask
            messages.Add(userMessage);
            messages.Add(errorResponseMessage);

            StateHasChanged();
            return;
        }

        Logger.LogDebug("[Chat:{InstanceId}] Setting agent context...", _instanceId);
        await SetAgentContextAsync(chat);
        Logger.LogDebug("[Chat:{InstanceId}] Agent context set", _instanceId);

        // Add the user message to the conversation
        messages.Add(userMessage);
        var lastRole = "Assistant";
        var responseText = new TextContent(string.Empty);
        currentResponseMessage = new ChatMessage(new ChatRole(lastRole), [responseText]);

        currentResponseCancellation = new();
        isGeneratingResponse = true;
        Logger.LogDebug("[Chat:{InstanceId}] About to call StateHasChanged before streaming", _instanceId);
        StateHasChanged(); // Ensure the UI updates to show the loading indicator

        try
        {
            Logger.LogDebug("[Chat:{InstanceId}] Starting streaming... UseStreaming={UseStreaming}", _instanceId, UseStreaming);
            if (UseStreaming)
                await InvokeStreamingAsync(userMessage, chat, lastRole, responseText);
            else
                await InvokeAsync(userMessage, chat, lastRole, responseText);
            Logger.LogDebug("[Chat:{InstanceId}] Streaming completed", _instanceId);
        }
        catch (Exception e)
        {
            Logger.LogError(e, "[Chat:{InstanceId}] ERROR during streaming/response", _instanceId);
            if (!string.IsNullOrWhiteSpace(currentResponseMessage?.Text))
                // Store the final response in the conversation
                messages.Add(currentResponseMessage!);
            lastRole = "Assistant";
            responseText = new("An error has occurred during processing: " + e.Message);
            currentResponseMessage = new ChatMessage(new ChatRole(lastRole), [responseText]);
            ChatMessageItem.NotifyChanged(currentResponseMessage);
            CancelAnyCurrentResponse();
        }
        finally
        {
            // Store the final response in the conversation
            if (!string.IsNullOrWhiteSpace(currentResponseMessage?.Text))
            {
                messages.Add(currentResponseMessage!);
                currentResponseMessage = null;
                await SaveCurrentConversation();

            }
            // Re-enable the input and focus it
            if (chatInput != null)
            {
                await chatInput.FocusAsync();
            }

            isGeneratingResponse = false;

            // Check if model was changed while generating - reinstantiate now
            if (pendingModelChange)
            {
                ReinstantiateAgent();
            }

            StateHasChanged();
        }
    }

    private async Task InvokeAsync(ChatMessage userMessage, IAgentChat chat, string lastRole, TextContent responseText)
    {
        // Stream and display a new response from the IChatClient
        await foreach (var update in chat.GetResponseAsync([userMessage], currentResponseCancellation.Token))
        {


            if (lastRole == update.AuthorName)
            {
                messages.AddMessages(new ChatResponseUpdate(new(update.AuthorName ?? update.Role.Value), update.Text), filter: c => c is not TextContent);
                responseText.Text += update.Text;
            }
            else
            {
                if (!string.IsNullOrWhiteSpace(currentResponseMessage?.Text))
                    // Store the final response in the conversation
                    messages.Add(currentResponseMessage!);
                lastRole = update.AuthorName ?? "Assistant";
                responseText = new TextContent(update.Text);
                currentResponseMessage = new ChatMessage(new ChatRole(lastRole), [responseText]);
            }

            StateHasChanged();

            if (currentResponseMessage != null)
                ChatMessageItem.NotifyChanged(currentResponseMessage);

        }

    }
    private async Task InvokeStreamingAsync(ChatMessage userMessage, IAgentChat chat, string lastRole, TextContent responseText)
    {
        Logger.LogDebug("[Chat:{InstanceId}] InvokeStreamingAsync entered, calling GetStreamingResponseAsync...", _instanceId);
        var updateCount = 0;

        // Stream and display a new response from the IChatClient
        Logger.LogDebug("[Chat:{InstanceId}] About to start await foreach over GetStreamingResponseAsync", _instanceId);
        await foreach (var update in chat.GetStreamingResponseAsync([userMessage], currentResponseCancellation.Token))
        {
            updateCount++;
            if (updateCount == 1)
            {
                Logger.LogDebug("[Chat:{InstanceId}] Got FIRST streaming update!", _instanceId);
            }
            var currentAuthor = update.AuthorName ?? "Assistant";
            Logger.LogDebug("Streaming update #{Count}: Author={Author}, TextLength={Length}",
                updateCount, currentAuthor, update.Text?.Length ?? 0);

            if (lastRole == currentAuthor)
            {
                // Same author - concatenate to existing message
                responseText.Text += update.Text;
                messages.AddMessages(update, filter: c => c is not TextContent);
            }
            else
            {
                // Different author - finalize previous message and start new one
                if (!string.IsNullOrWhiteSpace(currentResponseMessage?.Text))
                {
                    // Store the final response in the conversation
                    messages.Add(currentResponseMessage!);
                }

                // Start new message with new author
                lastRole = currentAuthor;
                responseText = new TextContent(update.Text);
                currentResponseMessage = new ChatMessage(new ChatRole(lastRole), [responseText])
                {
                    AuthorName = currentAuthor
                };
                Logger.LogDebug("Created new response message for author: {Author}", currentAuthor);
            }

            if (currentResponseMessage != null)
            {
                Logger.LogDebug("Notifying message change, text length: {Length}", currentResponseMessage.Text?.Length ?? 0);
                ChatMessageItem.NotifyChanged(currentResponseMessage);
            }

            Logger.LogDebug("Calling StateHasChanged");
            StateHasChanged();
        }

        Logger.LogDebug("[Chat:{InstanceId}] Streaming completed, total updates: {Count}", _instanceId, updateCount);
        // Delegation messages are now added immediately during streaming, no need to defer them
    }

    private async Task SetAgentContextAsync(IAgentChat chat)
    {
        // Get the current context with MeshNode name resolution
        var context = await GetCurrentAgentContextAsync();

        // Always set the context (even if null) to ensure it's updated
        chat.SetContext(context);
    }

    private AgentContext? GetCurrentAgentContext()
    {
        // If boundContext is set (via control binding), use it
        // Otherwise, get it from the current URL (synchronous version without MeshNode name)
        return boundContext ?? GetContextFromCurrentUrlSync();
    }

    private async Task<AgentContext?> GetCurrentAgentContextAsync()
    {
        // If boundContext is set (via control binding), use it
        // Otherwise, get it from the current URL with MeshNode name resolution
        return boundContext ?? await GetContextFromCurrentUrlAsync();
    }

    /// <summary>
    /// Synchronous version that returns basic context without MeshNode name.
    /// Use GetContextFromCurrentUrlAsync for full context including MeshNode name.
    /// </summary>
    private AgentContext? GetContextFromCurrentUrlSync()
    {
        var path = NavigationManager.ToBaseRelativePath(NavigationManager.Uri);
        Logger.LogDebug("Current URL path: '{Path}'", path);

        // Skip if path is empty or just "chat"
        if (string.IsNullOrEmpty(path) || path == "chat")
        {
            Logger.LogDebug("Path is empty or 'chat', returning null context");
            return null;
        }

        // Split the path into segments
        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        Logger.LogDebug("Path segments: {Segments} (count: {Count})", string.Join(", ", segments), segments.Length);

        // Need at least one segment for a context
        if (segments.Length < 1)
        {
            Logger.LogDebug("No segments for context, returning null");
            return null;
        }

        // For single-segment paths (e.g., "Systemorph"), use it as the full path
        // For multi-segment paths (e.g., "Systemorph/Marketing"), use first as type, second as id
        var address = segments.Length == 1
            ? new Address(segments[0], string.Empty)
            : new Address(segments[0], segments[1]);

        var layoutArea = segments.Length <= 2 ? null : new LayoutAreaReference(segments[2])
        {
            Id = string.Join('/', segments.Skip(3))
        };

        var context = new AgentContext
        {
            Address = address,
            LayoutArea = layoutArea
        };

        Logger.LogDebug("Created context - Address: {Address}, LayoutArea: {LayoutArea}", address, layoutArea?.Area);

        return context;
    }

    /// <summary>
    /// Async version that resolves the path using MeshCatalog and includes the MeshNode name.
    /// Uses the same resolution logic as ApplicationPage.razor.cs.
    /// </summary>
    private async Task<AgentContext?> GetContextFromCurrentUrlAsync()
    {
        var path = NavigationManager.ToBaseRelativePath(NavigationManager.Uri);
        Logger.LogDebug("Current URL path for async resolution: '{Path}'", path);

        // Skip if path is empty or just "chat"
        if (string.IsNullOrEmpty(path) || path == "chat")
        {
            Logger.LogDebug("Path is empty or 'chat', returning null context");
            return null;
        }

        try
        {
            var meshCatalog = Hub.ServiceProvider.GetService<IMeshCatalog>();
            if (meshCatalog == null)
            {
                Logger.LogDebug("IMeshCatalog not available, falling back to sync version");
                return GetContextFromCurrentUrlSync();
            }

            // Resolve the path using MeshCatalog (same as ApplicationPage.razor.cs)
            var resolution = await meshCatalog.ResolvePathAsync(path);
            if (resolution == null)
            {
                Logger.LogDebug("Path resolution returned null, falling back to sync version");
                return GetContextFromCurrentUrlSync();
            }

            // Get the address from the resolution prefix
            var address = (Address)resolution.Prefix;

            // Parse the remainder into area and id (same logic as ApplicationPage)
            var (area, id) = ParseRemainder(resolution.Remainder);

            // Decode area and id
            area = area != null ? (string)WorkspaceReference.Decode(area) : null;
            id = id != null ? (string)WorkspaceReference.Decode(id) : "";

            var layoutArea = area != null ? new LayoutAreaReference(area) { Id = id } : null;

            // Get the MeshNode to retrieve its name
            string? meshNodeName = null;
            var node = await meshCatalog.GetNodeAsync(address);
            if (node != null)
            {
                meshNodeName = node.Name ?? node.Id;
                Logger.LogDebug("Resolved MeshNode name: '{MeshNodeName}'", meshNodeName);
            }

            var context = new AgentContext
            {
                Address = address,
                LayoutArea = layoutArea,
                MeshNodeName = meshNodeName
            };

            Logger.LogDebug("Created async context - Address: {Address}, LayoutArea: {LayoutArea}, MeshNodeName: {MeshNodeName}",
                address, layoutArea?.Area, meshNodeName);

            return context;
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Error resolving path async, falling back to sync version");
            return GetContextFromCurrentUrlSync();
        }
    }

    /// <summary>
    /// Parses the remainder of a resolved path into area and id.
    /// Same logic as ApplicationPage.razor.cs.
    /// </summary>
    private static (string? Area, string? Id) ParseRemainder(string? remainder)
    {
        if (string.IsNullOrEmpty(remainder))
            return (null, null);

        var slashIndex = remainder.IndexOf('/');
        if (slashIndex >= 0)
            return (remainder.Substring(0, slashIndex), remainder.Substring(slashIndex + 1));

        return (remainder, null);
    }

    private void CancelAnyCurrentResponse()
    {
        // If a response was cancelled while streaming, include it in the conversation so it's not lost
        if (currentResponseMessage is not null)
        {
            messages.Add(currentResponseMessage);
        }

        currentResponseCancellation.Cancel();
        currentResponseCancellation = new();
        currentResponseMessage = null;
        isGeneratingResponse = false;
        StateHasChanged();
    }

    private async Task CancelCurrentResponse()
    {
        CancelAnyCurrentResponse();

        // Re-enable the input and focus it
        if (chatInput != null)
        {
            await chatInput.FocusAsync();
        }
    }
    [Parameter] public EventCallback OnCloseRequested { get; set; }

    private async Task CloseChatAsync()
    {
        // If parent component provided a close callback, use it
        if (OnCloseRequested.HasDelegate)
        {
            await OnCloseRequested.InvokeAsync();
        }
        else
        {
            // Otherwise, navigate away from the chat page
            NavigationManager.NavigateTo("/");
        }
    }

    private async Task ChangeChatPosition(ChatPosition newPosition)
    {
        positionMenuVisible = false;

        if (currentPosition != newPosition)
        {
            currentPosition = newPosition;
            StateHasChanged(); // Update UI immediately

            if (OnPositionChanged.HasDelegate)
            {
                await OnPositionChanged.InvokeAsync(currentPosition);
            }
        }
        else
        {
            StateHasChanged();
        }
    }

    private CompletionProviderConfig GetAgentCompletionConfig()
    {
        // Only provide trigger characters - items are fetched async via GetCompletionsForEditorAsync
        // Note: @ triggers @agent/Name and @model/Name, / triggers commands
        return new CompletionProviderConfig
        {
            TriggerCharacters = ["@", "/"],
            Items = []
        };
    }

    /// <summary>
    /// Async callback for editor autocomplete with server-side fuzzy scoring.
    /// Called from MonacoEditorView when user types after @ trigger.
    /// Uses the AutocompleteClient to dispatch requests based on query stage.
    /// </summary>
    private async Task<CompletionItem[]> GetCompletionsForEditorAsync(string query)
    {
        try
        {
            var context = GetCurrentAgentContext();
            var meshCatalog = Hub.ServiceProvider.GetService<IMeshCatalog>();

            // Create autocomplete client with:
            // - Base addresses: app/Agents (for agents, models, prefixes, commands)
            // - MeshCatalog: for namespace address resolution (e.g., @pricing/ -> Insurance app)
            var client = new AutocompleteClient(
                Hub,
                _ => [AI.Application.ApplicationAddress.Agents],
                meshCatalog);

            // Get completions from dispatched addresses
            var response = await client.GetCompletionsAsync(query, context);

            // Apply local fuzzy scoring for better UI display
            var fuzzyScorer = new FuzzyScorer();
            var scored = fuzzyScorer.Score(response.Items, query, i => i.Label);

            // Sort by priority (desc), then fuzzy score (desc)
            var results = scored
                .OrderByDescending(s => s.Item.Priority)
                .ThenByDescending(s => s.Score)
                .Take(20)
                .Select(s => new CompletionItem
                {
                    Label = s.Item.Label,
                    InsertText = s.Item.InsertText,
                    Description = s.Item.Description,
                    Category = s.Item.Category,
                    Path = s.Item.InsertText,  // Use InsertText as path for two-line display
                    Kind = MapAutocompleteKindToCompletionKind(s.Item.Kind)
                })
                .ToArray();

            return results;
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

    /// <summary>
    /// Extracts text content from a ChatMessage.
    /// </summary>
    private static string ExtractTextFromChatMessage(ChatMessage message)
    {
        var textBuilder = new System.Text.StringBuilder();
        foreach (var content in message.Contents)
        {
            if (content is TextContent textContent)
            {
                textBuilder.Append(textContent.Text);
            }
        }
        return textBuilder.ToString();
    }

    /// <summary>
    /// Handles a parsed chat command.
    /// </summary>
    private async Task HandleCommandAsync(ParsedCommand command, string originalMessage)
    {
        if (commandRegistry == null || !commandRegistry.TryGetCommand(command.Name, out var chatCommand) || chatCommand == null)
        {
            AddSystemMessage($"Unknown command: /{command.Name}. Type /help for available commands.");
            return;
        }

        var context = new CommandContext
        {
            ParsedCommand = command,
            AvailableAgents = agentDisplayInfos.ToDictionary(a => a.Name),
            CurrentAgent = selectedAgentInfo,
            SetCurrentAgent = agent => OnAgentInfoChanged(agent),
            AvailableModels = availableModels,
            CurrentModel = selectedModel,
            SetCurrentModel = model => OnModelChanged(model),
            AgentContext = GetCurrentAgentContext(),
            CommandRegistry = commandRegistry
        };

        var result = await chatCommand.ExecuteAsync(context);

        if (!string.IsNullOrEmpty(result.Message))
        {
            // Show the user's command as a user message
            var userCommandMessage = new ChatMessage(ChatRole.User, originalMessage);
            messages.Add(userCommandMessage);

            AddSystemMessage(result.Message);
        }
    }

    /// <summary>
    /// Adds a system message to the chat (for command output).
    /// </summary>
    private void AddSystemMessage(string message)
    {
        var systemMessage = new ChatMessage(ChatRole.Assistant, message)
        {
            AuthorName = "System"
        };
        messages.Add(systemMessage);
        StateHasChanged();
    }

    /// <summary>
    /// Displays a configuration error message to the user.
    /// </summary>
    private void ShowConfigurationError(string errorMessage)
    {
        var formattedMessage = $"⚠️ **Configuration Error**\n\n{errorMessage}";
        var errorContent = new TextContent(formattedMessage);
        var errorChatMessage = new ChatMessage(new ChatRole("Assistant"), [errorContent])
        {
            AuthorName = "System"
        };
        messages.Add(errorChatMessage);
        StateHasChanged();
    }

    public override ValueTask DisposeAsync()
    {
        Logger.LogInformation("[Chat:{InstanceId}] DisposeAsync called. Was already disposed: {WasDisposed}", _instanceId, _isDisposed);

        if (!_isDisposed)
        {
            _isDisposed = true;
            NavigationManager.LocationChanged -= OnLocationChanged;
            Logger.LogInformation("[Chat:{InstanceId}] Unsubscribed from NavigationManager.LocationChanged", _instanceId);

            currentResponseCancellation.Cancel();
            Logger.LogInformation("[Chat:{InstanceId}] Cancelled response cancellation token", _instanceId);
        }

        return base.DisposeAsync();
    }


}
