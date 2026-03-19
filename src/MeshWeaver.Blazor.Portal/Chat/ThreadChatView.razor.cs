using System.Text.Json;
using MeshWeaver.AI;
using MeshWeaver.Blazor.Components;
using MeshWeaver.Blazor.Components.Monaco;
using MeshWeaver.Blazor.Portal.SidePanel;
using MeshWeaver.ContentCollections;
using MeshWeaver.Data;
using MeshWeaver.Layout;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Blazor.Portal.Chat;

public enum ChatViewMode { Chat, ResumeThreads }

public partial class ThreadChatView : BlazorView<ThreadChatControl, ThreadChatView>
{
    [Inject] private INavigationService NavigationService { get; set; } = null!;
    [Inject] private SidePanelStateService SidePanelState { get; set; } = null!;
    [Inject] private IMeshService MeshQuery { get; set; } = null!;

    private bool _isDisposed;
    private IDisposable? agentSubscription;
    private ISynchronizationStream<JsonElement>? _ownedStream;
    private readonly string _instanceId = Guid.NewGuid().ToString("N")[..8];

    // Thread state
    private string? threadPath;
    private string? threadName;
    private string? initialContext; // Backing field for agent initialization


    private ThreadViewModel? _threadViewModel;
    private ThreadViewModel? ThreadViewModel
    {
        get => _threadViewModel;
        set
        {
            var old = _threadViewModel;
            _threadViewModel = value;

            var oldMsgs = old?.Messages ?? (IReadOnlyList<string>)[];
            var newMsgs = value?.Messages ?? (IReadOnlyList<string>)[];

            Logger.LogDebug("[ThreadChat:{InstanceId}] ThreadViewModel setter: old={OldCount} msgs, new={NewCount} msgs, equal={Equal}, stream={HasStream}",
                _instanceId, oldMsgs.Count, newMsgs.Count, Equals(old, value), Stream != null);

            // Sync threadPath and initialContext from the view model
            if (value != null)
            {
                threadPath = value.ThreadPath ?? threadPath;
                initialContext = value.InitialContext ?? initialContext;
            }

            // If messages changed, force re-render and release submission handler
            if (!Equals(old, value))
            {
                Logger.LogDebug("[ThreadChat:{InstanceId}] ThreadViewModel CHANGED: [{OldIds}] -> [{NewIds}]",
                    _instanceId,
                    string.Join(",", oldMsgs),
                    string.Join(",", newMsgs));

                var oldCount = oldMsgs.Count;
                var newCount = newMsgs.Count;
                if (newCount > oldCount &&
                    submissionHandler.State != ChatSubmissionHandler.SubmissionState.Idle)
                {
                    submissionHandler.OnResponseAppeared();
                }
                // Force re-render — DataBind's RequestStateChange may not trigger
                // if the setter is called from a non-UI synchronization context
                InvokeAsync(StateHasChanged);
            }
        }
    }
    private IReadOnlyList<string> ThreadMessages => ThreadViewModel?.Messages ?? [];
    private string? lastContextUrl; // Track URL for context change detection

    // Input state
    private MonacoEditorView? monacoEditor;
    private string? MessageText;
    private bool isCreatingThread;
    private readonly ChatSubmissionHandler submissionHandler = new();

    // Unified attachments (context + @references)
    private readonly List<AttachmentInfo> attachments = new();
    private const string placeholderText = "Type a message... Use @ to reference nodes";

    // View mode state
    private ChatViewMode viewMode = ChatViewMode.Chat;

    // Resume threads state
    private List<MeshNode> recentThreads = new();
    private string resumeSearchQuery = "";
    private bool isLoadingRecentThreads;

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

        // Initialize from direct ViewModel properties (side panel / dashboard case)
        threadPath ??= ViewModel.ThreadPath;
        initialContext ??= ViewModel.InitialContext;

        // Subscribe to side panel menu actions
        SidePanelState.OnActionRequested += OnSidePanelAction;

