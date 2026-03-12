using System.Collections.Immutable;
using System.Reactive.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using MeshWeaver.AI;
using MeshWeaver.Blazor.Components.Monaco;
using MeshWeaver.Data;
using MeshWeaver.Messaging;
using MeshWeaver.Layout;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.ShortGuid;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MeshThread = MeshWeaver.AI.Thread;

using MeshWeaver.Blazor.Components;
using MeshWeaver.Blazor.Portal.SidePanel;
using MeshWeaver.ContentCollections;

namespace MeshWeaver.Blazor.Portal.Chat;

public enum ChatViewMode { Chat, ResumeThreads }

public partial class ThreadChatView : BlazorView<ThreadChatControl, ThreadChatView>
{
    [Inject] private INavigationService NavigationService { get; set; } = null!;
    [Inject] private SidePanelStateService SidePanelState { get; set; } = null!;
    [Inject] private IMeshService MeshQuery { get; set; } = null!;

    private bool _isDisposed;
    private IDisposable? agentSubscription;
    private readonly string _instanceId = Guid.NewGuid().ToString("N")[..8];

    // Thread state
    private string? threadPath;
    private string? threadName;
    private string? initialContext; // Backing field for agent initialization

    // Data-bound message cells from server-side layout
    private ImmutableList<LayoutAreaControl> _cells = ImmutableList<LayoutAreaControl>.Empty;
    private ImmutableList<LayoutAreaControl> cells
    {
        get => _cells;
        set
        {
            var previousCount = _cells.Count;
            _cells = value ?? ImmutableList<LayoutAreaControl>.Empty;
            // Release submission handler when new cells appear after a submit
            if (_cells.Count > previousCount &&
                submissionHandler.State != ChatSubmissionHandler.SubmissionState.Idle)
            {
                submissionHandler.OnResponseAppeared();
            }
        }
    }
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
        DataBind(ViewModel.ThreadPath, x => x.threadPath);
        DataBind(ViewModel.InitialContext, x => x.initialContext);
        DataBind(ViewModel.Cells, x => x.cells);
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

            // Auto-create thread on first message
            if (string.IsNullOrEmpty(threadPath))
            {
                isCreatingThread = true;
                StateHasChanged();
                try
                {
                    await AutoCreateThreadAsync(userMessageText!);
                }
                finally
                {
                    isCreatingThread = false;
                }
            }

            // Post execution request to thread hub — hub creates both user and response nodes
            Hub.Post(new ExecuteThreadMessageRequest
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
        }
        else
        {
            // Add as attachment chip if not already present
            if (!attachments.Any(a => a.Path.Equals(path, StringComparison.OrdinalIgnoreCase)))
            {
                var displayName = await ResolveContextDisplayNameAsync(path);
                attachments.Add(new AttachmentInfo(path, displayName, IsContext: false));
            }
        }

        // Wait briefly for Monaco to finish inserting the text
        await Task.Delay(50);

        // Remove the @reference from the editor text
        if (monacoEditor != null)
        {
            var currentText = await monacoEditor.GetValueAsync();
            if (!string.IsNullOrEmpty(currentText))
            {
                var cleanedText = MarkdownReferenceExtractor.RemoveReferenceByPath(currentText, path);
                if (cleanedText != currentText)
                {
                    MessageText = cleanedText;
                    await monacoEditor.SetValueAsync(cleanedText);
                }
            }
        }

