using System.Reactive.Linq;
using System.Text.Json;
using MeshWeaver.AI;
using MeshWeaver.AI.Commands;
using MeshWeaver.AI.Parsing;
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
using MeshWeaver.Reactive;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.JSInterop;

namespace MeshWeaver.Blazor.Portal.Chat;

public enum ChatViewMode { Chat, ResumeThreads }

public partial class ThreadChatView : BlazorView<ThreadChatControl, ThreadChatView>
{
    [Inject] private INavigationService NavigationService { get; set; } = null!;
    [Inject] private SidePanelStateService SidePanelState { get; set; } = null!;
    [Inject] private IMeshService MeshQuery { get; set; } = null!;
    [Inject] private IChatCompletionOrchestrator CompletionOrchestrator { get; set; } = null!;

    /// <summary>
    /// Optional — when present, leading "/word args" in the user input is
    /// parsed by <see cref="ChatPreParser"/> and dispatched to the matching
    /// <see cref="IChatCommand"/> instead of being sent to the agent. Wired
    /// up by <c>AddAgentChatServices</c>.
    /// </summary>
    [Inject] private ChatCommandRegistry? CommandRegistry { get; set; }

    /// <summary>
    /// ModelTier mapping (heavy/standard/light → concrete model name). Used
    /// to resolve an agent's <c>ModelTier</c> to its actual model so the
    /// chat picker shows the SAME model the AzureClaude factory will route
    /// the request to. Without this, agents that declare only ModelTier
    /// (no PreferredModel) leave the picker on whatever happened to be
    /// selected before — so picking "haiku" can quietly send to "sonnet"
    /// because the agent's tier resolves to it server-side.
    /// </summary>
    [Inject] private IOptions<ModelTierConfiguration>? ModelTierOptions { get; set; }

    /// <summary>Stateless — single instance reused per submission.</summary>
    private static readonly ChatPreParser ChatParser = new();

    /// <summary>
    /// Most recent command-result message for the breadcrumb / status row.
    /// Cleared on the next submission.
    /// </summary>
    private string? lastCommandStatus;
    private bool lastCommandStatusIsError;

    /// <summary>
    /// Inline widget the most recent command asked us to render. <see cref="ChatWidget.None"/>
    /// hides the widget area. Driven by <see cref="CommandResult.Widget"/>.
    /// </summary>
    private ChatWidget pendingWidget = ChatWidget.None;

    private bool _isDisposed;
    private IDisposable? _navContextSubscription;
    private NavigationContext? _currentNavContext;
    private IDisposable? agentSubscription;
    private IDisposable? modelSubscription;
    private readonly string _instanceId = Guid.NewGuid().ToString("N")[..8];

    // Thread state
    private string? threadPath;
    private string? threadName;
    private string? initialContext; // Backing field for agent initialization

    /// <summary>
    /// Single live stream for the thread MeshNode — serves every read AND
    /// every write the chat performs (cancel, update sticky agent/model,
    /// append pending message). Held as a field so we don't re-open a fresh
    /// remote subscription per click; <see cref="EnsureThreadStream"/>
    /// (re)opens it when <see cref="threadPath"/> changes.
    ///
    /// <para>This mirrors the canonical [DataBinding] pattern: hold one
    /// stream, subscribe for reads, call <c>.Update(...)</c> for writes. No
    /// separate <c>IRequest</c>/<c>IResponse</c> for thread mutations.</para>
    /// </summary>
    private MeshNodeStreamHandle? _threadStream;
    private string? _threadStreamPath;

    private MeshNodeStreamHandle? EnsureThreadStream()
    {
        if (string.IsNullOrEmpty(threadPath)) return null;
        if (_threadStream is not null && string.Equals(_threadStreamPath, threadPath, StringComparison.Ordinal))
            return _threadStream;
        _threadStream = Hub.GetWorkspace().GetMeshNodeStream(threadPath);
        _threadStreamPath = threadPath;
        return _threadStream;
    }


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