        // Set initial title
        UpdateSidePanelTitle();

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

        Logger.LogDebug("[ThreadChat:{InstanceId}] OnInitializedAsync completed", _instanceId);
    }

    private async Task<string?> ResolveContextDisplayNameAsync(string? path)
    {
        if (string.IsNullOrEmpty(path))
            return null;

        try
        {
            var pathResolver = Hub.ServiceProvider.GetService<IPathResolver>();
            if (pathResolver == null)
                return null;

            var resolution = await pathResolver.ResolvePathAsync(path);
            if (resolution == null)
                return null;

            var meshQuery = Hub.ServiceProvider.GetRequiredService<IMeshService>();
            var node = await meshQuery.QueryAsync<MeshNode>($"path:{resolution.Prefix}").FirstOrDefaultAsync();
            return node?.Name ?? node?.Id;
        }
        catch (Exception ex)
        {
            Logger.LogDebug(ex, "Error resolving context display name for path: {Path}", path);
            return null;
        }
    }

    /// <summary>
    /// Resolves a path to its primary context path, handling satellite nodes.
    /// If the node at the path is a satellite, returns its PrimaryNodePath instead.
    /// </summary>
    private async Task<string> ResolvePrimaryContextPathAsync(string path)
    {
        try
        {
            var meshQuery = Hub.ServiceProvider.GetService<IMeshService>();
            if (meshQuery == null)
                return path;

            var node = await meshQuery.QueryAsync<MeshNode>($"path:{path}").FirstOrDefaultAsync();
            if (node != null && node.MainNode != node.Path)
                return node.MainNode;
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
        Logger.LogDebug("[ThreadChat:{InstanceId}] BindData called, ViewModel.ThreadViewModel={VmType}, Stream={HasStream}",
            _instanceId, ViewModel.ThreadViewModel?.GetType().Name ?? "null", Stream != null);
        DataBind(ViewModel.ThreadViewModel, x => x.ThreadViewModel, ConvertThreadViewModel);
    }

    /// <summary>
    /// Establishes a remote stream to the thread hub's layout area when the view
    /// is used in the side panel (where Stream is null). This enables DataBind
    /// for cells to work via JsonPointerReference.
    /// </summary>
    private void EnsureStreamForThread(string threadPath)
    {
        if (Stream != null) return; // Already have a stream (full-screen case)
        _ownedStream?.Dispose();
        var workspace = Hub.ServiceProvider.GetService<IWorkspace>();
        if (workspace == null) return;
        _ownedStream = workspace.GetRemoteStream<JsonElement, LayoutAreaReference>(
            new Address(threadPath),
            new LayoutAreaReference(ThreadNodeType.ThreadArea));
        Stream = _ownedStream;
        BindData();
        StateHasChanged();
    }

    private Task InitializeAgentAndModelSelectionsAsync()
    {
        // Load available models from DI-registered factories
        var factories = ChatClientFactories.ToList();
        Logger.LogInformation("[ThreadChat:{InstanceId}] IChatClientFactory instances resolved: {Count}", _instanceId, factories.Count);

        availableModels = factories
            .OrderBy(f => f.Order)
            .SelectMany(f => f.Models.Select(m => new ModelInfo
            {
                Name = m,
                Provider = f.Name,
                Order = f.Order
            }))
            .ToList();

        Logger.LogInformation("[ThreadChat:{InstanceId}] Available models ({Count}): [{Models}]",
            _instanceId, availableModels.Count, string.Join(", ", availableModels.Select(m => $"{m.Name} ({m.Provider})")));

        // Subscribe to agent MeshNodes reactively
        SubscribeToAgentNodes();

        return Task.CompletedTask;
    }

    // Merged agent nodes from multiple reactive queries, keyed by path
    private readonly Dictionary<string, AgentDisplayInfo> _agentsByPath = new();

    private void SubscribeToAgentNodes()
    {
        agentSubscription?.Dispose();
        _agentsByPath.Clear();

        var subscriptions = new List<IDisposable>();

        // Query 1: Agents from the Agent namespace
        var agentNsRequest = MeshQueryRequest.FromQuery("namespace:Agent nodeType:Agent");
        subscriptions.Add(MeshQuery.ObserveQuery<MeshNode>(agentNsRequest)
            .Subscribe(change => InvokeAsync(() => OnAgentQueryChange(change))));

        // Query 2: Agents along the current context path's ancestor chain
        if (!string.IsNullOrEmpty(initialContext))
        {
            var contextRequest = MeshQueryRequest.FromQuery($"namespace:{initialContext} nodeType:Agent scope:selfAndAncestors");
            subscriptions.Add(MeshQuery.ObserveQuery<MeshNode>(contextRequest)
                .Subscribe(change => InvokeAsync(() => OnAgentQueryChange(change))));
        }

        agentSubscription = new System.Reactive.Disposables.CompositeDisposable(subscriptions);
    }

    private void OnAgentQueryChange(QueryResultChange<MeshNode> change)
    {
        if (change.ChangeType == QueryChangeType.Initial ||
            change.ChangeType == QueryChangeType.Reset ||
            change.ChangeType == QueryChangeType.Added ||
            change.ChangeType == QueryChangeType.Updated)
        {
            foreach (var node in change.Items)
            {
                var info = ToAgentDisplayInfo(node);
                if (info != null && node.Path != null)
                    _agentsByPath[node.Path] = info;
            }
        }
        else if (change.ChangeType == QueryChangeType.Removed)
        {
            foreach (var node in change.Items)
            {
                if (node.Path != null)
                    _agentsByPath.Remove(node.Path);
            }
        }

        agentDisplayInfos = _agentsByPath.Values
            .OrderBy(a => a.Order)
            .ThenBy(a => a.Name)
            .ToList();

        // Preserve current selection if still valid, otherwise select first
        if (selectedAgentInfo != null &&
            agentDisplayInfos.Any(a => a.Path == selectedAgentInfo.Path))
        {
            selectedAgentInfo = agentDisplayInfos.First(a => a.Path == selectedAgentInfo.Path);
        }
        else
        {
            selectedAgentInfo = agentDisplayInfos.FirstOrDefault();
        }

        // Set model from agent's preferred model
        if (selectedAgentInfo != null)
            selectedModelInfo = GetPreferredModelInfoForAgent(selectedAgentInfo.Name);
        else
            selectedModelInfo = availableModels.FirstOrDefault();

        StateHasChanged();
    }

    private static AgentDisplayInfo? ToAgentDisplayInfo(MeshNode node)
    {
        if (node.Content is not AgentConfiguration config)
            return null;
        return new AgentDisplayInfo
        {
            Name = config.DisplayName ?? config.Id,
            Path = node.Path,
            Description = config.Description ?? "",
            GroupName = config.GroupName,
            Order = config.Order,
            Icon = config.Icon,
            CustomIconSvg = config.CustomIconSvg,
            AgentConfiguration = config
        };
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

    private async Task SendMessageAsync()
    {
        if (_isDisposed)
            return;

        // Read the editor value directly to avoid truncation from async binding lag
        string? userMessageText;
        if (monacoEditor != null)
        {
            userMessageText = await monacoEditor.GetValueAsync();
        }
        else
        {
            userMessageText = MessageText;
        }

        // Attempt to begin submission — rejects empty text and concurrent submissions
        if (!submissionHandler.TryBeginSubmit(userMessageText))
            return;

        // Disable input and clear the editor immediately
        MessageText = null;
        StateHasChanged();

        if (monacoEditor != null)
        {
            await monacoEditor.ClearAsync();
        }

        try
        {
            // Check for context change before sending
            await CheckAndUpdateContextAsync();

            await UpdateExtractedReferencesAsync();

            // Auto-create thread on first message via server-side CreateThreadRequest
            if (string.IsNullOrEmpty(threadPath))
            {
                isCreatingThread = true;
                StateHasChanged();
                try
                {
                    var ns = NavigationService.CurrentNamespace ?? initialContext ?? "";
                    var createResponse = await Hub.AwaitResponse(
                        new CreateThreadRequest
                        {
                            Namespace = ns,
                            UserMessageText = userMessageText!,
                            InitialContext = initialContext,
                            ModelName = selectedModelInfo?.Name
                        },
                        o => o.WithTarget(new Address(ns)));

                    if (!createResponse.Message.Success)
                    {
                        Logger.LogError("[ThreadChat:{InstanceId}] Failed to create thread: {Error}",
                            _instanceId, createResponse.Message.Error);
                        submissionHandler.ForceRelease();
                        return;
                    }

                    threadPath = createResponse.Message.ThreadPath;
                    threadName = createResponse.Message.ThreadName;
                    SidePanelState.SetContentPath(threadPath);
                    UpdateSidePanelTitle();
                    EnsureStreamForThread(threadPath!);
                }
                finally
                {
                    isCreatingThread = false;
                }
            }

            // Post execution request to thread hub — hub creates both user and response nodes
            Hub.Post(new SubmitMessageRequest
            {
                ThreadPath = threadPath!,
                UserMessageText = userMessageText!,
                AgentName = selectedAgentInfo?.Name,
                ModelName = selectedModelInfo?.Name,
                ContextPath = initialContext,
                Attachments = attachments.Select(a => a.Path).ToList()
            }, o => o.WithTarget(new Address(threadPath!)));

            // Transition to WaitingForResponse — input stays disabled until new cells appear
            submissionHandler.OnMessagePosted();

            // In compact/dashboard mode: navigate to full-screen thread chat
            if (ViewModel.HideEmptyState)
            {
                NavigationManager.NavigateTo($"/{threadPath}");
            }
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "[ThreadChat:{InstanceId}] Error during message submission", _instanceId);
            submissionHandler.ForceRelease();
        }

        StateHasChanged();
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

    private async Task OnCompletionItemAccepted(string path)
    {
        if (string.IsNullOrEmpty(path))
            return;

        // Check if this path matches a known agent — select it instead of adding a chip
        if (_agentsByPath.TryGetValue(path, out var agentInfo))
        {
            OnAgentChanged(agentInfo);
            return;
        }

        // Skip directory/collection entries (they'll re-trigger autocomplete via JS)
        if (path.EndsWith("/") || path.EndsWith(":"))
            return;

        // Add as attachment chip if not already present (text stays in editor)
        if (!attachments.Any(a => a.Path.Equals(path, StringComparison.OrdinalIgnoreCase)))
        {
            var displayName = await ResolveContextDisplayNameAsync(path);
            attachments.Add(new AttachmentInfo(path, displayName, IsContext: false));
        }

        StateHasChanged();
    }

    private async Task UpdateExtractedReferencesAsync()
    {
        var currentRefs = MarkdownReferenceExtractor.GetUniquePaths(MessageText);
        var updatedText = MessageText;
        var editorNeedsUpdate = false;

        var existingPaths = attachments.Select(a => a.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var refPath in currentRefs)
        {
            if (existingPaths.Contains(refPath))
                continue;

            if (_agentsByPath.TryGetValue(refPath, out var agentInfo))
            {
                OnAgentChanged(agentInfo);
                // Only remove agent references from text
                if (!string.IsNullOrEmpty(updatedText))
                {
                    updatedText = MarkdownReferenceExtractor.RemoveReferenceByPath(updatedText, refPath);
                    editorNeedsUpdate = true;
                }
            }
            else
            {
                // Add as attachment chip, keep text in editor
                attachments.Add(new AttachmentInfo(refPath, DisplayName: null, IsContext: false));
            }
        }

        // Remove stale reference attachments not in current text
        var remainingRefs = MarkdownReferenceExtractor.GetUniquePaths(editorNeedsUpdate ? updatedText : MessageText);
        attachments.RemoveAll(a => !a.IsContext && !remainingRefs.Contains(a.Path, StringComparer.OrdinalIgnoreCase));

        if (editorNeedsUpdate)
        {
            MessageText = updatedText;
            if (monacoEditor != null)
                await monacoEditor.SetValueAsync(updatedText ?? "");
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

        // Update model to agent's preferred model
        var preferredModel = GetPreferredModelInfoForAgent(newAgent.Name);
        if (preferredModel != null)
        {
            selectedModelInfo = preferredModel;
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

        StateHasChanged();
    }

    // --- Side panel title and action handling ---

    private void UpdateSidePanelTitle()
    {
        if (!string.IsNullOrEmpty(threadName))
            SidePanelState.SetTitle(threadName);
        else if (!string.IsNullOrEmpty(threadPath))
            SidePanelState.SetTitle(threadPath);
        else
            SidePanelState.SetTitle(null); // Will show "New Thread" in SidePanel
    }

    private void OnSidePanelAction(string action)
    {
        InvokeAsync(() =>
        {
            switch (action)
            {
                case "New":
                    StartNewThread();
                    break;
                case "Resume":
                    _ = SwitchToResumeModeAsync();
                    break;
            }
        });
    }

    // --- Mode switching ---

    private void StartNewThread()
    {
        threadPath = null;
        threadName = null;
        SidePanelState.SetContentPath(null);
        UpdateSidePanelTitle();
        _ownedStream?.Dispose();
        _ownedStream = null;
        Stream = null;
        ThreadViewModel = null;
        viewMode = ChatViewMode.Chat;
        StateHasChanged();
    }

    private async Task SwitchToResumeModeAsync()
    {
        viewMode = ChatViewMode.ResumeThreads;
        resumeSearchQuery = "";
        isLoadingRecentThreads = true;
        StateHasChanged();

        await LoadRecentThreadsAsync();
    }

    private void SwitchToChatMode()
    {
        viewMode = ChatViewMode.Chat;
        StateHasChanged();
    }

    // --- Resume threads ---

    private async Task LoadRecentThreadsAsync(string? searchText = null)
    {
        try
        {
            isLoadingRecentThreads = true;
            StateHasChanged();

            var meshQuery = Hub.ServiceProvider.GetService<IMeshService>();
            if (meshQuery == null)
            {
                recentThreads = [];
                return;
            }

            var query = string.IsNullOrWhiteSpace(searchText)
                ? "nodeType:Thread limit:20 sort:LastModified-desc"
                : $"nodeType:Thread limit:20 sort:LastModified-desc {searchText}";

            recentThreads = await meshQuery.QueryAsync<MeshNode>(query).ToListAsync();
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "[ThreadChat:{InstanceId}] Error loading recent threads", _instanceId);
            recentThreads = [];
        }
        finally
        {
            isLoadingRecentThreads = false;
            StateHasChanged();
        }
    }

    private void OnResumeSearchChanged(string query)
    {
        resumeSearchQuery = query;
        _ = LoadRecentThreadsAsync(query);
    }

    private Task OnSelectThread(MeshNode node)
    {
        threadPath = node.Path;
        threadName = node.Name;
        viewMode = ChatViewMode.Chat;
        SidePanelState.SetContentPath(threadPath);
        UpdateSidePanelTitle();
        if (!string.IsNullOrEmpty(threadPath))
            EnsureStreamForThread(threadPath);
        StateHasChanged();
        return Task.CompletedTask;
    }

    // Thread creation is handled server-side via CreateThreadRequest handler.

    private CompletionProviderConfig GetCompletionConfig()
    {
        return new CompletionProviderConfig
        {
            TriggerCharacters = ["@"],
            Items = []
        };
    }

    private static readonly (string Tag, string Description, string Emoji)[] UnifiedPathTags =
    [
        ("content:", "Content files (documents, images)", "\U0001F4C4"),
        ("data:", "Data collections and entities", "\U0001F5C3"),
        ("schema:", "JSON schemas and type definitions", "\U0001F4CB"),
        ("collection:", "Collection configuration", "\U0001F4C1"),
        ("model:", "Data model diagrams", "\U0001F4CA"),
        ("menu:", "Menu items", "\U0001F4DC"),
        ("layoutAreas:", "Available layout areas", "\U0001F5BC"),
    ];

    /// <summary>
    /// Main completion handler. Parses the reference after @ and routes to the right handler.
    /// All results are scoped and filtered — no random/unrelated suggestions.
    /// </summary>
    private async Task<CompletionItem[]> GetCompletionsAsync(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return [];

        try
        {
            if (!query.StartsWith("@"))
                return [];

            var reference = query[1..]; // strip @
            var currentAddress = NavigationService.CurrentNamespace ?? initialContext ?? "";

            // Case 1: Contains colon → tag query (e.g., "Org/Microsoft/content:My")
            if (reference.Contains(':'))
                return (await GetContentCompletionsAsync(reference)).ToArray();

            // Parse reference into basePath + filter
            // e.g. "Org/Microsoft/" → basePath="Org/Microsoft", filter=""
            // e.g. "Org/Micro"     → basePath="Org", filter="Micro"
            // e.g. ""               → basePath=currentAddress, filter=""
            // e.g. "Sys"            → basePath="", filter="Sys"
            string basePath;
            string filter;

            if (string.IsNullOrEmpty(reference))
            {
                // Just "@" — scope to current address
                basePath = currentAddress;
                filter = "";
            }
            else if (reference.EndsWith("/"))
            {
                // Completed path segment — show children
                basePath = reference.TrimEnd('/');
                filter = "";
            }
            else
            {
                var lastSlash = reference.LastIndexOf('/');
                if (lastSlash >= 0)
                {
                    basePath = reference[..lastSlash];
                    filter = reference[(lastSlash + 1)..];
                }
                else
                {
                    // No slash — search from root with this as prefix
                    basePath = "";
                    filter = reference;
                }
            }

            var results = new List<CompletionItem>();

            // Get node suggestions from mesh query
            var suggestions = await MeshQuery.AutocompleteAsync(basePath, filter, 20).ToArrayAsync();
            results.AddRange(suggestions.Select(s => ToCompletionItem(s, string.IsNullOrEmpty(basePath) ? "Addresses" : "Nearby")));

            // When showing children of an address (basePath is set, filter is empty or partial),
            // also show unified path tags scoped to this address
            if (!string.IsNullOrEmpty(basePath))
                results.AddRange(GetUnifiedPathTagCompletions(basePath, filter));

            // When no basePath and no filter, also show global suggestions + tags for current address
            if (string.IsNullOrEmpty(basePath) && string.IsNullOrEmpty(filter) && !string.IsNullOrEmpty(currentAddress))
            {
                var existingPaths = results.Select(r => r.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
                var contextSuggestions = await MeshQuery.AutocompleteAsync(currentAddress, "", 20).ToArrayAsync();
                results.InsertRange(0, contextSuggestions
                    .Where(s => !existingPaths.Contains(s.Path))
                    .Select(s => ToCompletionItem(s, "Nearby")));
                results.AddRange(GetUnifiedPathTagCompletions(currentAddress, ""));
            }

            return results.ToArray();
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error getting completions for query: {Query}", query);
            return [];
        }
    }

    private static List<CompletionItem> GetUnifiedPathTagCompletions(string address, string filterText)
    {
        var items = new List<CompletionItem>();
        foreach (var (tag, description, emoji) in UnifiedPathTags)
        {
            if (!string.IsNullOrEmpty(filterText) &&
                !tag.StartsWith(filterText, StringComparison.OrdinalIgnoreCase))
                continue;

            var tagPath = string.IsNullOrEmpty(address)
                ? tag
                : $"{address}/{tag}";

            items.Add(new CompletionItem
            {
                Label = $"{emoji} {tag}",
                InsertText = $"@{tagPath}",
                Description = description,
                Path = tagPath,
                Category = "Keywords",
                Kind = CompletionItemKind.Module
            });
        }
        return items;
    }

    private static CompletionItem ToCompletionItem(QuerySuggestion s, string category)
    {
        return new CompletionItem
        {
            Label = s.Path,
            InsertText = $"@{s.Path}",
            Description = s.NodeType ?? s.Name,
            Path = s.Path,
            Category = category,
            IconUrl = s.Icon
        };
    }

    /// <summary>
    /// Queries content collection files and folders and returns matching completion items.
    /// Uses Unified Path format: {address}/{collectionName}:{filePath}
    /// Supports browsing into folders when prefix contains collectionName:subpath/
    /// Gets the content service from the current navigation address hub (not the portal hub).
    /// </summary>
    private async Task<List<CompletionItem>> GetContentCompletionsAsync(string prefix)
    {
        var items = new List<CompletionItem>();

        try
        {
            // Use NavigationService to get the current address — content collections
            // are registered on node hubs, not on the portal hub.
            var nodeAddress = NavigationService.CurrentNamespace ?? initialContext ?? "";

            IContentService? contentService = null;
            if (!string.IsNullOrEmpty(nodeAddress))
            {
                var nodeHub = Hub.GetHostedHub(new Address(nodeAddress), HostedHubCreation.Never);
                contentService = nodeHub?.ServiceProvider.GetService<IContentService>();
            }
            // Fallback to portal hub's content service
            contentService ??= Hub.ServiceProvider.GetService<IContentService>();
            if (contentService == null)
                return items;
            var configs = contentService.GetAllCollectionConfigs();

            // Parse prefix to detect collectionName:subpath pattern
            var colonIndex = prefix.IndexOf(':');
            string? targetCollection = null;
            string browsePath = "/";
            string filterText = "";

            if (colonIndex >= 0)
            {
                // User typed something like "content:subfolder/" or "content:read"
                // Extract the collection name (may include address prefix)
                var beforeColon = prefix[..colonIndex];
                var afterColon = colonIndex < prefix.Length - 1 ? prefix[(colonIndex + 1)..] : "";

                // The collection name is the last segment before the colon
                var lastSlash = beforeColon.LastIndexOf('/');
                targetCollection = lastSlash >= 0 ? beforeColon[(lastSlash + 1)..] : beforeColon;

                if (afterColon.EndsWith("/"))
                {
                    // Browsing inside a folder: content:images/
                    browsePath = "/" + afterColon.TrimEnd('/');
                }
                else if (afterColon.Contains('/'))
                {
                    // Partial path: content:images/log → browse "images", filter "log"
                    var pathSlash = afterColon.LastIndexOf('/');
                    browsePath = "/" + afterColon[..pathSlash];
                    filterText = afterColon[(pathSlash + 1)..];
                }
                else
                {
                    // Filter at root: content:read → browse "/", filter "read"
                    filterText = afterColon;
                }
            }

            foreach (var config in configs)
            {
                // If user typed a collection prefix, only show that collection
                if (targetCollection != null &&
                    !config.Name.Equals(targetCollection, StringComparison.OrdinalIgnoreCase))
                    continue;

                // If no colon yet, show collection names as suggestions
                if (colonIndex < 0)
                {
                    var collPath = string.IsNullOrEmpty(nodeAddress)
                        ? $"{config.Name}:"
                        : $"{nodeAddress}/{config.Name}:";

                    if (!string.IsNullOrEmpty(prefix) &&
                        !config.Name.Contains(prefix, StringComparison.OrdinalIgnoreCase) &&
                        !collPath.Contains(prefix, StringComparison.OrdinalIgnoreCase))
                        continue;

                    items.Add(new CompletionItem
                    {
                        Label = $"\U0001F4C1 {config.Name}:",
                        InsertText = $"@{collPath}",
                        Description = config.DisplayName ?? config.Name,
                        Path = collPath,
                        Category = "Content",
                        Kind = CompletionItemKind.Module
                    });
                    continue;
                }

                ContentCollection? collection;
                try
                {
                    collection = await contentService.GetCollectionAsync(config.Name);
                }
                catch
                {
                    continue;
                }

                if (collection == null)
                    continue;

                var collectionName = collection.Collection;

                // Add folders
                try
                {
                    var folders = await collection.GetFoldersAsync(browsePath);
                    foreach (var folder in folders)
                    {
                        if (!string.IsNullOrEmpty(filterText) &&
                            !folder.Name.Contains(filterText, StringComparison.OrdinalIgnoreCase))
                            continue;

                        var folderSubPath = (browsePath.TrimStart('/') + "/" + folder.Name).TrimStart('/');
                        var unifiedPath = string.IsNullOrEmpty(nodeAddress)
                            ? $"{collectionName}:{folderSubPath}/"
                            : $"{nodeAddress}/{collectionName}:{folderSubPath}/";

                        items.Add(new CompletionItem
                        {
                            Label = $"\U0001F4C1 {folder.Name}/",
                            InsertText = $"@{unifiedPath}",
                            Description = $"{folder.ItemCount} items",
                            Path = unifiedPath,
                            Category = "Content",
                            Kind = CompletionItemKind.Folder
                        });
                    }
                }
                catch
                {
                    // Skip folders that fail to enumerate
                }

                // Add files
                try
                {
                    var files = await collection.GetFilesAsync(browsePath);
                    foreach (var file in files)
                    {
                        if (!string.IsNullOrEmpty(filterText) &&
                            !file.Name.Contains(filterText, StringComparison.OrdinalIgnoreCase))
                            continue;

                        var filePath = file.Path.TrimStart('/');
                        var unifiedPath = string.IsNullOrEmpty(nodeAddress)
                            ? $"{collectionName}:{filePath}"
                            : $"{nodeAddress}/{collectionName}:{filePath}";

                        items.Add(new CompletionItem
                        {
                            Label = $"\U0001F4C4 {file.Name}",
                            InsertText = $"@{unifiedPath}",
                            Description = collection.DisplayName,
                            Path = unifiedPath,
                            Category = "Content",
                            Kind = CompletionItemKind.File
                        });
                    }
                }
                catch
                {
                    continue; // Skip collections that fail to enumerate
                }
            }
        }
        catch (Exception ex)
        {
            Logger.LogDebug(ex, "Error getting content completions");
        }

        return items;
    }

    /// <summary>
    /// Converts the data-bound ThreadViewModel to a message ID list.
    /// GetStream&lt;object&gt; deserializes the ThreadViewModel (has $type), so we get the typed object.
    /// </summary>
    private ThreadViewModel? ConvertThreadViewModel(object? value, ThreadViewModel? _)
    {
        var result = value switch
        {
            null => null,
            JsonElement je => je.Deserialize<ThreadViewModel?>(Hub.JsonSerializerOptions),
            AI.ThreadViewModel m => m,
            _ => throw new ArgumentException($"Cannot convert type {value.GetType().Name}.")
        };
        Logger.LogDebug("[ThreadChat:{InstanceId}] ConvertThreadViewModel: input={InputType}, msgs={MsgCount}",
            _instanceId, value?.GetType().Name ?? "null", result?.Messages?.Count ?? 0);
        return result;
    }

    /// <summary>
    /// Creates a LayoutAreaControl pointing to a ThreadMessage node's Overview layout area.
    /// </summary>
    private LayoutAreaControl GetMessageCell(string msgId)
        => new LayoutAreaControl(
            $"{threadPath}/{msgId}",
            new LayoutAreaReference(ThreadMessageNodeType.OverviewArea))
            .WithShowProgress(false);

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
            _ownedStream?.Dispose();
            agentSubscription?.Dispose();
            submissionHandler.Dispose();
            SidePanelState.OnActionRequested -= OnSidePanelAction;
        }

        return base.DisposeAsync();
    }
}
