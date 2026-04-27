using System.Reactive.Linq;
using System.Text.Json;
using MeshWeaver.AI;
using MeshWeaver.Blazor.Components;
using MeshWeaver.Blazor.Components.Monaco;
using MeshWeaver.Blazor.Portal.SidePanel;
using MeshWeaver.Data;
using MeshWeaver.Data.Completion;
using MeshWeaver.Graph;
using MeshWeaver.Layout;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;

namespace MeshWeaver.Blazor.Portal.Chat;

public enum ChatViewMode { Chat, ResumeThreads }

public partial class ThreadChatView : BlazorView<ThreadChatControl, ThreadChatView>
{
    [Inject] private INavigationService NavigationService { get; set; } = null!;
    [Inject] private SidePanelStateService SidePanelState { get; set; } = null!;
    [Inject] private IMeshService MeshQuery { get; set; } = null!;
    [Inject] private IChatCompletionOrchestrator CompletionOrchestrator { get; set; } = null!;

    private bool _isDisposed;
    private IDisposable? agentSubscription;
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
                    isCancelling = false;
                }
                // Force re-render and auto-scroll to bottom
                InvokeAsync(async () =>
                {
                    StateHasChanged();
                    await Task.Yield(); // Let render complete
                    await ScrollToBottomAsync();
                });
            }
        }
    }
    private IReadOnlyList<string> ThreadMessages => ThreadViewModel?.Messages ?? [];

    // Input state
    private MonacoEditorView? monacoEditor;
    private ElementReference messagesContainer;
    private string? MessageText;
    private readonly bool isCreatingThread;
    private bool isCancelling;
    private readonly CancellationTokenSource? _submissionCts;
    private readonly ChatSubmissionHandler submissionHandler = new();

    // Pending (optimistic) cells — rendered directly as bubbles, not via LayoutAreaView.
    // Output cells are cleared after 3s so LayoutAreaView takes over (grain should be active by then).
    // Input cells stay pending (they're static, LayoutAreaView adds nothing).
    // pendingCells removed — GUI creates real nodes, LayoutAreaView renders them directly.
    private bool showSubmissionProgress;

    // Unified attachments (context + @references)
    private readonly List<AttachmentInfo> attachments = new();
    private const string placeholderText = "Type a message... Use @ to reference nodes";

    // View mode state
    private ChatViewMode viewMode = ChatViewMode.Chat;

    // Resume threads state
    private MeshSearchControl? resumeSearchControl;

    // Agent/model selection
    private AgentDisplayInfo? selectedAgentInfo;
    private ModelInfo? selectedModelInfo;
    private IReadOnlyList<AgentDisplayInfo> agentDisplayInfos = [];
    private IReadOnlyList<ModelInfo> availableModels = [];
    private readonly Dictionary<string, string> agentModelPreferences = new();

    private IEnumerable<IChatClientFactory> ChatClientFactories => Hub.ServiceProvider.GetServices<IChatClientFactory>();

    protected override void OnInitialized()
    {
        Logger.LogDebug("[ThreadChat:{InstanceId}] OnInitialized started", _instanceId);

        // Initialize from direct ViewModel properties (side panel / dashboard case)
        threadPath ??= ViewModel.ThreadPath;
        initialContext ??= ViewModel.InitialContext;

        // Subscribe to side panel menu actions
        SidePanelState.OnActionRequested += OnSidePanelAction;

        // Track navigation changes via NavigationService — no query, no await.
        NavigationService.OnNavigationContextChanged += OnNavigationContextChanged;

        // Set initial title
        UpdateSidePanelTitle();

        // Seed initial context attachment from NavigationService (already resolved, no query).
        if (string.IsNullOrEmpty(initialContext))
        {
            var ctx = NavigationService.Context;
            if (ctx is not null && !string.IsNullOrEmpty(ctx.PrimaryPath) && ctx.Path != "chat")
            {
                initialContext = ctx.PrimaryPath;
                attachments.Add(new AttachmentInfo(ctx.PrimaryPath, ctx.Node?.Name ?? ctx.Node?.Id, IsContext: true));
            }
        }
        else
        {
            // ViewModel.InitialContext passed the raw path (e.g., side panel with ctx.PrimaryPath).
            // Look up the display name via GetDataRequest + RegisterCallback — never await.
            var capturedContext = initialContext;
            attachments.Add(new AttachmentInfo(capturedContext, null, IsContext: true));
            RequestDisplayName(capturedContext, name => InvokeAsync(() =>
            {
                if (_isDisposed) return;
                var idx = attachments.FindIndex(a => a.IsContext && a.Path == capturedContext);
                if (idx >= 0)
                    attachments[idx] = attachments[idx] with { DisplayName = name };
                StateHasChanged();
            }));
        }

        try
        {
            InitializeAgentAndModelSelections();
            Logger.LogDebug("[ThreadChat:{InstanceId}] Agent and model selections initialized", _instanceId);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "[ThreadChat:{InstanceId}] Failed to initialize agent/model selections", _instanceId);
        }

        Logger.LogDebug("[ThreadChat:{InstanceId}] OnInitialized completed", _instanceId);
    }

    /// <summary>
    /// Resolves the display name of a node at the given path via GetDataRequest.
    /// Purely Post + RegisterCallback — no query, no await.
    /// </summary>
    private void RequestDisplayName(string path, Action<string?> onResult)
    {
        if (string.IsNullOrEmpty(path))
        {
            onResult(null);
            return;
        }

        try
        {
            var delivery = Hub.Post(new GetDataRequest(new MeshNodeReference()),
                o => o.WithTarget(new Address(path)));

            if (delivery == null)
            {
                onResult(null);
                return;
            }

            Hub.RegisterCallback((IMessageDelivery)delivery, response =>
            {
                try
                {
                    if (response is IMessageDelivery<GetDataResponse> gdr && gdr.Message.Data is MeshNode node)
                        onResult(node.Name ?? node.Id);
                    else
                        onResult(null);
                }
                catch (Exception ex) when (!_isDisposed)
                {
                    Logger.LogDebug(ex, "Error reading display name for {Path}", path);
                    onResult(null);
                }
                return response;
            });
        }
        catch (Exception ex) when (!_isDisposed)
        {
            Logger.LogDebug(ex, "Error posting GetDataRequest for {Path}", path);
            onResult(null);
        }
    }

    protected override void BindData()
    {
        base.BindData();
        DataBind(ViewModel.ThreadViewModel, x => x.ThreadViewModel, ConvertThreadViewModel);
    }


    private void InitializeAgentAndModelSelections()
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

    private void SendMessage()
    {
        if (_isDisposed)
            return;

        // No await in the click path — dispatch to Blazor render context and return void.
        // All Hub operations use Post + RegisterCallback; all IMeshService operations
        // use IObservable + Subscribe. No awaits on hub-backed calls anywhere in this chain.
        _ = InvokeAsync(SubmitMessageCore);
    }

    private void SubmitMessageCore()
    {
        try
        {
            if (_isDisposed)
                return;

            // Use MessageText (updated via Monaco ValueChanged binding) — no blocking Monaco read.
            var userMessageText = MessageText;

            // Attempt to begin submission — rejects empty text and concurrent submissions
            if (!submissionHandler.TryBeginSubmit(userMessageText))
                return;

            // Disable input and clear the editor immediately — flush render so spinner shows
            MessageText = null;
            StateHasChanged();

            // Fire-and-forget Monaco clear — no await in the submit path.
            if (monacoEditor != null)
            {
                _ = ClearMonacoAsync();
            }

            var accessService = Hub.ServiceProvider.GetService<AccessService>();
            var createdBy = accessService?.Context?.ObjectId ?? accessService?.CircuitContext?.ObjectId;
            var authorName = accessService?.Context?.Name ?? "You";
            var isCompact = ViewModel.HideEmptyState;
            var capturedAttachments = attachments.Select(a => a.Path).ToList();

            var ns = !string.IsNullOrEmpty(NavigationService.CurrentNamespace)
                ? NavigationService.CurrentNamespace
                : !string.IsNullOrEmpty(initialContext)
                    ? initialContext
                    : !string.IsNullOrEmpty(createdBy)
                        ? $"User/{createdBy}"
                        : "User";

            var ctx = new SubmitContext
            {
                Hub = Hub,
                ThreadPath = string.IsNullOrEmpty(threadPath) ? null : threadPath,
                Namespace = ns,
                UserText = userMessageText!,
                AgentName = selectedAgentInfo?.Name,
                ModelName = selectedModelInfo?.Name,
                ContextPath = initialContext,
                Attachments = capturedAttachments,
                CreatedBy = createdBy,
                AuthorName = authorName,
                OnError = err => InvokeAsync(() =>
                {
                    if (_isDisposed) return;
                    Logger.LogWarning("[ThreadChat:{InstanceId}] Submit failed: {Error}", _instanceId, err);
                    showSubmissionProgress = false;
                    submissionHandler.ForceRelease();
                    StateHasChanged();
                }),
                OnThreadCreated = node => InvokeAsync(() =>
                {
                    if (_isDisposed) return;
                    threadPath = node.Path;
                    threadName = node.Name;
                    UpdateSidePanelTitle();
                    if (isCompact && !string.IsNullOrEmpty(node.Path))
                    {
                        NavigationManager.NavigateTo($"/{node.Path}");
                    }
                    else if (!string.IsNullOrEmpty(node.Path))
                    {
                        SidePanelState.SetContentPath(node.Path);
                    }
                    showSubmissionProgress = false;
                    StateHasChanged();
                })
            };

            if (string.IsNullOrEmpty(threadPath))
            {
                showSubmissionProgress = isCompact;
                ThreadSubmission.CreateThreadAndSubmit(ctx);
            }
            else
            {
                ThreadSubmission.Submit(ctx);
            }

            // Claude-Code-style queue: input stays enabled so the user can keep typing while
            // previous submissions are being processed by the thread. The server watcher
            // batches unprocessed user messages into a single round.
            submissionHandler.ForceRelease();
            StateHasChanged();
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "[ThreadChat:{InstanceId}] SubmitMessageCore failed", _instanceId);
        }
    }

    private async Task ClearMonacoAsync()
    {
        try
        {
            if (monacoEditor != null)
                await monacoEditor.ClearAsync();
        }
        catch (Exception ex) when (!_isDisposed)
        {
            Logger.LogDebug(ex, "[ThreadChat:{InstanceId}] Failed to clear Monaco editor", _instanceId);
        }
    }

    private async Task ScrollToBottomAsync()
    {
        try
        {
            await JSRuntime.InvokeVoidAsync("eval",
                "document.querySelector('.thread-chat-messages')?.scrollTo({top: 999999, behavior: 'smooth'})");
        }
        catch (Exception) when (!_isDisposed)
        {
            // Ignore JS interop failures
        }
    }

    private void CancelSubmission()
    {
        if (submissionHandler.IsInputEnabled || isCancelling)
            return;

        isCancelling = true;
        StateHasChanged();

        // Cancel any in-flight submission token
        _submissionCts?.Cancel();

        // Force release the handler — the progress bar stays visible
        // with "Cancelling..." text until the response appears or times out
        submissionHandler.ForceRelease();
        isCancelling = false;
        StateHasChanged();
    }

    private void CancelExecution()
    {
        if (string.IsNullOrEmpty(threadPath) || isCancelling)
            return;

        isCancelling = true;
        StateHasChanged();

        var delivery = Hub.Post(new CancelThreadStreamRequest { ThreadPath = threadPath },
            o => o.WithTarget(new Address(threadPath)));

        if (delivery != null)
        {
            _ = Hub.RegisterCallback(delivery, (response, _) =>
            {
                isCancelling = false;
                InvokeAsync(StateHasChanged);
                return Task.FromResult(response);
            }, CancellationToken.None);
        }
        else
        {
            isCancelling = false;
            StateHasChanged();
        }
    }

    /// <summary>
    /// Reacts to navigation context changes from INavigationService.
    /// NavigationService owns path resolution — we just read Context.PrimaryPath and Context.Node.
    /// No query, no await.
    /// </summary>
    private void OnNavigationContextChanged(NavigationContext? ctx)
    {
        if (_isDisposed) return;
        if (ctx is null || string.IsNullOrEmpty(ctx.PrimaryPath) || ctx.Path == "chat") return;

        var newPath = ctx.PrimaryPath;
        if (newPath == initialContext) return;

        var name = ctx.Node?.Name ?? ctx.Node?.Id;

        InvokeAsync(() =>
        {
            if (_isDisposed) return;
            if (newPath == initialContext) return;

            Logger.LogDebug("[ThreadChat:{InstanceId}] Context changed from {OldContext} to {NewContext}",
                _instanceId, initialContext, newPath);

            initialContext = newPath;
            attachments.RemoveAll(a => a.IsContext);
            attachments.Insert(0, new AttachmentInfo(newPath, name, IsContext: true));
            StateHasChanged();
        });
    }

    private void OnMessageTextChanged(string value)
    {
        MessageText = value;
        // Fire-and-forget reference extraction — may touch Monaco via JS interop.
        _ = UpdateExtractedReferencesAsync();
        StateHasChanged();
    }

    private void OnCompletionItemAccepted(string path)
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
            // Add the chip immediately with path-only label; resolve display name via Post/RegisterCallback.
            attachments.Add(new AttachmentInfo(path, null, IsContext: false));
            RequestDisplayName(path, displayName => InvokeAsync(() =>
            {
                if (_isDisposed) return;
                var idx = attachments.FindIndex(a => a.Path.Equals(path, StringComparison.OrdinalIgnoreCase));
                if (idx >= 0)
                    attachments[idx] = attachments[idx] with { DisplayName = displayName };
                StateHasChanged();
            }));
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
                    SidePanelState.SetContentPath(null);
                    break;
                case "Resume":
                    _ = SwitchToResumeModeAsync();
                    break;
            }
        });
    }

    // --- Mode switching ---


    private Task SwitchToResumeModeAsync()
    {
        var ns = NavigationService.CurrentNamespace;
        if (string.IsNullOrEmpty(ns))
        {
            var accessService = Hub.ServiceProvider.GetService<AccessService>();
            var userId = accessService?.Context?.ObjectId ?? accessService?.CircuitContext?.ObjectId;
            ns = !string.IsNullOrEmpty(userId) ? $"User/{userId}" : null;
        }

        var hiddenQuery = string.IsNullOrEmpty(ns)
            ? "nodeType:Thread sort:LastModified-desc"
            : $"nodeType:Thread namespace:{ns}/_Thread sort:LastModified-desc";

        resumeSearchControl = Controls.MeshSearch
            .WithHiddenQuery(hiddenQuery)
            .WithPlaceholder("Search threads...")
            .WithRenderMode(MeshSearchRenderMode.Flat)
            .WithMaxColumns(1)
            .WithDisableNavigation();

        viewMode = ChatViewMode.ResumeThreads;
        StateHasChanged();
        return Task.CompletedTask;
    }

    private MeshSearchControl? GetResumeSearchControl() => resumeSearchControl;

    private void SwitchToChatMode()
    {
        viewMode = ChatViewMode.Chat;
        StateHasChanged();
    }

    private Task OnSelectThread(MeshNode node)
    {
        threadName = node.Name;
        viewMode = ChatViewMode.Chat;
        SidePanelState.SetContentPath(node.Path);
        UpdateSidePanelTitle();
        return Task.CompletedTask;
    }

    // Thread creation is handled server-side via CreateNodeRequest.

    private CompletionProviderConfig GetCompletionConfig()
    {
        return new CompletionProviderConfig
        {
            TriggerCharacters = ["@"],
            Items = []
        };
    }

    private CancellationTokenSource? _completionCts;

    /// <summary>
    /// Main completion handler — delegates to IChatCompletionOrchestrator.
    /// Returns the first batch immediately; streams remaining batches in the background
    /// and pushes progressive updates to the Monaco widget.
    /// </summary>
    private async Task<CompletionItem[]> GetCompletionsAsync(string query)
    {
        if (string.IsNullOrWhiteSpace(query) || !query.StartsWith("@"))
            return [];

        // Cancel any previous streaming request
        _completionCts?.Cancel();
        _completionCts = new CancellationTokenSource();
        var ct = _completionCts.Token;

        try
        {
            var currentAddress = NavigationService.CurrentNamespace ?? initialContext ?? "";

            var allItems = new List<CompletionItem>();
            var isFirst = true;

            await foreach (var batch in CompletionOrchestrator.GetCompletionsAsync(query, currentAddress, ct))
            {
                foreach (var item in batch.Items)
                {
                    allItems.Add(AutocompleteToCompletion(item, batch.Category, batch.CategoryPriority));
                }

                if (isFirst)
                {
                    isFirst = false;
                    // Return first batch immediately; collect remaining in background
                    var firstResults = allItems.ToArray();
                    _ = CollectRemainingBatchesAsync(query, currentAddress, allItems, ct);
                    return firstResults;
                }
            }

            return allItems.ToArray();
        }
        catch (OperationCanceledException)
        {
            return [];
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error getting completions for query: {Query}", query);
            return [];
        }
    }

    /// <summary>
    /// Continues collecting batches from the orchestrator after the first batch was returned.
    /// Pushes progressive updates to the Monaco widget via PushCompletionUpdateAsync.
    /// </summary>
    private async Task CollectRemainingBatchesAsync(
        string query,
        string currentAddress,
        List<CompletionItem> allItems,
        CancellationToken ct)
    {
        try
        {
            // Start a new streaming call to get all batches (including any we already have).
            // Deduplication below ensures we only push genuinely new items.
            await foreach (var batch in CompletionOrchestrator.GetCompletionsAsync(query, currentAddress, ct))
            {
                var hadNew = false;
                foreach (var item in batch.Items)
                {
                    var completionItem = AutocompleteToCompletion(item, batch.Category, batch.CategoryPriority);
                    // Deduplicate by InsertText
                    if (!allItems.Any(existing =>
                        string.Equals(existing.InsertText, completionItem.InsertText, StringComparison.OrdinalIgnoreCase)))
                    {
                        allItems.Add(completionItem);
                        hadNew = true;
                    }
                }

                // Push updated list to Monaco if we got new items. Fire-and-forget by design —
                // this runs inside the streaming completion loop and must not block; errors are
                // non-fatal (debug-logged). Discard silences CS4014.
                if (hadNew && monacoEditor != null)
                {
                    _ = InvokeAsync(async () =>
                    {
                        try
                        {
                            await monacoEditor.PushCompletionUpdateAsync(allItems.ToArray());
                        }
                        catch (Exception ex)
                        {
                            Logger.LogDebug(ex, "[ThreadChat] Failed to push completion update");
                        }
                    });
                }
            }
        }
        catch (OperationCanceledException) { /* expected when user types more */ }
        catch (Exception ex)
        {
            Logger.LogDebug(ex, "[ThreadChat] Background completion collection failed");
        }
    }

    private static CompletionItem AutocompleteToCompletion(
        Data.Completion.AutocompleteItem item, string category, int categoryPriority)
    {
        // Build a sort key: lower = shown first. Invert priority so higher priority sorts first.
        var sortOrder = 9999 - Math.Min(categoryPriority + item.Priority, 9999);
        var sortKey = $"{sortOrder:D4}_{item.Label?.ToLowerInvariant()}";

        return new CompletionItem
        {
            Label = item.Path ?? item.Label!,
            InsertText = item.InsertText,
            Description = item.Description ?? category,
            Path = item.Path ?? item.Label,
            Category = item.Category ?? category,
            IconUrl = item.Icon,
            Kind = item.Kind switch
            {
                Data.Completion.AutocompleteKind.File => CompletionItemKind.File,
                Data.Completion.AutocompleteKind.Agent => CompletionItemKind.Module,
                Data.Completion.AutocompleteKind.Command => CompletionItemKind.Function,
                _ => CompletionItemKind.Text
            },
            SortKey = sortKey
        };
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
    private LayoutAreaControl? GetMessageCell(string msgId)
    {
        if (string.IsNullOrEmpty(threadPath))
            return null;
        return new LayoutAreaControl(
            $"{threadPath}/{msgId}",
            new LayoutAreaReference(ThreadMessageNodeType.OverviewArea))
            .WithSpinnerType(SpinnerType.Skeleton);
    }

    /// <summary>
    /// Creates a LayoutAreaControl pointing to the thread's Header layout area
    /// (parent-thread back-link + aggregated UpdatedNodes summary). Null when the
    /// thread doesn't exist yet. Uses <see cref="SpinnerType.None"/> — the header
    /// is ancillary; showing a skeleton for it looks like a phantom message cell.
    /// </summary>
    private LayoutAreaControl? GetHeaderCell()
    {
        if (string.IsNullOrEmpty(threadPath))
            return null;
        return new LayoutAreaControl(
            threadPath,
            new LayoutAreaReference(ThreadNodeType.HeaderArea))
            .WithSpinnerType(SpinnerType.None);
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
            NavigationService.OnNavigationContextChanged -= OnNavigationContextChanged;
            agentSubscription?.Dispose();
            submissionHandler.Dispose();
            SidePanelState.OnActionRequested -= OnSidePanelAction;
        }

        return base.DisposeAsync();
    }
}