        StateHasChanged();
    }

    private async Task UpdateExtractedReferencesAsync()
    {
        var currentRefs = MarkdownReferenceExtractor.GetUniquePaths(MessageText);
        var updatedText = MessageText;
        var editorNeedsUpdate = false;

        // Check if any new reference matches a known agent — if so, select that agent
        var existingPaths = attachments.Select(a => a.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var refPath in currentRefs)
        {
            if (existingPaths.Contains(refPath))
                continue;

            // Check if this path matches a known agent
            if (_agentsByPath.TryGetValue(refPath, out var agentInfo))
            {
                // Select the agent instead of adding as attachment
                OnAgentChanged(agentInfo);

                // Remove the @reference text from the editor
                if (!string.IsNullOrEmpty(updatedText))
                {
                    updatedText = MarkdownReferenceExtractor.RemoveReferenceByPath(updatedText, refPath);
                    editorNeedsUpdate = true;
                }
            }
            else
            {
                // Non-agent reference: add as attachment chip and remove @text from editor
                attachments.Add(new AttachmentInfo(refPath, DisplayName: null, IsContext: false));

                if (!string.IsNullOrEmpty(updatedText))
                {
                    updatedText = MarkdownReferenceExtractor.RemoveReferenceByPath(updatedText, refPath);
                    editorNeedsUpdate = true;
                }
            }
        }

        // Remove stale reference attachments (not in current refs, and not context)
        // Re-extract from updated text since we may have removed some references
        var remainingRefs = MarkdownReferenceExtractor.GetUniquePaths(updatedText);
        attachments.RemoveAll(a => !a.IsContext && !remainingRefs.Contains(a.Path, StringComparer.OrdinalIgnoreCase));

        // Update the editor if we removed any @text
        if (editorNeedsUpdate)
        {
            MessageText = updatedText;
            if (monacoEditor != null)
            {
                await monacoEditor.SetValueAsync(updatedText ?? "");
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
                ? "source:activity nodeType:Thread limit:20"
                : $"source:activity nodeType:Thread limit:20 {searchText}";

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
        StateHasChanged();
        return Task.CompletedTask;
    }

    // --- Auto thread creation via AI ---

    /// <summary>
    /// Creates the thread node immediately using a fallback name derived from the message text.
    /// The ThreadNamer AI agent runs in the background to rename the thread after creation.
    /// </summary>
    private async Task AutoCreateThreadAsync(string userMessageText)
    {
        var ns = NavigationService.CurrentNamespace ?? initialContext ?? "";

        // Immediate fallback: derive name and ID from message text (no AI call)
        var name = userMessageText.Length > 60
            ? userMessageText[..60] + "..."
            : userMessageText;
        var id = GenerateIdFromName(name);

        // Append short random suffix to prevent path collisions
        if (!string.IsNullOrEmpty(id))
            id += Guid.NewGuid().ToString("N")[..4];

        // Ensure we have valid values
        if (string.IsNullOrWhiteSpace(name))
            name = $"Chat {DateTime.UtcNow:yyyy-MM-dd HH:mm}";
        if (string.IsNullOrWhiteSpace(id))
            id = $"Chat{DateTime.UtcNow:yyyyMMddHHmmss}";

        threadPath = string.IsNullOrEmpty(ns) ? id : $"{ns}/{id}";
        threadName = name;

        var threadContent = new MeshThread
        {
            ParentPath = string.IsNullOrEmpty(ns) ? null : ns
        };

        var newNode = new MeshNode(threadPath)
        {
            Name = name,
            NodeType = ThreadNodeType.NodeType,
            Content = threadContent,
            MainNode = string.IsNullOrEmpty(ns) ? null : ns
        };

        try
        {
            var nodeFactory = Hub.ServiceProvider.GetRequiredService<IMeshService>();
            var accessService = Hub.ServiceProvider.GetService<AccessService>();
            var userId = accessService?.Context?.ObjectId;
            var createdNode = await nodeFactory.CreateNodeAsync(newNode);
            threadPath = createdNode.Path;
            SidePanelState.SetContentPath(threadPath);
            UpdateSidePanelTitle();
            Logger.LogDebug("[ThreadChat:{InstanceId}] Auto-created thread: {Path}", _instanceId, threadPath);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "[ThreadChat:{InstanceId}] Failed to auto-create thread, reusing existing: {Path}", _instanceId, threadPath);
            SidePanelState.SetContentPath(threadPath);
            UpdateSidePanelTitle();
        }

        // Fire-and-forget: rename thread via ThreadNamer AI in background
        _ = RenameThreadInBackgroundAsync(threadPath!, userMessageText);
    }

    /// <summary>
    /// Background task: uses ThreadNamer agent to generate a better name,
    /// then updates the thread node and side panel title.
    /// </summary>
    private async Task RenameThreadInBackgroundAsync(string targetThreadPath, string userMessageText)
    {
        try
        {
            var nameGenChat = new AgentChatClient(Hub.ServiceProvider);
            var model = selectedModelInfo?.Name ?? availableModels.FirstOrDefault()?.Name ?? "";
            await nameGenChat.InitializeAsync(initialContext, model);
            nameGenChat.SetSelectedAgent(BuiltInAgentProvider.ThreadNamerId);

            var nameRequest = new ChatMessage(ChatRole.User, userMessageText);
            var responseText = new System.Text.StringBuilder();

            await foreach (var update in nameGenChat.GetStreamingResponseAsync([nameRequest], CancellationToken.None))
            {
                if (!string.IsNullOrEmpty(update.Text))
                    responseText.Append(update.Text);
            }

            var response = responseText.ToString();
            var (name, _) = ParseNameIdResponse(response);

            if (string.IsNullOrWhiteSpace(name))
                return; // Fallback name is already functional

            Logger.LogDebug("[ThreadChat:{InstanceId}] Background rename to '{Name}' for {Path}",
                _instanceId, name, targetThreadPath);

            // Update the node name via UpdateNodeRequest
            var meshQuery = Hub.ServiceProvider.GetRequiredService<IMeshService>();
            var existingNode = await meshQuery.QueryAsync<MeshNode>($"path:{targetThreadPath}").FirstOrDefaultAsync();
            if (existingNode != null)
            {
                var updatedNode = existingNode with { Name = name };
                var nodeJson = System.Text.Json.JsonSerializer.SerializeToElement(updatedNode, Hub.JsonSerializerOptions);
                Hub.Post(new DataChangeRequest { Updates = [nodeJson] },
                    o => o.WithTarget(new Address(targetThreadPath)));
            }

            // Update local UI state if component is still alive and on the same thread
            if (!_isDisposed && threadPath == targetThreadPath)
            {
                await InvokeAsync(() =>
                {
                    threadName = name;
                    UpdateSidePanelTitle();
                    StateHasChanged();
                });
            }
        }
        catch (Exception ex)
        {
            Logger.LogDebug(ex, "[ThreadChat:{InstanceId}] Background thread rename failed for {Path}",
                _instanceId, targetThreadPath);
        }
    }

    private static (string name, string id) ParseNameIdResponse(string response)
    {
        string? name = null;
        string? id = null;

        foreach (var line in response.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (line.StartsWith("Name:", StringComparison.OrdinalIgnoreCase))
                name = line["Name:".Length..].Trim();
            else if (line.StartsWith("Id:", StringComparison.OrdinalIgnoreCase))
                id = line["Id:".Length..].Trim();
        }

        // Clean up: remove quotes if the AI wrapped them
        name = name?.Trim('"', '\'', '*');
        id = id?.Trim('"', '\'', '`');

        // Ensure id is actually PascalCase alphanumeric
        if (!string.IsNullOrEmpty(id))
            id = Regex.Replace(id, @"[^a-zA-Z0-9]", "");

        // If id is empty but name exists, generate from name
        if (string.IsNullOrEmpty(id) && !string.IsNullOrEmpty(name))
            id = GenerateIdFromName(name);

        return (name ?? "", id ?? "");
    }

    private static string GenerateIdFromName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return "";

        var words = Regex.Split(name, @"[\s\-_]+")
            .Where(w => !string.IsNullOrEmpty(w))
            .Select(w => char.ToUpperInvariant(w[0]) + w[1..].ToLowerInvariant());

        var pascalCase = string.Join("", words);
        pascalCase = Regex.Replace(pascalCase, @"[^a-zA-Z0-9]", "");

        return string.IsNullOrEmpty(pascalCase) ? "" : pascalCase;
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
        if (string.IsNullOrWhiteSpace(query))
            return [];

        try
        {
            var results = new List<CompletionItem>();

            if (query.StartsWith("@"))
            {
                var reference = query[1..];

                QuerySuggestion[] suggestions;
                if (string.IsNullOrWhiteSpace(reference))
                {
                    suggestions = await MeshQuery.AutocompleteAsync("", "", 20).ToArrayAsync();
                }
                else if (reference.EndsWith("/"))
                {
                    var basePath = reference.TrimEnd('/');
                    suggestions = await MeshQuery.AutocompleteAsync(basePath, "", 20).ToArrayAsync();
                }
                else
                {
                    suggestions = await MeshQuery.AutocompleteAsync("", reference, 20).ToArrayAsync();
                }

                results.AddRange(suggestions.Select(s => new CompletionItem
                {
                    Label = s.Path,
                    InsertText = $"@{s.Path}",
                    Description = s.NodeType ?? s.Name,
                    Path = s.Path,
                    Category = "Addresses"
                }));

                // Also include content collection files from the current context
                var contentItems = await GetContentCompletionsAsync(reference);
                results.AddRange(contentItems);
            }
            else
            {
                var suggestions = await MeshQuery.AutocompleteAsync("", query, 20).ToArrayAsync();
                results.AddRange(suggestions.Select(s => new CompletionItem
                {
                    Label = s.Path,
                    InsertText = s.Path,
                    Description = s.NodeType ?? "",
                    Path = s.Path,
                    Category = ""
                }));
            }

            return results.ToArray();
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error getting completions for query: {Query}", query);
            return [];
        }
    }

    /// <summary>
    /// Queries content collection files and returns matching completion items.
    /// Uses Unified Path format: {address}/{collectionName}:{filePath}
    /// </summary>
    private async Task<List<CompletionItem>> GetContentCompletionsAsync(string prefix)
    {
        var items = new List<CompletionItem>();

        try
        {
            var contentService = Hub.ServiceProvider.GetService<IContentService>();
            if (contentService == null)
                return items;

            // The node address forms the first part of the unified path
            var nodeAddress = initialContext ?? "";

            var configs = contentService.GetAllCollectionConfigs();
            foreach (var config in configs)
            {
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

                IReadOnlyCollection<FileItem>? files;
                try
                {
                    files = await collection.GetFilesAsync("/");
                }
                catch
                {
                    continue; // Skip collections that fail to enumerate
                }

                if (files == null)
                    continue;

                var collectionName = collection.Collection;
                foreach (var file in files)
                {
                    var filePath = file.Path.TrimStart('/');
                    // Unified path: {address}/{collectionName}:{filePath}
                    var unifiedPath = string.IsNullOrEmpty(nodeAddress)
                        ? $"{collectionName}:{filePath}"
                        : $"{nodeAddress}/{collectionName}:{filePath}";

                    // Filter by prefix if one was typed
                    if (!string.IsNullOrEmpty(prefix) &&
                        !file.Name.Contains(prefix, StringComparison.OrdinalIgnoreCase) &&
                        !unifiedPath.Contains(prefix, StringComparison.OrdinalIgnoreCase))
                        continue;

                    items.Add(new CompletionItem
                    {
                        Label = file.Name,
                        InsertText = $"@{unifiedPath}",
                        Description = collection.DisplayName,
                        Path = unifiedPath,
                        Category = "Content",
                        Kind = CompletionItemKind.File
                    });
                }
            }
        }
        catch (Exception ex)
        {
            Logger.LogDebug(ex, "Error getting content completions");
        }

        return items;
    }

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
            agentSubscription?.Dispose();
            submissionHandler.Dispose();
            SidePanelState.OnActionRequested -= OnSidePanelAction;
        }

        return base.DisposeAsync();
    }
}
