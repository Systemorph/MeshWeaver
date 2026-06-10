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
using MeshWeaver.Mesh.Security;
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
    /// per click; we resolve the cache once and call <c>Update(threadPath, fn)</c>
    /// on each write.
    ///
    /// <para>This mirrors the canonical [DataBinding] pattern: all reads +
    /// writes go through <c>IMeshNodeStreamCache</c> so the patch is observed
    /// by every reader on the same path. No per-view upstream subscription.</para>
    /// </summary>
    private IMeshNodeStreamCache? _cache;

    private IMeshNodeStreamCache? EnsureCache()
    {
        if (_cache is not null) return _cache;
        try
        {
            _cache = Hub.ServiceProvider.GetRequiredService<IMeshNodeStreamCache>();
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex,
                "[ThreadChat:{InstanceId}] Failed to resolve IMeshNodeStreamCache; thread writes will fail",
                _instanceId);
        }
        return _cache;
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

                // Rebind the data-bound composer (and the selection projection) to this thread's
                // own ThreadComposer once its id arrives. EnsureComposer creates the node if absent,
                // then opens the projection — so the embedded selectors area always has a node.
                if (!string.IsNullOrEmpty(value.ThreadComposerId) && _templatePath != value.ThreadComposerId)
                {
                    _templatePath = value.ThreadComposerId;
                    EnsureComposer();
                }
            }

            // Open per-message cache subscriptions AFTER threadPath is set —
            // DataBind invokes the value converter BEFORE this setter runs, so
            // calling SyncMessageSubscriptions from inside the converter sees
            // an empty threadPath on the first emission and bails. Result was
            // 9 skeleton bubbles forever when the upstream didn't push a
            // second time. Call here, where threadPath is guaranteed current.
            SyncMessageSubscriptions(value?.Messages ?? []);

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

    /// <summary>
    /// True when the thread has no materialised messages AND nothing queued — the brand-new /
    /// empty-thread state. Drives the full-height composer (the "start a conversation" landing):
    /// the input box fills the view instead of sitting as a thin bar at the bottom.
    /// </summary>
    private bool HasNoMessages =>
        ThreadMessages.Count == 0
        && (ThreadViewModel?.PendingMessageTexts?.Count ?? 0) == 0;

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

    // Agent/model lists — fed by AgentPickerProjection; consumed by the /agent and /model
    // slash commands + @-reference agent detection. The visible harness/agent/model SELECTION
    // is 100% data-bound to the composer node (ThreadComposerView.SelectorsArea, embedded in
    // the footer) — there is no imperative selectedAgent/selectedModel/selectedHarness state,
    // no sticky restore, no resolve/rebuild machinery. The current selection is projected
    // one-way from the composer node into the bound* fields below.
    private IReadOnlyList<AgentDisplayInfo> agentDisplayInfos = [];
    private IReadOnlyList<ModelInfo> availableModels = [];

    /// <summary>
    /// Current selection, projected one-way from the composer node (<see cref="_templatePath"/>)
    /// by <see cref="EnsureComposer"/>. Each holds the picked node's PATH; submit + resubmit
    /// pass <see cref="LastSegment"/> of it. The data-bound pickers write the node; the /agent
    /// /model commands and @-agent references write it via <see cref="WriteComposerSelection"/>.
    /// </summary>
    private string? boundHarness;
    private string? boundAgentPath;
    private string? boundModelPath;
    private IDisposable? composerSubscription;

    // ─── Per-user chat input (ThreadComposer) ───
    // The harness/agent/model selection persists on a stable ThreadComposer node — the per-user
    // singleton {userHome}/_Thread/ThreadComposer in compose mode, the thread's OWN ThreadComposer
    // (ThreadViewModel.ThreadComposerId) inside a thread. The composer's data-bound selectors area
    // (embedded below) reads + auto-persists it the standard way.
    private string? _userHome;
    private string? _templatePath;

    /// <summary>The Id (last path segment) the execution pipeline matches on, from a picked node path.</summary>
    private static string? LastSegment(string? path) =>
        string.IsNullOrEmpty(path) ? path : path.Split('/')[^1];

    /// <summary>
    /// True when viewing an existing thread created by another user. Threads are
    /// editable only by their owner, so in this case the chat input and all
    /// thread-modifying ops (stop, edit, resubmit, delete) are hidden — the thread
    /// renders read-only. The new-thread composer (no <c>threadPath</c>) is always
    /// editable, as are the current user's own threads.
    /// </summary>
    private bool IsReadOnlyThread =>
        !string.IsNullOrEmpty(threadPath)
        && !string.IsNullOrEmpty(_userHome)
        && !string.IsNullOrEmpty(ThreadViewModel?.CreatedBy)
        && !string.Equals(ThreadViewModel.CreatedBy, _userHome, StringComparison.OrdinalIgnoreCase);

    private IEnumerable<IChatClientFactory> ChatClientFactories => Hub.ServiceProvider.GetServices<IChatClientFactory>();

    protected override void OnInitialized()
    {
        Logger.LogDebug("[ThreadChat:{InstanceId}] OnInitialized started", _instanceId);

        // Initialize from direct ViewModel properties (side panel / dashboard case)
        threadPath ??= ViewModel.ThreadPath;
        initialContext ??= ViewModel.InitialContext;

        // Subscribe to side panel menu actions
        SidePanelState.OnActionRequested += OnSidePanelAction;

        // 1-second ticker for elapsed-time chips on the exec bar, sub-thread cards,
        // and per-bubble streaming chips. Only fires StateHasChanged when something's
        // executing — silent on idle threads so we don't burn render cycles when
        // there's nothing to update.
        elapsedTicker = System.Reactive.Linq.Observable
            .Interval(TimeSpan.FromSeconds(1))
            .Subscribe(_ =>
            {
                if (_isDisposed) return;
                var anyExecuting =
                    ThreadViewModel?.IsExecuting == true
                    || delegationHeaders.Values.Any(h => h.IsExecuting)
                    || messageStates.Values.Any(s => s.Status == "Streaming");
                if (anyExecuting)
                    InvokeAsync(StateHasChanged);
            });

        // Track navigation changes — subscribe to the reactive NavigationContext stream.
        _navContextSubscription = NavigationService.NavigationContext
            .Subscribe(ctx => { _currentNavContext = ctx; OnNavigationContextChanged(ctx); });

        // Set initial title
        UpdateSidePanelTitle();

        // Resolve the composer node: the per-user singleton {userHome}/_Thread/ThreadComposer in
        // compose mode, the thread's OWN ThreadComposer once its id arrives (see the ThreadViewModel
        // setter). EnsureComposer creates it with defaults if absent (robust — checked on every chat
        // open), then drives the embedded data-bound selectors area + the live selection projection.
        var accessSvc = Hub.ServiceProvider.GetService<AccessService>();
        _userHome = ResolveUserHome(accessSvc);
        if (string.IsNullOrEmpty(threadPath) && !string.IsNullOrEmpty(_userHome))
            _templatePath = MeshWeaver.AI.ThreadComposerNodeType.PathFor(_userHome);

        EnsureComposer();

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

    // ─── Per-user chat template (_ThreadTemplate) ─────────────────────────────

    /// <summary>
    /// The signed-in user's partition — the partition that owns
    /// <c>{user}/_Thread/ThreadComposer</c> and the namespace a submitted thread is created
    /// under. Prefer <see cref="AccessService.CircuitContext"/> (the durable per-circuit
    /// identity); <see cref="AccessService.Context"/> (AsyncLocal) is only a fallback and
    /// is filtered for a leaked <c>system-security</c> / hub principal. Trusting
    /// <c>Context</c> first pointed the composer at <c>system-security/_Thread/ThreadComposer</c>
    /// and would have created threads under the wrong partition.
    /// </summary>
    private static string? ResolveUserHome(AccessService? accessSvc)
    {
        if (accessSvc is null) return null;
        foreach (var candidate in new[] { accessSvc.CircuitContext?.ObjectId, accessSvc.Context?.ObjectId })
        {
            if (!string.IsNullOrEmpty(candidate)
                && candidate != WellKnownUsers.System
                && !AccessService.LooksLikeHubPrincipal(candidate))
                return candidate;
        }
        return null;
    }

    /// <summary>
    /// Robust composer bring-up, run every time the chat opens / rebinds (<see cref="_templatePath"/>).
    /// The data-bound selectors area (embedded in the footer) needs the composer node to EXIST, so we
    /// reliably create it with default content if absent via <c>MeshQuery.CreateNode</c> (never clobbers
    /// — rejects an existing node), then open the one-way selection projection into
    /// boundHarness/boundAgentPath/boundModelPath ONLY after the node is confirmed present. Fully
    /// reactive (Subscribe, never await); we never read GetMeshNodeStream on a not-yet-present node
    /// (that NotFound-storms the partition hub). Bad-data tolerant via ContentAs.
    /// </summary>
    private void EnsureComposer()
    {
        composerSubscription?.Dispose();
        boundHarness = boundAgentPath = boundModelPath = null;
        if (string.IsNullOrEmpty(_templatePath))
            return;
        var path = _templatePath;

        // Self-heal: existing users predate the onboarding seed (ThreadComposerSeedHandler), so the
        // composer node may be missing. RELIABLY create it — CreateNode registers a routable node
        // (unlike GetMeshNodeStream(path).Update, which only patches an EXISTING node and otherwise
        // NotFound-storms the partition hub — that wedged the portal). CreateNode rejects an existing
        // node (NodeAlreadyExists), so BOTH success and that benign error mean "node present" — and
        // ONLY THEN do we open the live read. We never subscribe GetMeshNodeStream on an absent node.
        MeshQuery.CreateNode(MeshWeaver.Mesh.MeshNode.FromPath(path) with
            {
                NodeType = MeshWeaver.AI.ThreadComposerNodeType.NodeType,
                Name = "Chat Input",
                Content = new MeshWeaver.AI.ThreadComposer()
            })
            .Subscribe(
                _ => InvokeAsync(() => OpenComposerProjection(path)),
                ex => InvokeAsync(() =>
                {
                    Logger.LogDebug(ex,
                        "[ThreadChat:{InstanceId}] ensure composer node (benign if already exists) {Path}", _instanceId, path);
                    OpenComposerProjection(path);
                }));
    }

    /// <summary>
    /// Opens the live one-way projection of the composer selection into bound* — called ONLY after the
    /// node is confirmed present (created or already-exists), so the read never hits a missing node.
    /// </summary>
    private void OpenComposerProjection(string path)
    {
        if (_isDisposed || _templatePath != path)
            return;
        composerSubscription?.Dispose();
        composerSubscription = Hub.GetMeshNodeStream(path)
            .Select(n => n.ContentAs<MeshWeaver.AI.ThreadComposer>(Hub.JsonSerializerOptions, Logger))
            .Where(c => c is not null)
            .Subscribe(
                c => InvokeAsync(() =>
                {
                    if (_isDisposed || _templatePath != path) return;
                    boundHarness = c!.Harness;
                    boundAgentPath = c.AgentName;
                    boundModelPath = c.ModelName;
                    StateHasChanged();
                }),
                ex => Logger.LogDebug(ex,
                    "[ThreadChat:{InstanceId}] composer projection errored for {Path}", _instanceId, path));
        StateHasChanged();
    }

    /// <summary>
    /// Writes a harness/agent/model selection onto the composer node — the imperative entry point
    /// for the /agent and /model slash-commands and @-agent references (the visual pickers write the
    /// node themselves). Values are node PATHS (agent/harness) or the model Id; a null arg leaves that
    /// field untouched. Creates the node on first write. Bad-data tolerant: an unreadable node is left
    /// alone, never clobbered.
    /// </summary>
    private void WriteComposerSelection(string? harness = null, string? agentPath = null, string? modelName = null)
    {
        if (string.IsNullOrEmpty(_templatePath))
            return;
        Hub.GetMeshNodeStream(_templatePath).Update(node =>
        {
            var existing = node.ContentAs<MeshWeaver.AI.ThreadComposer>(Hub.JsonSerializerOptions, Logger);
            if (node?.Content is not null && existing is null)
                return node!;
            var updated = (existing ?? new MeshWeaver.AI.ThreadComposer()) with
            {
                Harness = harness ?? existing?.Harness,
                AgentName = agentPath ?? existing?.AgentName,
                ModelName = modelName ?? existing?.ModelName
            };
            return node?.Content is not null
                ? node with { Content = updated }
                : MeshWeaver.Mesh.MeshNode.FromPath(_templatePath!) with
                {
                    NodeType = MeshWeaver.AI.ThreadComposerNodeType.NodeType,
                    Content = updated
                };
        }).Subscribe(
            _ => { },
            ex => Logger.LogDebug(ex,
                "[ThreadChat:{InstanceId}] composer selection write failed for {Path}", _instanceId, _templatePath));
    }

    /// <summary>
    /// Resolves the display name of a node at the given path via the
    /// timeout-bounded <c>Hub.GetMeshNode</c> helper. Bounded by an internal
    /// 5 s deadline — for missing or unroutable paths the helper returns
    /// null (instead of leaving a hub callback dangling indefinitely, which
    /// is what the prior direct-Post + Observe shape did and what surfaced
    /// in prod 2026-05-24 as the chat-page SSR hang when a satellite at the
    /// requested path didn't exist).
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
            Hub.GetMeshNode(path, TimeSpan.FromSeconds(5))
                .Subscribe(
                    node =>
                    {
                        if (_isDisposed) return;
                        onResult(node?.Name ?? node?.Id);
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
            Logger.LogDebug(ex, "Error reading display name for {Path}", path);
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
        StateHasChanged();
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

            Action<string> onError = err => InvokeAsync(() =>
            {
                if (_isDisposed) return;
                Logger.LogWarning("[ThreadChat:{InstanceId}] Submit failed: {Error}", _instanceId, err);
                showSubmissionProgress = false;
                submissionHandler.ForceRelease();
                StateHasChanged();
            });

            if (string.IsNullOrEmpty(threadPath))
            {
                showSubmissionProgress = isCompact;
                Logger.LogInformation("[Chat] Creating thread + submitting message");
                Hub.StartThread(
                    namespacePath: ns,
                    userText: userMessageText!,
                    agentName: LastSegment(boundAgentPath),
                    modelName: LastSegment(boundModelPath),
                    contextPath: initialContext,
                    attachments: capturedAttachments,
                    createdBy: createdBy,
                    authorName: authorName,
                    harness: LastSegment(boundHarness) ?? Harnesses.MeshWeaver,
                    onCreated: node => InvokeAsync(() =>
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
                    }),
                    onError: onError);
            }
            else
            {
                Logger.LogInformation("[Chat] Submitting to thread {Thread}", threadPath);
                Hub.SubmitMessage(
                    threadPath: threadPath,
                    userText: userMessageText!,
                    agentName: LastSegment(boundAgentPath),
                    modelName: LastSegment(boundModelPath),
                    contextPath: initialContext,
                    attachments: capturedAttachments,
                    createdBy: createdBy,
                    authorName: authorName,
                    onError: onError,
                    harness: LastSegment(boundHarness) ?? Harnesses.MeshWeaver);
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
            CurrentAgent = agentDisplayInfos.FirstOrDefault(a => a.Path == boundAgentPath),
            SetCurrentAgent = a => WriteComposerSelection(agentPath: a.Path),
            CurrentModel = availableModels.FirstOrDefault(m => string.Equals(m.Name, LastSegment(boundModelPath), StringComparison.OrdinalIgnoreCase)),
            SetCurrentModel = m => WriteComposerSelection(modelName: m.Name),
            AvailableModels = availableModels,
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

        var cache = EnsureCache();
        if (cache is null || string.IsNullOrEmpty(threadPath))
        {
            isCancelling = false;
            StateHasChanged();
            return;
        }

        isCancelling = true;
        StateHasChanged();

        // Stream-update cancellation: set RequestedStatus = Cancelled on the
        // thread node through the process-wide cache. The thread hub's cancel
        // watcher cancels the CTS and propagates to every active delegation
        // sub-thread. The button clears once IsExecuting flips false via the
        // live thread stream (which every other reader is subscribed to on
        // the same shared cache handle).
        Hub.GetMeshNodeStream(threadPath)
            .Update(curr => curr?.Content is MeshWeaver.AI.Thread t
                ? curr with { Content = t with { RequestedStatus = MeshWeaver.AI.ThreadExecutionStatus.Cancelled } }
                : curr!)
            .Subscribe(
                updated =>
                {
                    if ((updated?.Content as MeshWeaver.AI.Thread)?.RequestedStatus is null)
                    {
                        Logger.LogWarning(
                            "[ThreadChat:{InstanceId}] Cancel stream.Update returned a node WITHOUT RequestedStatus set for {Thread}",
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
            WriteComposerSelection(agentPath: agentInfo.Path);
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
                WriteComposerSelection(agentPath: agentInfo.Path);
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
                    viewMode = ChatViewMode.Chat;
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
    /// each item flows through <see cref="MeshWeaver.Reactive.ObservableTopNExtensions.ScanTopN{T}(System.IObservable{T}, int, System.Collections.Generic.IComparer{T})"/>, which folds it
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
    /// Converts the data-bound ThreadViewModel to a message ID list. Also
    /// syncs per-message cache subscriptions so the inline bubble render in
    /// <see cref="ThreadChatView"/>'s Razor template gets live ThreadMessage
    /// content via <see cref="messageStates"/>.
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
        // SyncMessageSubscriptions runs in the property setter (below) — calling
        // here is too early: threadPath is set by the setter AFTER conversion.
        return result;
    }

    // ─── Inline bubble subscriptions ──────────────────────────────────────
    // Per-message live state, keyed by message id. Populated by
    // SyncMessageSubscriptions opening one IMeshNodeStreamCache subscription
    // per visible id. Razor template iterates ThreadMessages and renders
    // each bubble inline using messageStates[id].
    private record MessageBubbleState(
        string Role,
        string AuthorName,
        string? ModelName,
        DateTime? Timestamp,
        string? Text,
        IReadOnlyList<ToolCallEntry>? ToolCalls,
        IReadOnlyList<NodeChangeEntry>? UpdatedNodes,
        string? Status = null,
        DateTime? CompletedAt = null,
        string? Harness = null,
        int? InputTokens = null,
        int? OutputTokens = null);

    private readonly Dictionary<string, MessageBubbleState> messageStates = new();
    private readonly Dictionary<string, IDisposable> messageSubs = new();
    private readonly HashSet<string> editingMessages = new();
    /// <summary>Message ids whose satellite cell did NOT emit within the cache
    /// settle window — surfaced as "Missing message" in the bubble instead of
    /// the loading skeleton. A deleted-by-someone-else or never-materialised
    /// satellite would otherwise leave the bubble stuck on a skeleton forever
    /// and (in prod 2026-05-24) hang any code path that does a GetDataRequest
    /// on the same path.</summary>
    private readonly HashSet<string> missingMessages = new();
    private readonly Dictionary<string, IDisposable> missingProbes = new();

    /// <summary>Live state for a delegated sub-thread. <see cref="Title"/> is the
    /// sub-thread MeshNode's <c>Name</c> (ThreadNamer-generated or user-edited);
    /// <see cref="Icon"/> is the node's <c>Icon</c> property; <see cref="IsExecuting"/>
    /// drives the runtime panel — running sub-threads show a live row, completed
    /// ones drop out. <see cref="ExecutionStatus"/> + <see cref="StreamingText"/>
    /// feed the inline progress preview ("Calling search_nodes…" / first 120 chars
    /// of the streaming response). <see cref="StartedAt"/> drives the elapsed-time
    /// chip; null on a freshly-created sub-thread before its first
    /// StartingExecution → Executing flip.</summary>
    private record DelegationHeader(
        string? Title,
        string? Icon,
        bool IsExecuting,
        string? ExecutionStatus,
        string? StreamingText,
        DateTime? StartedAt);

    /// <summary>delegationPath → live header (Title + Icon) so the chip can show
    /// the sub-thread's actual name instead of just the agent's name. Populated by
    /// subscriptions opened from <see cref="SyncDelegationSubscriptions"/>.</summary>
    private readonly Dictionary<string, DelegationHeader> delegationHeaders = new();
    private readonly Dictionary<string, IDisposable> delegationSubs = new();

    private void SyncMessageSubscriptions(IReadOnlyList<string> messageIds)
    {
        if (_isDisposed || string.IsNullOrEmpty(threadPath))
            return;

        IMeshNodeStreamCache? cache;
        try
        {
            cache = Hub.ServiceProvider.GetRequiredService<IMeshNodeStreamCache>();
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex,
                "[ThreadChat:{InstanceId}] IMeshNodeStreamCache unavailable — bubbles will not update live",
                _instanceId);
            return;
        }
        var accessService = Hub.ServiceProvider.GetService<AccessService>();

        var idSet = messageIds.ToHashSet(StringComparer.Ordinal);
        var stale = messageSubs.Keys.Where(id => !idSet.Contains(id)).ToList();
        foreach (var id in stale)
        {
            messageSubs[id].Dispose();
            messageSubs.Remove(id);
            messageStates.Remove(id);
            editingMessages.Remove(id);
            if (missingProbes.Remove(id, out var probe)) probe.Dispose();
            missingMessages.Remove(id);
        }

        foreach (var id in messageIds)
        {
            if (messageSubs.ContainsKey(id)) continue;
            var nodePath = $"{threadPath}/{id}";

            // 🚨 Subscribe INSIDE the ImpersonateAsSystem scope.
            // `cache.GetStream` returns a cold IObservable whose RLS gate
            // resolves at Subscribe-time (not at GetStream-time). The
            // framework carries the AccessContext captured at Subscribe
            // through to the gate via CarryAccessContext. If we subscribe
            // after the using block closes, the gate sees the Blazor
            // circuit's identity in a sync-emission scope and silently
            // rejects every emission — symptom: empty skeleton bars.
            // Same pattern UserActivityLayoutAreas.cs:42-67 documents.
            var capturedId = id;
            using (accessService?.ImpersonateAsSystem())
            {
                var stream = Hub.GetMeshNodeStream(nodePath);
                messageSubs[id] = stream
                    .Where(n => n?.Content is not null)
                    .Subscribe(
                        n =>
                        {
                            // Real content arrived — drop any "missing" mark and the
                            // probe; the bubble will render normally.
                            if (missingProbes.Remove(capturedId, out var probe)) probe.Dispose();
                            if (missingMessages.Remove(capturedId))
                                InvokeAsync(StateHasChanged);
                            UpdateMessageState(capturedId, n);
                        },
                        ex =>
                        {
                            // 🚨 CRITICAL: handle errors here. The cache surfaces
                            // missing satellites as OnError(DeliveryFailureException)
                            // — without this handler the exception is unhandled and
                            // crashes the Blazor circuit (the user-visible
                            // "still crashing / stuck on progress screen" symptom
                            // in prod 2026-05-24). Mark the bubble as missing and
                            // re-render. Reproduced by
                            // test/MeshWeaver.Threading.Test/MissingSatelliteTest.
                            Logger.LogDebug(ex,
                                "[ThreadChat:{InstanceId}] cache.GetStream errored for {NodePath} — marking message as missing",
                                _instanceId, nodePath);
                            InvokeAsync(() =>
                            {
                                if (_isDisposed) return;
                                if (missingProbes.Remove(capturedId, out var probe)) probe.Dispose();
                                if (missingMessages.Add(capturedId))
                                    StateHasChanged();
                            });
                        });
            }

            // Missing-message probe — backup for the case where the cache stream
            // neither emits content nor errors within the deadline (cold-observable
            // starvation; not the path the OnError above catches). Surfaces the
            // bubble as "missing" so the GUI never gets stuck on an indefinite
            // skeleton.
            var probeDelay = TimeSpan.FromSeconds(5);
            missingProbes[id] = System.Reactive.Linq.Observable
                .Timer(probeDelay)
                .Subscribe(_ => InvokeAsync(() =>
                {
                    if (_isDisposed) return;
                    if (!messageStates.ContainsKey(capturedId) && missingMessages.Add(capturedId))
                        StateHasChanged();
                }));
        }
    }

    private void UpdateMessageState(string id, MeshNode node)
    {
        if (_isDisposed) return;
        var je = ToJsonElement(node.Content!, Hub.JsonSerializerOptions);

        var role = je.TryGetProperty("role", out var roleProp) && roleProp.ValueKind == JsonValueKind.String
            ? roleProp.GetString() ?? "user" : "user";
        var explicitAuthor = je.TryGetProperty("authorName", out var aProp) && aProp.ValueKind == JsonValueKind.String
            ? aProp.GetString() : null;
        var agentName = je.TryGetProperty("agentName", out var agProp) && agProp.ValueKind == JsonValueKind.String
            ? agProp.GetString() : null;
        var author = explicitAuthor
            ?? (role.Equals("user", StringComparison.OrdinalIgnoreCase)
                ? "You" : (agentName ?? "Assistant"));
        var modelName = je.TryGetProperty("modelName", out var mProp) && mProp.ValueKind == JsonValueKind.String
            ? mProp.GetString() : null;
        DateTime? timestamp = je.TryGetProperty("timestamp", out var tsProp) && tsProp.ValueKind == JsonValueKind.String
            && DateTime.TryParse(tsProp.GetString(), out var parsed) ? parsed : null;
        var text = je.TryGetProperty("text", out var textProp) && textProp.ValueKind == JsonValueKind.String
            ? textProp.GetString() : null;
        IReadOnlyList<ToolCallEntry>? toolCalls = je.TryGetProperty("toolCalls", out var tcProp)
            && tcProp.ValueKind == JsonValueKind.Array
                ? tcProp.Deserialize<List<ToolCallEntry>>(Hub.JsonSerializerOptions) : null;
        IReadOnlyList<NodeChangeEntry>? updatedNodes = je.TryGetProperty("updatedNodes", out var unProp)
            && unProp.ValueKind == JsonValueKind.Array
                ? unProp.Deserialize<List<NodeChangeEntry>>(Hub.JsonSerializerOptions) : null;
        // Status + CompletedAt drive the per-bubble duration chip — "1:23" while
        // Streaming (live ticker), "1:23 ✓" once Completed (frozen final value).
        var status = je.TryGetProperty("status", out var stProp) && stProp.ValueKind == JsonValueKind.String
            ? stProp.GetString() : null;
        DateTime? completedAt = je.TryGetProperty("completedAt", out var caProp)
            && caProp.ValueKind == JsonValueKind.String
            && DateTime.TryParse(caProp.GetString(), out var ca) ? ca : null;
        // Harness + token usage drive the assistant meta line "Harness · time · N in / M out".
        var harness = je.TryGetProperty("harness", out var hProp) && hProp.ValueKind == JsonValueKind.String
            ? hProp.GetString() : null;
        int? inputTokens = je.TryGetProperty("inputTokens", out var itProp) && itProp.ValueKind == JsonValueKind.Number
            ? itProp.GetInt32() : null;
        int? outputTokens = je.TryGetProperty("outputTokens", out var otProp) && otProp.ValueKind == JsonValueKind.Number
            ? otProp.GetInt32() : null;

        var newState = new MessageBubbleState(role, author, modelName, timestamp, text, toolCalls, updatedNodes, status, completedAt, harness, inputTokens, outputTokens);
        var prev = messageStates.GetValueOrDefault(id);
        if (Equals(prev, newState)) return;

        messageStates[id] = newState;
        SyncDelegationSubscriptions();
        InvokeAsync(StateHasChanged);
    }

    /// <summary>
    /// Opens (and tears down) per-sub-thread MeshNode subscriptions so the
    /// delegation chips can render the sub-thread's actual Name + Icon instead
    /// of just the agent name. Called from <see cref="UpdateMessageState"/>
    /// whenever a bubble's ToolCalls list changes — picks up newly-emitted
    /// DelegationPaths and drops subscriptions for paths no longer referenced
    /// by any current bubble.
    /// </summary>
    private void SyncDelegationSubscriptions()
    {
        if (_isDisposed) return;

        var activePaths = messageStates.Values
            .SelectMany(s => s.ToolCalls ?? (IReadOnlyList<ToolCallEntry>)[])
            .Select(c => c.DelegationPath)
            .Where(p => !string.IsNullOrEmpty(p))
            .Cast<string>()
            .ToHashSet(StringComparer.Ordinal);

        // Drop subscriptions for delegations no longer in any bubble's tool calls
        // (e.g. message edited / deleted-from-here truncated the tail).
        var stale = delegationSubs.Keys.Where(p => !activePaths.Contains(p)).ToList();
        foreach (var p in stale)
        {
            delegationSubs[p].Dispose();
            delegationSubs.Remove(p);
            delegationHeaders.Remove(p);
        }

        if (activePaths.Count == 0) return;

        IMeshNodeStreamCache? cache;
        try
        {
            cache = Hub.ServiceProvider.GetRequiredService<IMeshNodeStreamCache>();
        }
        catch
        {
            // No cache available (minimal test fixture) — chips fall back to
            // the agent-name summary, which is fine.
            return;
        }
        var accessService = Hub.ServiceProvider.GetService<AccessService>();

        foreach (var path in activePaths)
        {
            if (delegationSubs.ContainsKey(path)) continue;

            // Same ImpersonateAsSystem pattern as SyncMessageSubscriptions —
            // the cache's RLS gate resolves at Subscribe-time; without the
            // system scope the gate sees the Blazor circuit's identity and
            // can deny silently. Parent-thread access was already gated when
            // the user opened this chat; the chip read piggybacks on that.
            using (accessService?.ImpersonateAsSystem())
            {
                var stream = Hub.GetMeshNodeStream(path);
                delegationSubs[path] = stream
                    .Where(n => n is not null)
                    .Subscribe(
                        n => UpdateDelegationHeader(path, n),
                        ex => Logger.LogDebug(ex,
                            "[ThreadChat:{InstanceId}] delegation cache.GetStream errored for {Path} — chip falls back to agent-name summary",
                            _instanceId, path));
            }
        }
    }

    private void UpdateDelegationHeader(string path, MeshNode node)
    {
        if (_isDisposed) return;
        // Parse the Thread content for live execution state. The Title/Icon come
        // from the MeshNode envelope; IsExecuting + ExecutionStatus + StreamingText
        // live inside Content (MeshThread record). We use JsonElement parsing so
        // this view doesn't have to take a hard dependency on MeshWeaver.AI types
        // — same shape UpdateMessageState uses for ThreadMessage.
        bool isExecuting = false;
        string? executionStatus = null;
        string? streamingText = null;
        DateTime? startedAt = null;
        if (node.Content is not null)
        {
            var je = ToJsonElement(node.Content, Hub.JsonSerializerOptions);
            // MeshThread.IsExecuting is a computed `[JsonIgnore]` property — read
            // Status directly and recompute (StartingExecution / Executing →
            // executing). Order matches Thread.cs Status enum: Idle=0,
            // StartingExecution=1, Executing=2, Cancelled=3, Done=4 — so 3
            // (Cancelled) is NOT executing.
            if (je.TryGetProperty("status", out var statusProp))
            {
                var s = statusProp.ValueKind == JsonValueKind.String
                    ? statusProp.GetString()
                    : statusProp.ValueKind == JsonValueKind.Number
                        ? statusProp.GetInt32().ToString()
                        : null;
                isExecuting = s is "StartingExecution" or "Executing" or "1" or "2";
            }
            if (je.TryGetProperty("executionStatus", out var esProp) && esProp.ValueKind == JsonValueKind.String)
                executionStatus = esProp.GetString();
            if (je.TryGetProperty("streamingText", out var stProp) && stProp.ValueKind == JsonValueKind.String)
                streamingText = stProp.GetString();
            // Drives the elapsed-time chip on the sub-thread card.
            if (je.TryGetProperty("executionStartedAt", out var startProp)
                && startProp.ValueKind == JsonValueKind.String
                && startProp.TryGetDateTime(out var parsed))
                startedAt = parsed;
        }

        var header = new DelegationHeader(
            Title: string.IsNullOrEmpty(node.Name) ? null : node.Name,
            Icon: string.IsNullOrEmpty(node.Icon) ? null : node.Icon,
            IsExecuting: isExecuting,
            ExecutionStatus: executionStatus,
            StreamingText: streamingText,
            StartedAt: startedAt);
        var prev = delegationHeaders.GetValueOrDefault(path);
        if (Equals(prev, header)) return;
        delegationHeaders[path] = header;
        InvokeAsync(StateHasChanged);
    }

    private DelegationHeader? GetDelegationHeader(string? path) =>
        string.IsNullOrEmpty(path) ? null : delegationHeaders.GetValueOrDefault(path);

    /// <summary>
    /// Enumerates all currently-executing sub-threads launched from this thread's
    /// bubbles — drives the runtime panel below the chat. Deduplicated by path
    /// (an agent can re-delegate to the same sub-thread; we render one card per
    /// distinct path). Order: first appearance in tool-call traversal, so the
    /// list is stable across re-renders.
    /// </summary>
    private IEnumerable<(string Path, DelegationHeader Header)> GetRunningSubThreads()
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var state in messageStates.Values)
        {
            if (state.ToolCalls is null) continue;
            foreach (var call in state.ToolCalls)
            {
                if (string.IsNullOrEmpty(call.DelegationPath)) continue;
                if (!seen.Add(call.DelegationPath)) continue;
                var header = delegationHeaders.GetValueOrDefault(call.DelegationPath);
                if (header is { IsExecuting: true })
                    yield return (call.DelegationPath, header);
            }
        }
    }

    // Use the hub's options (camelCase property naming) so the field-name
    // lookups below ("role", "text", "agentName" …) match what the wire
    // serializer produced. With default options the serialiser emits "Role"
    // / "Text" and every TryGetProperty miss falls through to defaults —
    // symptom: every bubble labelled "You" with no message text.
    private static JsonElement ToJsonElement(object content, JsonSerializerOptions options)
        => content is JsonElement je ? je
            : JsonSerializer.SerializeToElement(content, options);

    private MessageBubbleState? GetMessageState(string id) => messageStates.GetValueOrDefault(id);

    private bool IsMissing(string id) => missingMessages.Contains(id);

    private bool IsEditing(string id) => editingMessages.Contains(id);

    private void StartEdit(string id)
    {
        editingMessages.Add(id);
        StateHasChanged();
    }

    private void CancelEdit(string id)
    {
        editingMessages.Remove(id);
        StateHasChanged();
    }

    private void ResubmitMessage(string id)
    {
        var state = GetMessageState(id);
        if (state == null || string.IsNullOrEmpty(threadPath)) return;
        var outId = Guid.NewGuid().ToString("N")[..8];
        Hub.Post(new CreateNodeRequest(new MeshNode(outId, threadPath)
        {
            NodeType = ThreadMessageNodeType.NodeType,
            MainNode = threadPath,
            Content = new ThreadMessage
            {
                Role = "assistant",
                Text = "",
                Timestamp = DateTime.UtcNow,
                Type = ThreadMessageType.AgentResponse
            }
        }), o => o.WithTarget(new Address(threadPath)));
        Hub.ResubmitMessage(threadPath, id, newUserText: state.Text ?? "",
            agentName: LastSegment(boundAgentPath), modelName: LastSegment(boundModelPath), harness: LastSegment(boundHarness));
    }

    private void DeleteFromMessage(string id)
    {
        if (string.IsNullOrEmpty(threadPath)) return;
        Hub.DeleteFromMessage(threadPath, id);
    }

    // ─── Tool-call display helpers ────────────────────────────────────────

    private readonly record struct ToolCallDisplay(string Verb, string? Path, bool IsNodeModifying);

    private static string FormatToolCallSummary(ToolCallEntry call)
    {
        var d = FormatToolCallDisplay(call);
        return d.Path is null ? d.Verb : $"{d.Verb} {d.Path}";
    }

    private static ToolCallDisplay FormatToolCallDisplay(ToolCallEntry call)
    {
        if (!string.IsNullOrEmpty(call.DelegationPath))
        {
            var name = call.DisplayName ?? call.Name;
            if (name.Contains("Delegating to "))
                name = name.Replace("Delegating to ", "").TrimEnd('.', ' ');
            return new ToolCallDisplay(name, null, false);
        }
        var rawArgs = call.Arguments ?? "";
        string? path = null;
        foreach (var line in rawArgs.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("path:", StringComparison.OrdinalIgnoreCase)) { path = trimmed["path:".Length..].Trim(); break; }
            if (trimmed.StartsWith("url:", StringComparison.OrdinalIgnoreCase)) { path = trimmed["url:".Length..].Trim(); break; }
            if (trimmed.StartsWith("query:", StringComparison.OrdinalIgnoreCase)) { path = trimmed["query:".Length..].Trim(); break; }
        }
        if (string.IsNullOrEmpty(path))
            path = rawArgs.Split('\n').FirstOrDefault()?.Trim();
        if (!string.IsNullOrEmpty(path) && path.StartsWith('@'))
            path = path[1..].TrimStart('/');

        return call.Name switch
        {
            "Get" or "get_node" => new ToolCallDisplay("Reading", path, false),
            "Search" or "search_nodes" => new ToolCallDisplay("Searching", path, false),
            "Create" or "create_node" => new ToolCallDisplay("Created", path, true),
            "Update" or "update_node" => new ToolCallDisplay("Updated", path, true),
            "Patch" or "patch_node" => new ToolCallDisplay("Patched", path, true),
            "Delete" or "delete_node" => new ToolCallDisplay("Deleted", path, true),
            "NavigateTo" or "navigate_to" => new ToolCallDisplay("Navigating to", path, false),
            "SearchWeb" => new ToolCallDisplay("Searching web for", path, false),
            "FetchWebPage" => new ToolCallDisplay("Fetching", path, false),
            "store_plan" => new ToolCallDisplay("Stored plan", null, false),
            _ => new ToolCallDisplay(call.DisplayName ?? call.Name, path, false)
        };
    }

    private static NodeChangeEntry? FindChange(IReadOnlyList<NodeChangeEntry>? updatedNodes, string? path)
    {
        if (string.IsNullOrEmpty(path) || updatedNodes is null) return null;
        return updatedNodes.FirstOrDefault(n => string.Equals(n.Path, path, StringComparison.Ordinal));
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
    /// Compact elapsed-time formatter for "how long has this been running".
    /// Returns <c>"0:12"</c> for &lt; 1 h, <c>"1:23:45"</c> for &gt;= 1 h.
    /// Negative or null clamps to <c>"0:00"</c> so a clock-skew anomaly
    /// doesn't render <c>-3:14</c>.
    /// </summary>
    private static string FormatElapsed(DateTime? startedAt, DateTime? endedAt = null)
    {
        if (startedAt is null) return "0:00";
        var end = endedAt ?? DateTime.UtcNow;
        var span = end - startedAt.Value;
        if (span < TimeSpan.Zero) span = TimeSpan.Zero;
        return span.TotalHours >= 1
            ? $"{(int)span.TotalHours}:{span.Minutes:D2}:{span.Seconds:D2}"
            : $"{span.Minutes}:{span.Seconds:D2}";
    }

    /// <summary>
    /// 1-second ticker that drives the elapsed-time chips' re-render. Subscribed
    /// in <see cref="OnInitialized"/>; disposed in <see cref="DisposeAsync"/>.
    /// Only triggers <c>StateHasChanged</c> when <em>something</em> is
    /// executing (own thread or a sub-thread) — silent otherwise so an idle
    /// thread view doesn't burn render cycles every second.
    /// </summary>
    private IDisposable? elapsedTicker;

    public override ValueTask DisposeAsync()
    {
        if (!_isDisposed)
        {
            _isDisposed = true;
            elapsedTicker?.Dispose();
            _navContextSubscription?.Dispose();
            composerSubscription?.Dispose();
            agentSubscription?.Dispose();
            modelSubscription?.Dispose();
            submissionHandler.Dispose();
            foreach (var sub in messageSubs.Values) sub.Dispose();
            messageSubs.Clear();
            messageStates.Clear();
            foreach (var sub in missingProbes.Values) sub.Dispose();
            missingProbes.Clear();
            missingMessages.Clear();
            foreach (var sub in delegationSubs.Values) sub.Dispose();
            delegationSubs.Clear();
            delegationHeaders.Clear();
            SidePanelState.OnActionRequested -= OnSidePanelAction;
        }

        return base.DisposeAsync();
    }
}
