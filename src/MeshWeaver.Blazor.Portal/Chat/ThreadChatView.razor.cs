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
    private string? lastContextUrl; // Track URL for context change detection

    // Input state
    private MonacoEditorView? monacoEditor;
    private ElementReference messagesContainer;
    private string? MessageText;
    private bool isCreatingThread;
    private bool isCancelling;
    private CancellationTokenSource? _submissionCts;
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
            // Strip layout area suffixes (e.g., "/Thread", "/Overview") from the path
            var cleanPath = path;
            var lastSlash = cleanPath.LastIndexOf('/');
            if (lastSlash > 0)
            {
                var lastSegment = cleanPath[(lastSlash + 1)..];
                // Known area names that are not part of the node path
                if (lastSegment is "Thread" or "Overview" or "History" or "Settings" or "Metadata")
                    cleanPath = cleanPath[..lastSlash];
            }

            var meshQuery = Hub.ServiceProvider.GetService<IMeshService>();
            if (meshQuery == null)
                return cleanPath;

            var node = await meshQuery.QueryAsync<MeshNode>($"path:{cleanPath}").FirstOrDefaultAsync();
            if (node == null)
                return cleanPath;

            // For satellite nodes (threads, comments): use MainNode (content entity)
            if (node.MainNode != node.Path)
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
        DataBind(ViewModel.ThreadViewModel, x => x.ThreadViewModel, ConvertThreadViewModel);
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

        // Disable input and clear the editor immediately — flush render so spinner shows
        MessageText = null;
        StateHasChanged();
        await Task.Yield(); // Let Blazor flush the render before continuing

        if (monacoEditor != null)
        {
            await monacoEditor.ClearAsync();
        }

        try
        {
            // Check for context change before sending
            await CheckAndUpdateContextAsync();

            await UpdateExtractedReferencesAsync();

            var accessService = Hub.ServiceProvider.GetService<AccessService>();
            var createdBy = accessService?.Context?.ObjectId ?? accessService?.CircuitContext?.ObjectId;
            var authorName = accessService?.Context?.Name ?? "You";

            // Generate IDs for both cells
            var userMsgId = Guid.NewGuid().ToString("N")[..8];
            var responseMsgId = Guid.NewGuid().ToString("N")[..8];

            // No pending cells — GUI creates real nodes, LayoutAreaView renders them.

            if (string.IsNullOrEmpty(threadPath))
            {
                // NEW THREAD: create thread first, then cells + submit (same as existing thread flow).
                var ns = NavigationService.CurrentNamespace ?? initialContext;
                if (string.IsNullOrEmpty(ns))
                    ns = !string.IsNullOrEmpty(createdBy) ? $"User/{createdBy}" : "User";

                // BuildThreadNode — no PendingUserMessage, no auto-execute.
                var threadNode = ThreadNodeType.BuildThreadNode(ns, userMessageText!, createdBy);
                threadPath = threadNode.Path;
                threadName = threadNode.Name;

                ThreadViewModel = new AI.ThreadViewModel
                {
                    Messages = [userMsgId, responseMsgId],
                    ThreadPath = threadPath,
                    IsExecuting = true
                };

                showSubmissionProgress = ViewModel.HideEmptyState;
                UpdateSidePanelTitle();

                var isCompact = ViewModel.HideEmptyState;
                var capturedText = userMessageText!;
                var capturedAgent = selectedAgentInfo?.Name;
                var capturedModel = selectedModelInfo?.Name;
                var capturedContext = ns;
                var capturedAttachments = attachments.Select(a => a.Path).ToList();

                // Step 1: Create thread node
                var createDelivery = Hub.Post(new CreateNodeRequest(threadNode),
                    o => o.WithTarget(new Address(ns)));
                // Observable chain: each step waits for confirmation before proceeding.
                // Step 1: Create thread → verify
                // Step 2: Create user cell → verify
                // Step 3: Create response cell → verify
                // Step 4: Submit (adds to Messages, starts execution) → verify → navigate
                if (createDelivery != null)
                {
                    PostAndVerify(createDelivery, "thread creation", createdPath =>
                    {
                        // Step 2: Create user cell
                        var userDelivery = Hub.Post(new CreateNodeRequest(new MeshNode(userMsgId, createdPath)
                        {
                            NodeType = ThreadMessageNodeType.NodeType, MainNode = capturedContext,
                            Content = new ThreadMessage
                            {
                                Role = "user", Text = capturedText, Timestamp = DateTime.UtcNow,
                                Type = ThreadMessageType.ExecutedInput, CreatedBy = createdBy
                            }
                        }), o => o.WithTarget(new Address(createdPath)));

                        PostAndVerify(userDelivery, "user cell", _ =>
                        {
                            // Step 3: Create response cell
                            var responseDelivery = Hub.Post(new CreateNodeRequest(new MeshNode(responseMsgId, createdPath)
                            {
                                NodeType = ThreadMessageNodeType.NodeType, MainNode = capturedContext,
                                Content = new ThreadMessage
                                {
                                    Role = "assistant", Text = "", Timestamp = DateTime.UtcNow,
                                    Type = ThreadMessageType.AgentResponse,
                                    AgentName = capturedAgent, ModelName = capturedModel
                                }
                            }), o => o.WithTarget(new Address(createdPath)));

                            PostAndVerify(responseDelivery, "response cell", _ =>
                            {
                                // Step 4: All cells exist — submit
                                var submitDelivery = Hub.Post(new SubmitMessageRequest
                                {
                                    ThreadPath = createdPath,
                                    UserMessageText = capturedText,
                                    UserMessageId = userMsgId,
                                    ResponseMessageId = responseMsgId,
                                    AgentName = capturedAgent,
                                    ModelName = capturedModel,
                                    ContextPath = capturedContext,
                                    Attachments = capturedAttachments
                                }, o => o.WithTarget(new Address(createdPath)));

                                // Navigate after submit response (Messages + IsExecuting set)
                                // WatchForExecution on the thread hub picks up execution.
                                if (submitDelivery != null)
                                {
                                    Hub.RegisterCallback((IMessageDelivery)submitDelivery, resp =>
                                    {
                                        InvokeAsync(() =>
                                        {
                                            showSubmissionProgress = false;
                                            if (isCompact)
                                                NavigationManager.NavigateTo($"/{createdPath}");
                                            else
                                                SidePanelState.SetContentPath(createdPath);
                                            StateHasChanged();
                                        });
                                        return resp;
                                    });
                                }
                            });
                        });
                    });
                }
            }
            else
            {
                // EXISTING THREAD: same chain — create cells with verification, then submit
                var mainEntity = initialContext ?? threadPath ?? "";
                var existingThreadPath = threadPath!;
                var existingAgent = selectedAgentInfo?.Name;
                var existingModel = selectedModelInfo?.Name;
                var existingContext = initialContext;
                var existingAttachments = attachments.Select(a => a.Path).ToList();

                var userDelivery = Hub.Post(new CreateNodeRequest(new MeshNode(userMsgId, existingThreadPath)
                {
                    NodeType = ThreadMessageNodeType.NodeType, MainNode = mainEntity,
                    Content = new ThreadMessage
                    {
                        Role = "user", Text = userMessageText!, Timestamp = DateTime.UtcNow,
                        Type = ThreadMessageType.ExecutedInput, CreatedBy = createdBy
                    }
                }), o => o.WithTarget(new Address(existingThreadPath)));

                PostAndVerify(userDelivery, "user cell", _ =>
                {
                    var responseDelivery = Hub.Post(new CreateNodeRequest(new MeshNode(responseMsgId, existingThreadPath)
                    {
                        NodeType = ThreadMessageNodeType.NodeType, MainNode = mainEntity,
                        Content = new ThreadMessage
                        {
                            Role = "assistant", Text = "", Timestamp = DateTime.UtcNow,
                            Type = ThreadMessageType.AgentResponse,
                            AgentName = existingAgent, ModelName = existingModel
                        }
                    }), o => o.WithTarget(new Address(existingThreadPath)));

                    PostAndVerify(responseDelivery, "response cell", _ =>
                    {
                        var submitDelivery = Hub.Post(new SubmitMessageRequest
                        {
                            ThreadPath = existingThreadPath,
                            UserMessageText = userMessageText!,
                            UserMessageId = userMsgId,
                            ResponseMessageId = responseMsgId,
                            AgentName = existingAgent,
                            ModelName = existingModel,
                            ContextPath = existingContext,
                            Attachments = existingAttachments
                        }, o => o.WithTarget(new Address(existingThreadPath)));

                        RegisterSubmitCallback(submitDelivery, userMessageText!);
                    });
                });
            }

            // (submit callback is registered inside PostSubmit → RegisterSubmitCallback)

            // Transition to WaitingForResponse — spinner stays until new cells appear
            submissionHandler.OnMessagePosted();

            // In compact/dashboard mode: navigate after a brief delay so user sees progress
            if (ViewModel.HideEmptyState && !string.IsNullOrEmpty(threadPath))
            {
                showSubmissionProgress = false;
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

    /// <summary>
    /// Posts a CreateNodeRequest and verifies the response before proceeding.
    /// On success: calls onSuccess with the created node's path.
    /// On failure: logs error, releases submission handler.
    /// </summary>
    private void PostAndVerify(IMessageDelivery<CreateNodeRequest>? delivery, string stepName, Action<string> onSuccess)
    {
        if (delivery == null) { ReportError(stepName, "Post returned null"); return; }
        _ = Hub.RegisterCallback((IMessageDelivery)delivery, response =>
        {
            if (response is IMessageDelivery<CreateNodeResponse> { Message.Success: true } cnr)
            {
                var path = cnr.Message.Node?.Path ?? delivery.Message.Node.Path!;
                onSuccess(path);
            }
            else
            {
                var error = (response as IMessageDelivery<CreateNodeResponse>)?.Message.Error ?? "Unknown";
                ReportError(stepName, error);
            }
            return response;
        });
    }

    private void ReportError(string step, string error)
    {
        Logger.LogError("[ThreadChat:{InstanceId}] {Step} failed: {Error}", _instanceId, step, error);
        InvokeAsync(() => { showSubmissionProgress = false; submissionHandler.ForceRelease(); StateHasChanged(); });
    }

    private void RegisterSubmitCallback(IMessageDelivery<SubmitMessageRequest>? submitDelivery,
        string userMessageText, Action? onFirstResponse = null)
    {
        if (submitDelivery == null) return;
        var firstResponseFired = false;

        void Register()
        {
            _ = Hub.RegisterCallback((IMessageDelivery)submitDelivery!, response =>
            {
                if (response is IMessageDelivery<SubmitMessageResponse> smr)
                {
                    // Fire onFirstResponse once (Messages are set on the thread now)
                    if (!firstResponseFired && smr.Message.Success)
                    {
                        firstResponseFired = true;
                        if (onFirstResponse != null)
                            InvokeAsync(onFirstResponse);
                    }

                    if (!smr.Message.Success)
                    {
                        Logger.LogError("[ThreadChat:{InstanceId}] Submit FAILED: {Error}", _instanceId, smr.Message.Error);
                        InvokeAsync(() =>
                        {
                            MessageText = userMessageText;
                            submissionHandler.ForceRelease();
                            StateHasChanged();
                        });
                    }
                    else if (smr.Message.Status == SubmitMessageStatus.ExecutionCompleted ||
                             smr.Message.Status == SubmitMessageStatus.ExecutionFailed)
                    {
                        // Execution done
                        InvokeAsync(StateHasChanged);
                    }
                    else
                    {
                        // Intermediate response — re-register for completion
                        Register();
                    }
                }
                return response;
            });
        }
        Register();
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

    private async Task OnMessageTextChanged(string value)
    {
        MessageText = value;
        await UpdateExtractedReferencesAsync();
        StateHasChanged();
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

                // Push updated list to Monaco if we got new items
                if (hadNew && monacoEditor != null)
                {
                    await InvokeAsync(async () =>
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