                // Restore the user's sticky agent / model selection from the
                // Thread node so dropdowns survive a reload. Only fires when
                // we actually have new values — won't clobber an in-progress
                // user pick that hasn't yet been persisted.
                if (!string.IsNullOrEmpty(value.SelectedAgentName) &&
                    selectedAgentInfo?.Name != value.SelectedAgentName)
                {
                    var match = agentDisplayInfos.FirstOrDefault(a =>
                        string.Equals(a.Name, value.SelectedAgentName, StringComparison.OrdinalIgnoreCase));
                    if (match != null) selectedAgentInfo = match;
                }
                if (!string.IsNullOrEmpty(value.SelectedModelName) &&
                    selectedModelInfo?.Name != value.SelectedModelName)
                {
                    var match = availableModels.FirstOrDefault(m =>
                        string.Equals(m.Name, value.SelectedModelName, StringComparison.OrdinalIgnoreCase));
                    if (match != null) selectedModelInfo = match;
                }
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

        // Track navigation changes — subscribe to the reactive NavigationContext stream.
        _navContextSubscription = NavigationService.NavigationContext
            .Subscribe(ctx => { _currentNavContext = ctx; OnNavigationContextChanged(ctx); });

        // Set initial title
        UpdateSidePanelTitle();

        // Seed initial context attachment from NavigationService (already resolved, no query).
        if (string.IsNullOrEmpty(initialContext))
        {
            var ctx = _currentNavContext;
            if (ctx is not null && !string.IsNullOrEmpty(ctx.PrimaryPath) && ctx.Path != "chat")
            {
                var normalized = NormalizeContextPath(ctx.PrimaryPath);
                initialContext = normalized;
                if (!attachments.Any(a => a.IsContext && a.Path == normalized))
                    attachments.Add(new AttachmentInfo(normalized, ctx.Node?.Name ?? ctx.Node?.Id, IsContext: true));
            }
        }
        else
        {
            // ViewModel.InitialContext passed the raw path (e.g., side panel with ctx.PrimaryPath).
            // Look up the display name via GetDataRequest + RegisterCallback — never await.
            var capturedContext = NormalizeContextPath(initialContext);
            initialContext = capturedContext;
            if (!attachments.Any(a => a.IsContext && a.Path == capturedContext))
            {
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

            Hub.Observe((IMessageDelivery)delivery)
                .Subscribe(
                    response =>
                    {
                        try
                        {
                            if (response.Message is GetDataResponse gdr && gdr.Data is MeshNode node)
                                onResult(node.Name ?? node.Id);
                            else
                                onResult(null);
                        }
                        catch (Exception ex) when (!_isDisposed)
                        {
                            Logger.LogDebug(ex, "Error reading display name for {Path}", path);
                            onResult(null);
                        }
                    },
                    ex =>
                    {
                        if (!_isDisposed)
                            Logger.LogDebug(ex, "Error reading display name for {Path}", path);
                        onResult(null);
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
        Logger.LogDebug("[ThreadChat:{InstanceId}] IChatClientFactory instances resolved: {Count}", _instanceId, factories.Count);

        availableModels = factories
            .OrderBy(f => f.Order)
            .SelectMany(f => f.Models.Select(m => new ModelInfo
            {
                Name = m,
                Provider = f.Name,
                Order = f.Order
            }))
            .ToList();

        Logger.LogDebug("[ThreadChat:{InstanceId}] Available models ({Count}): [{Models}]",
            _instanceId, availableModels.Count, string.Join(", ", availableModels.Select(m => $"{m.Name} ({m.Provider})")));

        // Subscribe to agent MeshNodes reactively
        SubscribeToAgentNodes();
    }

    // Merged agent + model nodes from reactive queries, keyed by node path.
    // Single union query (`nodeType:Agent|Model`) gathers both — fork by
    // content type in OnAgentQueryChange.
    private readonly Dictionary<string, AgentDisplayInfo> _agentsByPath = new();
    private readonly Dictionary<string, ModelInfo> _modelsByPath = new();

    private void SubscribeToAgentNodes()
    {
        agentSubscription?.Dispose();
        modelSubscription?.Dispose();
        _agentsByPath.Clear();
        _modelsByPath.Clear();

        var workspace = Hub.ServiceProvider.GetService<IWorkspace>();
        if (workspace == null)
        {
            Logger.LogWarning("[ThreadChat:{InstanceId}] No IWorkspace — synced agent/model query skipped",
                _instanceId);
            return;
        }

        // 🚨 Two independent subscriptions through the SAME ObserveAgents /
        // ObserveModels methods that AgentPickerProjectionTest drives.
        // No query string or projection logic in this file — both live
        // in AgentPickerProjection so the dropdown can't drift from the
        // test.
        agentSubscription = AgentPickerProjection.ObserveAgents(workspace, Hub, initialContext)
            .Subscribe(agents => InvokeAsync(() => OnAgentList(agents)));
        modelSubscription = AgentPickerProjection.ObserveModels(workspace, Hub, initialContext)
            .Subscribe(models => InvokeAsync(() => OnModelList(models)));
    }

    /// <summary>
    /// Receives the full path-keyed snapshot from
    /// <see cref="SyncedQueryDataSourceExtensions.GetQuery(IWorkspace, object, string[])"/>
    /// and forks each node into agent / model bucket. Snapshot semantics
    /// are simple — every emission IS the complete current set, so we
    /// rebuild from scratch each time (no delta tracking, no flashing
    /// empty between queries' Initial events).
    /// </summary>
    private void OnAgentList(IReadOnlyList<AgentDisplayInfo> agents)
    {
        Logger.LogDebug("[ThreadChat:{InstanceId}] Agents received: count={Count}",
            _instanceId, agents.Count);

        _agentsByPath.Clear();
        foreach (var a in agents)
            if (!string.IsNullOrEmpty(a.Path))
                _agentsByPath[a.Path] = a;

        agentDisplayInfos = agents;
        ApplySelections();
        StateHasChanged();
    }

    private void OnModelList(IReadOnlyList<ModelInfo> models)
    {
        Logger.LogDebug("[ThreadChat:{InstanceId}] Models received: count={Count}",
            _instanceId, models.Count);

        _modelsByPath.Clear();
        foreach (var m in models)
            _modelsByPath[m.Name] = m;

        RebuildAvailableModels();
        ApplySelections();
        StateHasChanged();
    }

    private void ApplySelections()
    {
        if (selectedAgentInfo != null &&
            agentDisplayInfos.Any(a => a.Path == selectedAgentInfo.Path))
        {
            selectedAgentInfo = agentDisplayInfos.First(a => a.Path == selectedAgentInfo.Path);
        }
        else
        {
            // Default agent: prefer the one marked IsDefault=true (Orchestrator),
            // then fall back to the first in display order. Keeps the chat picker
            // landing on Orchestrator when no thread-side selection exists.
            selectedAgentInfo = agentDisplayInfos.FirstOrDefault(
                    a => a.AgentConfiguration?.IsDefault == true)
                ?? agentDisplayInfos.FirstOrDefault();
        }

        if (selectedAgentInfo != null)
            selectedModelInfo = GetPreferredModelInfoForAgent(selectedAgentInfo.Name);
        else
            selectedModelInfo = availableModels.FirstOrDefault();
    }

    /// <summary>
    /// Recomputes <see cref="availableModels"/> as the union of
    /// factory-provided models (from <see cref="IChatClientFactory"/>) and
    /// mesh-discovered <c>nodeType:Model</c> nodes. Mesh entries take
    /// precedence on Id collision so a user-authored Model node can
    /// override / customise a factory default.
    /// </summary>
    private void RebuildAvailableModels()
    {
        // Factory-provided baseline.
        var factoryModels = ChatClientFactories
            .OrderBy(f => f.Order)
            .SelectMany(f => f.Models.Select(m => new ModelInfo
            {
                Name = m,
                Provider = f.Name,
                Order = f.Order
            }));

        var byName = factoryModels.ToDictionary(m => m.Name, m => m, StringComparer.OrdinalIgnoreCase);

        // Mesh-defined models override on Id collision.
        foreach (var meshModel in _modelsByPath.Values)
            byName[meshModel.Name] = meshModel;

        availableModels = byName.Values
            .OrderBy(m => m.Order)
            .ThenBy(m => m.Provider, StringComparer.OrdinalIgnoreCase)
            .ThenBy(m => m.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private ModelInfo? GetPreferredModelInfoForAgent(string agentName)
    {
        if (agentModelPreferences.TryGetValue(agentName, out var preferredModelName))
            return availableModels.FirstOrDefault(m => m.Name == preferredModelName);

        var agentConfig = agentDisplayInfos.FirstOrDefault(a => a.Name == agentName)?.AgentConfiguration;

        // 1) Explicit PreferredModel on the agent wins.
        if (!string.IsNullOrEmpty(agentConfig?.PreferredModel))
        {
            var configuredModel = availableModels.FirstOrDefault(m => m.Name == agentConfig.PreferredModel);
            if (configuredModel != null)
                return configuredModel;
        }

        // 2) Resolve ModelTier (heavy/standard/light) the same way the factory does
        //    in ChatClientAgentFactory.CreateAgent — keeps the picker in sync with
        //    what the server will actually route the request to.
        if (!string.IsNullOrEmpty(agentConfig?.ModelTier))
        {
            var resolvedModelName = ModelTierOptions?.Value?.Resolve(agentConfig.ModelTier);
            if (!string.IsNullOrEmpty(resolvedModelName))
            {
                var resolved = availableModels.FirstOrDefault(m => m.Name == resolvedModelName);
                if (resolved != null)
                    return resolved;
            }
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
        // 🔬 Track submit → thread-created → first-message-visible timings.
        // Logs at Information level under channel `ChatPerf` so you can grep
        // a single resource log for "[ChatPerf]" and see step-by-step elapsed.
        var perfSw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            if (_isDisposed)
                return;

            // Use MessageText (updated via Monaco ValueChanged binding) — no blocking Monaco read.
            var userMessageText = MessageText;

            // Slash-command interception: parse leading "/word args" via
            // ChatPreParser. If a registered IChatCommand handles it,
            // dispatch and short-circuit (don't post to the agent). Tests
            // for /agent + /model live in MeshWeaver.AI.Test.
            if (!string.IsNullOrWhiteSpace(userMessageText) && CommandRegistry != null)
            {
                var parsed = ChatParser.Parse(userMessageText);
                if (parsed.Command != null)
                {
                    _ = HandleSlashCommandAsync(parsed.Command);
                    // Clear the input + bail — submissionHandler.TryBeginSubmit
                    // hasn't been called yet, so no need to release.
                    MessageText = null;
                    if (monacoEditor != null)
                        _ = ClearMonacoAsync();
                    StateHasChanged();
                    return;
                }
            }

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
                        ? createdBy
                        : "";

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
                    Logger.LogInformation(
                        "[Chat] Thread created path={Path} elapsed={Ms}ms",
                        node.Path, perfSw.ElapsedMilliseconds);
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
                Logger.LogInformation("[Chat] Creating thread + submitting message");
                ThreadSubmission.CreateThreadAndSubmit(ctx);
            }
            else
            {
                Logger.LogInformation("[Chat] Submitting to thread {Thread}", threadPath);
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

    /// <summary>
    /// Dispatches a parsed slash command through <see cref="ChatCommandRegistry"/>.
    /// Reads + writes the chat view's local agent/model state via the
    /// <see cref="CommandContext"/> callbacks; updates
    /// <see cref="lastCommandStatus"/> for the breadcrumb. No await on hub
    /// calls — the IChatCommand contract is in-process logic only.
    /// </summary>
    private async Task HandleSlashCommandAsync(ParsedCommand parsedCommand)
    {
        if (CommandRegistry == null)
            return;

        if (!CommandRegistry.TryGetCommand(parsedCommand.Name, out var command) || command == null)
        {
            lastCommandStatus = $"Unknown command: /{parsedCommand.Name}";
            lastCommandStatusIsError = true;
            await InvokeAsync(StateHasChanged);
            return;
        }

        var context = new CommandContext
        {
            ParsedCommand = parsedCommand,
            AvailableAgents = agentDisplayInfos.ToDictionary(a => a.Name, a => a, StringComparer.OrdinalIgnoreCase),
            CurrentAgent = selectedAgentInfo,
            SetCurrentAgent = a => OnAgentChanged(a),
            AvailableModels = availableModels,
            CurrentModel = selectedModelInfo,
            SetCurrentModel = m => OnModelChanged(m),
            CommandRegistry = CommandRegistry
        };

        try
        {
            var result = await command.ExecuteAsync(context);
            // When the command pops a picker widget, the picker IS the
            // response — clear lastCommandStatus so the breadcrumb shows
            // just the active-agent / active-model pills, not the long
            // "Pick an agent — or type ..." help text that's only useful
            // as a fallback for headless hosts.
            pendingWidget = result.Widget;
            if (result.Widget != ChatWidget.None)
            {
                lastCommandStatus = null;
                lastCommandStatusIsError = false;
            }
            else
            {
                lastCommandStatus = result.Message;
                lastCommandStatusIsError = !result.Success;
            }
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "[ThreadChat:{InstanceId}] /{Cmd} failed", _instanceId, parsedCommand.Name);
            lastCommandStatus = $"/{parsedCommand.Name} failed: {ex.Message}";
            lastCommandStatusIsError = true;
            pendingWidget = ChatWidget.None;
        }

        await InvokeAsync(StateHasChanged);
    }

    /// <summary>
    /// Selection callback wired to the inline agent picker.
    /// Equivalent to running <c>/agent &lt;name&gt;</c>.
    /// </summary>
    private void OnAgentPickerSelected(AgentDisplayInfo? agent)
    {
        if (agent == null) return;
        OnAgentChanged(agent);
        // The breadcrumb pill itself shows the new active agent — keep the
        // confirmation short so the row stays clean.
        lastCommandStatus = $"Switched agent → {agent.Name}";
        lastCommandStatusIsError = false;
        pendingWidget = ChatWidget.None;
        StateHasChanged();
    }

    /// <summary>
    /// Selection callback wired to the inline model picker.
    /// Equivalent to running <c>/model &lt;name&gt;</c>.
    /// </summary>
    private void OnModelPickerSelected(ModelInfo? model)
    {
        if (model == null) return;
        OnModelChanged(model);
        lastCommandStatus = $"Switched model → {model.Name}";
        lastCommandStatusIsError = false;
        pendingWidget = ChatWidget.None;
        StateHasChanged();
    }

    private void DismissWidget()
    {
        pendingWidget = ChatWidget.None;
        StateHasChanged();
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

    /// <summary>
    /// Esc on the input cancels the in-flight round (Claude.ai pattern).
    /// The typed text is preserved in MessageText so the user can re-send
    /// after the cancel completes — or just hit Send to queue it as the
    /// next round (PendingUserMessages on the thread).
    /// </summary>
    private void OnInputKeyDown(Microsoft.AspNetCore.Components.Web.KeyboardEventArgs e)
    {
        if (e.Key == "Escape" && ThreadViewModel?.IsExecuting == true && !isCancelling)
        {
            CancelExecution();
        }
    }

    private void CancelExecution()
    {
        if (string.IsNullOrEmpty(threadPath) || isCancelling)
            return;

        var stream = EnsureThreadStream();
        if (stream is null)
        {
            isCancelling = false;
            StateHasChanged();
            return;
        }

        isCancelling = true;
        StateHasChanged();

        // Stream-update cancellation: flip RequestedCancellationAt on the
        // thread node. The thread hub's cancel watcher cancels the CTS and
        // propagates to every active delegation sub-thread. The button clears
        // once IsExecuting flips false via the live thread stream.
        stream
            .Update(curr => curr?.Content is MeshWeaver.AI.Thread t
                ? curr with { Content = t with { RequestedCancellationAt = DateTime.UtcNow } }
                : curr!)
            .Subscribe(
                updated =>
                {
                    if ((updated?.Content as MeshWeaver.AI.Thread)?.RequestedCancellationAt is null)
                    {
                        Logger.LogWarning(
                            "[ThreadChat:{InstanceId}] Cancel stream.Update returned a node WITHOUT RequestedCancellationAt set for {Thread}",
                            _instanceId, threadPath);
                    }
                    isCancelling = false;
                    InvokeAsync(StateHasChanged);
                },
                ex =>
                {
                    Logger.LogWarning(ex,
                        "[ThreadChat:{InstanceId}] Cancel stream.Update failed for {Thread}",
                        _instanceId, threadPath);
                    isCancelling = false;
                    InvokeAsync(StateHasChanged);
                });
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

        var newPath = NormalizeContextPath(ctx.PrimaryPath);
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

    /// <summary>
    /// Normalizes a node path by stripping any satellite-partition suffix
    /// (segments starting with <c>_</c> such as <c>_Thread</c>, <c>_Comment</c>,
    /// <c>_Access</c>, <c>_Activity</c>, <c>_Approval</c>, <c>_Tracking</c>).
    /// Returns everything before the first such segment; returns the path
    /// unchanged when no satellite segment is present.
    /// </summary>
    private static string NormalizeContextPath(string path)
    {
        if (string.IsNullOrEmpty(path))
            return path;

        var segments = path.Split('/');
        for (var i = 0; i < segments.Length; i++)
        {
            if (segments[i].StartsWith('_'))
                return string.Join('/', segments, 0, i);
        }
        return path;
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

    /// <summary>
    /// Writes the user's sticky agent / model selection onto the
    /// <see cref="Thread"/> node so the picker re-populates after a reload.
    /// Distinct from <see cref="MeshWeaver.AI.Thread.PendingAgentName"/> /
    /// <see cref="MeshWeaver.AI.Thread.PendingModelName"/> which the server
    /// clears after each round — those describe the *next* execution, not
    /// the user's preference. No-op when no thread exists yet (the choice
    /// gets stamped onto the thread by ThreadInput.AppendUserInput when
    /// the first message is submitted).
    /// </summary>
    private void PersistSelectionOnThread(string? agentName, string? modelName)
    {
        var stream = EnsureThreadStream();
        if (stream is null) return;
        try
        {
            stream.Update(node =>
            {
                var thread = node.Content as MeshWeaver.AI.Thread ?? new MeshWeaver.AI.Thread();
                if (thread.SelectedAgentName == agentName && thread.SelectedModelName == modelName)
                    return node; // no-op
                return node with
                {
                    Content = thread with
                    {
                        SelectedAgentName = agentName ?? thread.SelectedAgentName,
                        SelectedModelName = modelName ?? thread.SelectedModelName
                    }
                };
            }).Subscribe(
                _ => { },
                ex => Logger.LogWarning(ex,
                    "[ThreadChat:{InstanceId}] Persisting selection {Agent}/{Model} on {Thread} failed",
                    _instanceId, agentName, modelName, threadPath));
        }
        catch (Exception ex)
        {
            Logger.LogDebug(ex,
                "[ThreadChat:{InstanceId}] PersistSelectionOnThread skipped (workspace unavailable)",
                _instanceId);
        }
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

        // Persist the user's sticky choice on the thread so it survives a
        // reload. Distinct from PendingAgentName which is transient (the
        // server clears it after the round runs).
        PersistSelectionOnThread(newAgent.Name, selectedModelInfo?.Name);

        StateHasChanged();
    }

    private void OnModelChanged(ModelInfo? newModel)
    {
        if (newModel?.Name == selectedModelInfo?.Name || newModel == null)
            return;

        selectedModelInfo = newModel;

        // Persist the model choice too (sticky, distinct from PendingModelName).
        PersistSelectionOnThread(selectedAgentInfo?.Name, newModel.Name);

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
            ns = userId;
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

    private const int CompletionTopN = 50;

    // Sort by SortKey ascending — AutocompleteToCompletion encodes priority into a
    // numeric prefix that puts higher-priority items first.
    private static readonly IComparer<CompletionItem> CompletionBySortKey =
        Comparer<CompletionItem>.Create((a, b) =>
            string.Compare(a.SortKey ?? "", b.SortKey ?? "", StringComparison.Ordinal));

    /// <summary>
    /// True while a completion stream is in flight. Drives the chat input's loading
    /// indicator: <c>SetCompletionsInflight(true)</c> on subscription, <c>false</c> when
    /// the orchestrator's <see cref="IObservable{T}"/> emits <c>OnCompleted</c>.
    /// </summary>
    private bool _isCompletingInflight;

    /// <summary>True while a chat-completion stream has subscribers but hasn't yet completed.</summary>
    public bool IsCompletingInflight => _isCompletingInflight;

    /// <summary>
    /// Streams top-N completion snapshots from <see cref="IChatCompletionOrchestrator"/>.
    /// The orchestrator yields batches as providers finish (fast local first, remote later);
    /// each item flows through <see cref="ObservableTopNExtensions.ScanTopN"/>, which folds it
    /// into a sorted snapshot. Monaco subscribes once per query and pushes each snapshot to
    /// the suggest widget — pure reactive, no Task, no await, no IAsyncEnumerable bridge.
    ///
    /// <para>The stream is wrapped in <c>Defer</c> + <c>Finally</c> so we know when it
    /// starts and when it completes (all providers done). That toggles
    /// <see cref="_isCompletingInflight"/> which drives the chat-input spinner via
    /// <c>StateHasChanged</c>.</para>
    ///
    /// <para><c>DistinctUntilChanged</c> over the snapshot prevents redundant push-to-JS
    /// when a producer finishes without changing the visible top-N (e.g., a partition
    /// fan-out yields items that all rank below the existing top-N).</para>
    /// </summary>
    private IObservable<IReadOnlyList<CompletionItem>> GetCompletions(string query)
    {
        if (string.IsNullOrWhiteSpace(query) || !query.StartsWith("@"))
            return Observable.Return<IReadOnlyList<CompletionItem>>(Array.Empty<CompletionItem>());

        var currentAddress = NavigationService.CurrentNamespace ?? initialContext ?? "";

        return Observable.Defer(() =>
        {
            SetCompletionsInflight(true);
            return CompletionOrchestrator.GetCompletions(query, currentAddress)
                .SelectMany(batch => batch.Items
                    .Select(item => AutocompleteToCompletion(item, batch.Category, batch.CategoryPriority)))
                .ScanTopN(CompletionTopN, CompletionBySortKey)
                .DistinctUntilChanged(SnapshotKey)
                .Catch<IReadOnlyList<CompletionItem>, Exception>(ex =>
                {
                    Logger.LogError(ex, "Error streaming completions for query: {Query}", query);
                    return Observable.Return<IReadOnlyList<CompletionItem>>(Array.Empty<CompletionItem>());
                })
                .Finally(() => SetCompletionsInflight(false));
        });
    }

    /// <summary>
    /// Toggles the chat-input's "loading" flag and re-renders. Idempotent — only
    /// fires <c>StateHasChanged</c> when the value actually changes (the
    /// <see cref="IObservable{T}"/>-equivalent is a manual <c>DistinctUntilChanged</c>
    /// guard at the sink).
    /// </summary>
    private void SetCompletionsInflight(bool inflight)
    {
        if (_isCompletingInflight == inflight) return;
        _isCompletingInflight = inflight;
        if (!_isDisposed)
            InvokeAsync(StateHasChanged);
    }

    /// <summary>
    /// Stable key for a completion-snapshot. Two consecutive snapshots collapse to a
    /// single push when their items (and their order) are identical — saves redundant
    /// JS-interop pushes when a producer finishes without changing the visible top-N.
    /// </summary>
    private static string SnapshotKey(IReadOnlyList<CompletionItem> items) =>
        string.Join('', items.Select(i => i.SortKey ?? i.InsertText ?? i.Label ?? ""));

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

    /// <summary>
    /// Maps a model to its brand badge (label + CSS color class + tooltip).
    /// Anthropic models → tier letter (O/S/H) coloured by tier; OpenAI models →
    /// stripped GPT version (4o, o1, etc.); other providers → first two letters.
    /// </summary>
    private static (string Label, string Class, string Tooltip) ModelBadge(ModelInfo model)
    {
        var name = model.Name?.ToLowerInvariant() ?? "";
        var provider = model.Provider ?? "";

        // Anthropic Claude — tier letter coloured per tier
        if (name.Contains("claude") || provider.Contains("Anthropic", StringComparison.OrdinalIgnoreCase)
                                    || provider.Contains("Claude", StringComparison.OrdinalIgnoreCase))
        {
            if (name.Contains("opus"))
                return ("O", "model-badge-anthropic-opus", $"Anthropic Opus ({model.Name})");
            if (name.Contains("sonnet"))
                return ("S", "model-badge-anthropic-sonnet", $"Anthropic Sonnet ({model.Name})");
            if (name.Contains("haiku"))
                return ("H", "model-badge-anthropic-haiku", $"Anthropic Haiku ({model.Name})");
            return ("A", "model-badge-anthropic-opus", $"Anthropic ({model.Name})");
        }

        // OpenAI / GPT — strip the gpt- prefix to leave the version
        if (name.StartsWith("gpt-") || name.StartsWith("o") && name.Length > 1 && char.IsDigit(name[1])
            || provider.Contains("OpenAI", StringComparison.OrdinalIgnoreCase)
            || provider.Contains("Foundry", StringComparison.OrdinalIgnoreCase))
        {
            var label = name.StartsWith("gpt-") ? name[4..] : name;
            // truncate after the first segment so badge stays readable (e.g. "4o-mini" → "4o")
            var dash = label.IndexOf('-');
            if (dash > 0) label = label[..dash];
            if (label.Length > 4) label = label[..4];
            return (label.ToUpperInvariant(), "model-badge-openai", $"OpenAI ({model.Name})");
        }

        // Fallback: first two letters of the model name
        var fallback = (model.Name ?? "?").Length >= 2 ? model.Name![..2].ToUpperInvariant() : "?";
        return (fallback, "model-badge-other", $"{provider} ({model.Name})");
    }

    public override ValueTask DisposeAsync()
    {
        if (!_isDisposed)
        {
            _isDisposed = true;
            _navContextSubscription?.Dispose();
            agentSubscription?.Dispose();
            modelSubscription?.Dispose();
            submissionHandler.Dispose();
            SidePanelState.OnActionRequested -= OnSidePanelAction;
        }

        return base.DisposeAsync();
    }
}
