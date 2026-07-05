using System.Reactive.Linq;
using System.Text.Json;
using MeshWeaver.AI;
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
using Microsoft.Extensions.Configuration;
using Microsoft.FluentUI.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.JSInterop;

namespace MeshWeaver.Blazor.Portal.Chat;

/// <summary>
/// Which content <c>ThreadChatView</c> is currently showing: the live conversation,
/// or the picker for resuming an earlier thread.
/// </summary>
public enum ChatViewMode
{
    /// <summary>The live chat conversation (default) — message list plus composer.</summary>
    Chat,

    /// <summary>The picker that lists earlier threads to resume instead of continuing the current one.</summary>
    ResumeThreads
}

/// <summary>
/// Layout of the in-thread navigation menu (hierarchy + sub-threads + other open threads + context
/// chip). Cycles <see cref="TopBar"/> → <see cref="LeftRail"/> → <see cref="Hidden"/>.
/// </summary>
public enum ThreadNavLayout
{
    /// <summary>A horizontal bar across the top of the thread.</summary>
    TopBar,
    /// <summary>A vertical rail down the left of the conversation.</summary>
    LeftRail,
    /// <summary>Collapsed — only a small reveal toggle shows.</summary>
    Hidden
}

/// <summary>
/// The full chat view for a single thread, bound to a <c>ThreadChatControl</c>. Renders the message
/// list, the composer (agent/model/harness selection, attachments, context chips), sub-thread
/// delegation headers, and per-round elapsed-time and token chips. Reactive throughout: it data-binds
/// the thread view-model from the mesh-node stream and keeps live subscriptions for navigation context,
/// the per-user composer, agent/model snapshots, and individual message cells.
/// </summary>
public partial class ThreadChatView : BlazorView<ThreadChatControl, ThreadChatView>
{
    [Inject] private INavigationService NavigationService { get; set; } = null!;
    [Inject] private SidePanelStateService SidePanelState { get; set; } = null!;
    [Inject] private IDialogService DialogService { get; set; } = null!;
    [Inject] private IMeshService MeshQuery { get; set; } = null!;
    [Inject] private IChatCompletionOrchestrator CompletionOrchestrator { get; set; } = null!;
    [Inject] private IConfiguration Configuration { get; set; } = null!;

    /// <summary>
    /// True when this chat renders inside the portal side panel (cascaded by
    /// <c>PortalLayoutBase</c>, flowing through <c>LayoutAreaView</c>-hosted threads and the
    /// direct new-chat composer alike). Drives the context chip's click target: a side-panel
    /// chat opens its context in the MAIN view; a main-view chat peeks it in the side panel.
    /// </summary>
    [CascadingParameter(Name = "IsInSidePanel")]
    public bool IsInSidePanel { get; set; }


    /// <summary>Stateless — single instance reused per submission.</summary>
    private static readonly ChatPreParser ChatParser = new();

    /// <summary>
    /// Most recent command-result message for the breadcrumb / status row.
    /// Cleared on the next submission.
    /// </summary>
    private string? lastCommandStatus;
    private bool lastCommandStatusIsError;

    /// <summary>
    /// The node-pick request the most recent command asked us to render (null hides the picker),
    /// plus the mesh nodes it resolved. Driven by the command's <c>NodePickerRequest</c> via OpenPicker.
    /// </summary>
    private NodePickerRequest? pendingPicker;
    private IReadOnlyList<MeshNode> pickerNodes = [];

    // Keyboard navigation of the command picker (the /agent etc. node list). The list is a focusable
    // widget; ↑/↓ move _pickerHighlight, Enter commits, Escape dismisses. _focusPickerOnRender moves
    // focus from the Monaco editor (where the command was typed) onto the widget when it opens, so the
    // arrow keys reach the widget instead of being swallowed by Monaco.
    private int _pickerHighlight;
    private ElementReference _pickerWidget;
    private bool _focusPickerOnRender;

    private bool _isDisposed;
    private IDisposable? _navContextSubscription;
    private NavigationContext? _currentNavContext;
    private IDisposable? agentSubscription;
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
    // The best-ever NON-EMPTY thread viewmodel seen for THIS thread (set only when Messages is non-empty,
    // so it can never be poisoned by a transient empty). ConvertThreadViewModel falls back to it whenever the
    // data-bind transiently yields null/empty (the cross-hub node stream momentarily reduces to an empty
    // node), so an open chat never flaps to the empty state mid-conversation (the round-N "vanish"). Reset
    // when the bound thread PATH actually changes (a different thread legitimately has its own messages).
    private ThreadViewModel? _lastNonEmptyThreadVm;
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

            // Sync threadPath and initialContext from the view model.
            if (value != null)
            {
                // 🎯 EMPTY means "no change", not "clear it". The side-panel control rebuilds with
                // ThreadPath = ContentPath ?? "" — i.e. an EMPTY STRING — on every re-render BEFORE the
                // just-created thread's SetContentPath lands. `?? ` only coalesces null, so an empty string
                // would CLOBBER a threadPath we just set in StartThread.onCreated → the next send sees an
                // empty threadPath and re-StartThreads a SECOND thread (the SidePanelChatTenMessagesTest
                // "2nd user bubble never appears" bug). Guard on IsNullOrEmpty, exactly like lines below.
                threadPath = string.IsNullOrEmpty(value.ThreadPath) ? threadPath : value.ThreadPath;
                initialContext = string.IsNullOrEmpty(value.InitialContext) ? initialContext : value.InitialContext;

                // Inside a thread the composer lives ON the thread node (Thread.Composer) —
                // bind the embedded selectors area + the selection projection to the THREAD
                // path. The thread node is the node we're rendering, so it is guaranteed
                // present (no maybe-absent read, no lazy-create/stamp machinery).
                if (!string.IsNullOrEmpty(value.ThreadPath) && _templatePath != value.ThreadPath)
                {
                    _templatePath = value.ThreadPath;
                    OpenComposerProjection(value.ThreadPath);
                }

                // Keep the nav menu's sub-thread (children) list bound to the current thread.
                if (!string.IsNullOrEmpty(value.ThreadPath))
                    SetupChildThreadsSubscription(value.ThreadPath);
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
                // Re-render. Auto-scroll is handled entirely by the JS MutationObserver attached in
                // OnAfterRenderAsync (#128) — it fires post-paint on every DOM mutation, including
                // streamed text, so no Task.Yield timing guess or explicit scroll call is needed here.
                InvokeAsync(StateHasChanged);
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
    // #128 auto-scroll: the JS module + the per-container handle it returns (disposed on teardown).
    private IJSObjectReference? _scrollModule;
    private IJSObjectReference? _scrollHandle;
    private string? MessageText;
    private readonly bool isCreatingThread;
    private bool isCancelling;
    private readonly CancellationTokenSource? _submissionCts;
    private readonly ChatSubmissionHandler submissionHandler = new();

    // ─── Hero-header inline title/description editing ───
    // Click-to-edit: the title + description render as plain text; clicking swaps in a Fluent input
    // bound to a local string, committed (written to the thread node's Name / Description via
    // stream.Update under the durable circuit user) on Enter/blur, cancelled on Escape. The display
    // values come from the data-bound ThreadViewModel (Name / Description) — one source of truth.
    private bool isEditingTitle;
    private bool isEditingDescription;
    private string editTitleText = "";
    private string editDescriptionText = "";
    private Microsoft.FluentUI.AspNetCore.Components.FluentTextField? _titleEditField;
    private Microsoft.FluentUI.AspNetCore.Components.FluentTextArea? _descEditField;
    private bool _focusTitleOnRender;
    private bool _focusDescOnRender;

    // Pending (optimistic) cells — rendered directly as bubbles, not via LayoutAreaView.
    // Output cells are cleared after 3s so LayoutAreaView takes over (grain should be active by then).
    // Input cells stay pending (they're static, LayoutAreaView adds nothing).
    // pendingCells removed — GUI creates real nodes, LayoutAreaView renders them directly.
    private bool showSubmissionProgress;

    // The just-submitted user text, shown UNDER the progress panel while the new thread is created +
    // redirected to, so the user sees their message immediately instead of a blank composer.
    private string? lastSubmittedText;

    // Unified attachments (context + @references)
    private readonly List<AttachmentInfo> attachments = new();
    private const string placeholderText = "Type a message... Use @ to reference nodes";

    // View mode state
    private ChatViewMode viewMode = ChatViewMode.Chat;

    // Resume threads list — live-bound to the synced query surface (hub.GetQuery): the
    // write-consistent, RLS-aware, snapshot stream the agent/model pickers also use
    // (AgentPickerProjection). Every emission is the COMPLETE current set, so a thread UPDATE
    // on submit re-emits the list with the thread still present. Replaces the
    // MeshSearchView/IMeshService.Query binding, whose eventually-consistent delta feed
    // re-queried the store per change and emitted a spurious Removed for a just-submitted thread
    // before the write reached that read path — the "thread disappears from the list on submit;
    // F5 restores it" drop.
    private IReadOnlyList<MeshNode> resumeThreads = [];
    private IDisposable? resumeSubscription;
    private bool resumeLoading;

    // ─── In-thread navigation menu ───
    // A navigation surface WITHIN the thread: the context chip, this thread's position in the
    // hierarchy (ancestors → current → sub-threads), and the user's OTHER open threads across ALL
    // partitions. Toggles between a horizontal top bar, a vertical left rail, and hidden; Option-Tab
    // cycles through the other open threads. Both lists are live-bound to the synced Hub.GetQuery
    // surface (same write-consistent, RLS-aware snapshot stream the Resume picker uses).
    private ThreadNavLayout _navLayout = ThreadNavLayout.TopBar;
    private IReadOnlyList<MeshNode> myThreads = [];        // my open threads, all partitions
    private IDisposable? myThreadsSubscription;
    private IReadOnlyList<MeshNode> childThreads = [];     // sub-threads (delegations) of THIS thread
    private IDisposable? childThreadsSubscription;

    // Agent/model lists — fed by AgentPickerProjection; consumed by the /agent and /model
    // slash commands + @-reference agent detection. The visible harness/agent/model SELECTION
    // is 100% data-bound to the composer node (ThreadComposerView.SelectorsArea, embedded in
    // the footer) — there is no imperative selectedAgent/selectedModel/selectedHarness state,
    // no sticky restore, no resolve/rebuild machinery. The current selection is projected
    // one-way from the composer node into the bound* fields below.
    private IReadOnlyList<AgentDisplayInfo> agentDisplayInfos = [];

    /// <summary>
    /// Current selection, projected one-way from the bound composer state (<see cref="_templatePath"/>).
    /// Each holds the picked node's PATH and flows through submit/resubmit UN-resolved — the
    /// execution boundary normalizes paths to ids (SelectionId.IdOf). The data-bound pickers write
    /// the composer themselves; the /agent /model commands and @-agent references write it via
    /// <c>WriteComposerSelection</c>.
    /// </summary>
    private string? boundHarness;
    private string? boundAgentPath;
    private string? boundModelPath;
    private string? boundEffort;
    // Actual runtime each subscription-CLI harness reports (model from its init/hello probe, effort from
    // its settings.json), keyed by harness id — shown in the status bar instead of the mesh model
    // selection. Populated by the probes in OnInitialized.
    private readonly Dictionary<string, MeshWeaver.AI.HarnessRuntime> harnessRuntimes = new();
    private readonly List<IDisposable> harnessRuntimeSubs = [];
    private IDisposable? composerSubscription;
    private IDisposable? composerDefaultsSubscription;
    private IDisposable? modelsSubscription;

    // Live model catalog (the EXACT AgentPickerProjection.ObserveModels pipeline the picker binds to),
    // kept READ-ONLY: used only to (a) disable Send + surface a GUI message when the catalog is empty and
    // (b) resolve an invalid bound selection to the top model AT SUBMIT TIME. It NEVER writes a node from
    // a subscription callback — an earlier version did (WriteComposerSelection from the model snapshot),
    // which patched a not-yet-present composer node and NotFound-stormed the hub into a wedge. _modelsLoaded
    // distinguishes "not loaded yet" (don't gate Send) from "loaded and genuinely empty" (gate Send).
    private IReadOnlyList<MeshWeaver.AI.ModelInfo> availableModels = System.Array.Empty<MeshWeaver.AI.ModelInfo>();
    private bool _modelsLoaded;

    /// <summary>True once the catalog has loaded and is empty — Send is disabled and the user is told to
    /// configure a model. False while still loading, so Send is never falsely gated.</summary>
    private bool HasNoModels => _modelsLoaded && availableModels.Count == 0;

    // ─── Composer binding target ───
    // Out of a thread: the per-user singleton composer NODE {userHome}/_Thread/ThreadComposer.
    // Inside a thread: the THREAD path — the composer is embedded on the thread content
    // (Thread.Composer) and the thread hub serves the same data-bound Selectors area.
    private string? _userHome;
    private string? _templatePath;

    /// <summary>The Id (last path segment) the execution pipeline matches on, from a picked node path.</summary>
    private static string? LastSegment(string? path) =>
        string.IsNullOrEmpty(path) ? path : path.Split('/')[^1];

    // (The thin status row's cumulative token figure is rendered by the <ThreadTokenChip> component,
    //  which reads the per-model TokenUsage satellites ({threadPath}/_Usage/*) reactively. The old
    //  inline FormatTokens(int) helper read token state off the Thread node — that state moved onto the
    //  satellites in 616b4e27f, leaving the helper orphaned, so it was removed.)

    // (Removed DefaultModelId / ModelTier:Standard — defaults are no longer hardcoded. The default
    //  model is the Order=-1 model resolved by AgentPickerProjection.ObserveDefaultComposer and
    //  written onto the composer; submit sends the bound selection, never an invented fallback.)

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

    /// <summary>
    /// Component initialization: seeds <c>threadPath</c>/<c>initialContext</c> from the view-model,
    /// hooks side-panel menu actions, starts the 1-second elapsed-time ticker (re-renders only while
    /// something is executing), subscribes to the navigation-context stream, resolves the composer
    /// binding (the embedded thread composer for an existing thread, otherwise the per-user
    /// <c>ThreadComposer</c> singleton), seeds the initial context-attachment chip, and primes the
    /// agent/model selections. All work is reactive — no <c>await</c>.
    /// </summary>
    protected override void OnInitialized()
    {
        Logger.LogDebug("[ThreadChat:{InstanceId}] OnInitialized started", _instanceId);

        // Initialize from direct ViewModel properties (side panel / dashboard case)
        threadPath ??= ViewModel.ThreadPath;
        initialContext ??= ViewModel.InitialContext;

        // Subscribe to side panel menu actions
        SidePanelState.OnActionRequested += OnSidePanelAction;

        // 1-second ticker for elapsed-time chips on the exec bar, sub-thread cards,
        // and per-bubble streaming chips. Only fires StateHasChanged while the thread
        // (or a sub-thread) is actually executing — silent on idle threads so we don't
        // burn render cycles when there's nothing to update. We deliberately do NOT key
        // off a cell's Status == "Streaming": a cell whose status lags/sticks at
        // "Streaming" after the round ends would keep the ticker (and the clock) alive
        // forever. IsExecuting is the authoritative "still running" signal, and the
        // per-bubble live clock is gated on it too (see ThreadChatView.razor).
        elapsedTicker = System.Reactive.Linq.Observable
            .Interval(TimeSpan.FromSeconds(1))
            .Subscribe(_ =>
            {
                if (_isDisposed) return;
                var anyExecuting =
                    ThreadViewModel?.IsExecuting == true
                    || delegationHeaders.Values.Any(h => h.IsExecuting);
                if (anyExecuting)
                    InvokeAsync(StateHasChanged);
            });

        // Set initial title
        UpdateSidePanelTitle();

        // Resolve the composer binding: the per-user singleton {userHome}/_Thread/ThreadComposer
        // in compose mode (EnsureComposer creates it with defaults if absent, then projects);
        // inside a thread the composer is EMBEDDED on the thread node (Thread.Composer) — bind
        // the thread path directly (the node we're rendering is guaranteed present).
        var accessSvc = Hub.ServiceProvider.GetService<AccessService>();
        _userHome = ResolveUserHome(accessSvc);
        if (!string.IsNullOrEmpty(threadPath))
        {
            _templatePath = threadPath;
            OpenComposerProjection(threadPath);
        }
        else if (!string.IsNullOrEmpty(_userHome))
        {
            _templatePath = MeshWeaver.AI.ThreadComposerNodeType.PathFor(_userHome);
            EnsureComposer();
        }

        // 🎯 Composer context — FULLY REACTIVE (the chip subscribes to the stream; nothing imperative,
        // no snapshot read, no one-shot lookup). NavigationService.NavigationContext is a ReplaySubject(1)
        // (NavigationServiceTest.NavigationContext_ReplaysLastValueToLateSubscriber), so this late
        // subscriber immediately gets the last navigation context. Pipeline:
        //   nav context → the composer context DECISION (the viewed page's MAIN node; an existing thread
        //   keeps its subject) → the main node's LIVE display name (its OWN node stream, so a rename
        //   re-labels the chip) → the context chip + initialContext.
        // ResolveComposerContext / DisplayNameOf are pure + unit-tested. _currentNavContext is kept as a
        // snapshot ONLY for the picker projection and the submit-time read.
        _navContextSubscription = NavigationService.NavigationContext
            .Do(ctx => _currentNavContext = ctx)
            .Select(ctx => MeshWeaver.AI.NavigationContextProjection.ResolveComposerContext(
                threadPath, ViewModel.InitialContext, ctx))
            .DistinctUntilChanged()
            .Select(path => string.IsNullOrEmpty(path)
                ? System.Reactive.Linq.Observable.Return((Path: (string?)null, Name: (string?)null))
                : Hub.GetMeshNodeStream(path)
                    .Select(node => (Path: (string?)path,
                        Name: (string?)MeshWeaver.AI.NavigationContextProjection.DisplayNameOf(node, path))))
            .Switch()
            .DistinctUntilChanged()
            .Subscribe(chip =>
            {
                // initialContext set SYNCHRONOUSLY so a submit immediately after navigation reads the
                // current context; the chip render is marshalled onto the renderer.
                initialContext = chip.Path;
                InvokeAsync(() =>
                {
                    if (_isDisposed) return;
                    attachments.RemoveAll(a => a.IsContext);
                    if (!string.IsNullOrEmpty(chip.Path))
                        attachments.Insert(0, new AttachmentInfo(chip.Path, chip.Name, IsContext: true));
                    StateHasChanged();
                });
            });

        try
        {
            InitializeAgentAndModelSelections();
            Logger.LogDebug("[ThreadChat:{InstanceId}] Agent and model selections initialized", _instanceId);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "[ThreadChat:{InstanceId}] Failed to initialize agent/model selections", _instanceId);
        }

        // In-thread navigation menu: my other open threads (all partitions) is independent of the
        // current thread, so subscribe once here; this thread's sub-threads bind once the path is known.
        SetupMyThreadsSubscription();
        if (!string.IsNullOrEmpty(threadPath))
            SetupChildThreadsSubscription(threadPath);

        // New-chat composer mounting (e.g. opened by "new thread from cell" while the panel was closed):
        // pick up a one-shot pending draft. Harmless in every other case (no-op when there's no draft).
        SeedPendingDraftIfAny();

        // Probe each subscription-CLI harness (Claude Code / Copilot) for the ACTUAL model it runs — read
        // from the CLI's init/hello message, cached. The status bar shows this, NOT the user's mesh model
        // selection (the Model partition is MeshWeaver-only). Cheap: cached + replayed across all views.
        foreach (var probe in Hub.ServiceProvider.GetServices<MeshWeaver.AI.IHarnessRuntimeInfo>())
        {
            var harnessId = probe.HarnessId;
            // Ask the harness for the effort THIS user actually has set — read from the user's OWN
            // per-user CLI config dir ({ClaudeCode:ConfigDirRoot}/{userId}/.claude, the exact dir the
            // harness runs the CLI under), NOT the server's ~/.claude. Null when no per-user root is
            // configured (local dev) → the probe falls back to the CLI's default config dir. The old
            // `userConfigDir: null` always read the server dir → a wrong/absent level shown as "medium".
            harnessRuntimeSubs.Add(probe.Get(ResolveProbeConfigDir(harnessId)).Subscribe(
                rt => InvokeAsync(() =>
                {
                    if (_isDisposed) return;
                    harnessRuntimes[harnessId] = rt;
                    StateHasChanged();
                }),
                ex => Logger.LogDebug(ex, "Harness runtime probe failed for {Harness}", harnessId)));
        }

        Logger.LogDebug("[ThreadChat:{InstanceId}] OnInitialized completed", _instanceId);
    }

    /// <summary>
    /// The per-user CLI config dir a runtime probe reads the user's <c>effortLevel</c> from — the SAME
    /// dir the harness runs the CLI under (<c>{ClaudeCode:ConfigDirRoot}/{userId}/.claude</c>), so the
    /// status bar reflects THIS user's setting, not the server's. Null when the harness has no per-user
    /// config root (local dev) or the user isn't resolved yet → the probe uses the CLI's default dir.
    /// </summary>
    private string? ResolveProbeConfigDir(string harnessId)
    {
        if (!string.Equals(harnessId, MeshWeaver.AI.Harnesses.ClaudeCode, StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrEmpty(_userHome))
            return null;
        var root = Configuration["ClaudeCode:ConfigDirRoot"]?.TrimEnd('/', '\\');
        return string.IsNullOrEmpty(root) ? null : $"{root}/{_userHome}/.claude";
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

    // ResolveCircuitUser() / RunUnderCircuitUser<T>() / UpdateMeshNodeAsCircuitUser(...) are inherited
    // from BlazorView — the ONE place every circuit-scoped view re-establishes the durable circuit user
    // for mesh reads/writes deferred behind an Rx hop (where the ambient AccessContext is nulled).

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
        composerDefaultsSubscription?.Dispose();
        boundHarness = boundAgentPath = boundModelPath = boundEffort = null;
        if (string.IsNullOrEmpty(_templatePath))
            return;
        var path = _templatePath;

        // Resolve the default composer selection BY ORDER — the Order=-1 (lowest-order) agent / model /
        // harness from the live registries, never a hardcoded name/id (AgentPickerProjection.ObserveDefaultComposer).
        // Take(1) + a short timeout so a brand-new composer seeds promptly; on timeout/empty we seed with
        // empty selections (no invented fallback) — the picker still defaults to the Order=-1 item.
        var picker = AgentPickerProjection.DerivePickerContext(_currentNavContext, initialContext);
        composerDefaultsSubscription = AgentPickerProjection
            .ObserveDefaultComposer(Hub, _userHome, AgentPickerProjection.PartitionOf(initialContext),
                picker.ContextPath, picker.NodeTypePath)
            .Take(1)
            .Timeout(TimeSpan.FromSeconds(5))
            .Catch<MeshWeaver.AI.ThreadComposer, Exception>(_ => System.Reactive.Linq.Observable.Return(new MeshWeaver.AI.ThreadComposer()))
            .Subscribe(defaults => InvokeAsync(() => CreateComposerWithDefaults(path, defaults)));
    }

    /// <summary>
    /// Creates the composer node with the order-resolved <paramref name="defaults"/> (heals users who
    /// predate the onboarding seed), then fills any EMPTY selection + opens the live projection.
    /// CreateNode registers a routable node (unlike GetMeshNodeStream(path).Update, which only patches
    /// an EXISTING node and otherwise NotFound-storms the partition hub — that wedged the portal);
    /// NodeAlreadyExists is benign (node present), so both paths proceed to fill + project.
    /// </summary>
    private void CreateComposerWithDefaults(string path, MeshWeaver.AI.ThreadComposer defaults)
    {
        if (_isDisposed || _templatePath != path)
            return;
        MeshQuery.CreateNode(MeshWeaver.Mesh.MeshNode.FromPath(path) with
            {
                NodeType = MeshWeaver.AI.ThreadComposerNodeType.NodeType,
                Name = "Chat Input",
                Content = defaults
            })
            .Subscribe(
                _ => InvokeAsync(() => FillDefaultsAndProject(path, defaults)),
                ex => InvokeAsync(() =>
                {
                    Logger.LogDebug(ex,
                        "[ThreadChat:{InstanceId}] ensure composer node (benign if already exists) {Path}", _instanceId, path);
                    FillDefaultsAndProject(path, defaults);
                }));
    }

    /// <summary>
    /// Fills EMPTY selection fields on the (now-present) composer node with <paramref name="defaults"/>
    /// — coalesce only, NEVER clobbering a value the user already set — then opens the live projection.
    /// One idempotent Update (skips the write when nothing's empty), on an existing node (no storm).
    /// </summary>
    private void FillDefaultsAndProject(string path, MeshWeaver.AI.ThreadComposer defaults)
    {
        if (_isDisposed || _templatePath != path)
            return;
        Hub.GetMeshNodeStream(path).Update(node =>
        {
            var c = node.ContentAs<MeshWeaver.AI.ThreadComposer>(Hub.JsonSerializerOptions, Logger);
            if (c is null) return node; // unreadable → leave alone, never clobber
            var filled = c with
            {
                Harness = string.IsNullOrEmpty(c.Harness) ? defaults.Harness : c.Harness,
                // Default (or RE-default) the agent: empty OR a background-generator agent → the
                // conversational default. The latter migrates composers a pre-sort:order picker
                // stamped with the first (utility) agent — the "ThreadNamer pre-selected" symptom.
                AgentName = NeedsAgentDefault(c.AgentName) ? defaults.AgentName : c.AgentName,
                ModelName = string.IsNullOrEmpty(c.ModelName) ? defaults.ModelName : c.ModelName,
            };
            return filled == c ? node : node with { Content = filled };
        }).Subscribe(
            _ => { },
            ex => Logger.LogDebug(ex,
                "[ThreadChat:{InstanceId}] composer default-fill failed for {Path}", _instanceId, path));

        OpenComposerProjection(path);
    }

    /// <summary>
    /// True when the composer's <c>AgentName</c> should be replaced with the conversational default:
    /// it's empty, or it's a background-GENERATOR agent (<c>modelTier:utility</c> — ThreadNamer,
    /// NodeInitializer, DescriptionWriter) that must never be the chat's selected agent (they emit
    /// structured "Name:/Id:/Svg:" output, not conversation). A pre-<c>sort:order</c> picker could
    /// default-to-first onto one of these and persist it; this clears that.
    /// </summary>
    private static bool NeedsAgentDefault(string? agentName)
    {
        if (string.IsNullOrEmpty(agentName)) return true;
        var seg = agentName.Contains('/') ? agentName[(agentName.LastIndexOf('/') + 1)..] : agentName;
        return seg is "ThreadNamer" or "NodeInitializer" or "DescriptionWriter";
    }

    /// <summary>
    /// Opens the live one-way projection of the composer selection into bound* — called ONLY after the
    /// node is confirmed present (created or already-exists; a thread node is the node being rendered),
    /// so the read never hits a missing node. <see cref="MeshWeaver.AI.ThreadComposerNodeType.ComposerOf"/>
    /// discriminates by NodeType: a ThreadComposer node's own content, or the thread's embedded
    /// <c>Thread.Composer</c>.
    /// </summary>
    private void OpenComposerProjection(string path)
    {
        if (_isDisposed || _templatePath != path)
            return;
        composerSubscription?.Dispose();
        composerSubscription = Hub.GetMeshNodeStream(path)
            .Select(n => MeshWeaver.AI.ThreadComposerNodeType.ComposerOf(n, Hub.JsonSerializerOptions, Logger))
            .Where(c => c is not null)
            .Subscribe(
                c => InvokeAsync(() =>
                {
                    if (_isDisposed || _templatePath != path) return;
                    boundHarness = c!.Harness;
                    boundAgentPath = c.AgentName;
                    boundModelPath = c.ModelName;
                    boundEffort = c.Effort;
                    StateHasChanged();
                }),
                ex => SurfaceError(ex, "Loading the composer"));
        StateHasChanged();
    }

    /// <summary>
    /// Writes a harness/agent/model selection onto the bound composer state — the imperative entry
    /// point for the /agent and /model slash-commands and @-agent references (the visual pickers
    /// write the node themselves). Values are node PATHS (or bare ids — execution normalizes); a
    /// null arg leaves that field untouched. Targets the composer node out of a thread and the
    /// thread's embedded <c>Thread.Composer</c> inside one. Bad-data tolerant: an unreadable node
    /// is left alone, never clobbered.
    /// </summary>
    private void WriteComposerSelection(string? harness = null, string? agentPath = null, string? modelName = null)
    {
        if (string.IsNullOrEmpty(_templatePath))
            return;
        // Apply to the ACTIVE composer (the thread's embedded composer inside a thread, the per-user
        // composer node out of one).
        ApplyComposerSelection(_templatePath!, harness, agentPath, modelName, ensure: false, "Saving your selection");

        // ALSO persist the selection as the user's DEFAULT composer, so a NEW chat (or a re-init after the
        // catalog changes) restores the last-used harness/model/agent instead of snapping back to the
        // catalog default — this is the "harness jumped back to Claude Code" report. Out of a thread the
        // default IS _templatePath (the second write is idempotent); inside a thread it is a different node
        // that may not be materialised yet, so ensure it first (never Update an absent node → NotFound storm).
        var defaultPath = string.IsNullOrEmpty(_userHome)
            ? null : MeshWeaver.AI.ThreadComposerNodeType.PathFor(_userHome);
        if (!string.IsNullOrEmpty(defaultPath) && !string.Equals(defaultPath, _templatePath, StringComparison.Ordinal))
            ApplyComposerSelection(defaultPath!, harness, agentPath, modelName, ensure: true, "Saving your default");
    }

    /// <summary>
    /// Merges the (harness/agent/model) selection onto a composer node's <c>ThreadComposer</c> content —
    /// coalescing only the provided fields (a null arg leaves that field untouched), bad-data tolerant (an
    /// unreadable node is left alone, never clobbered). When <paramref name="ensure"/> is set the node is
    /// CreateNode'd first (benign <c>NodeAlreadyExists</c>) so a not-yet-materialised per-user default
    /// composer does not NotFound-storm the partition hub on Update.
    /// </summary>
    private void ApplyComposerSelection(string path, string? harness, string? agentPath, string? modelName, bool ensure, string errorContext)
    {
        // Reached from the slash-command picker AFTER Rx hops (skill query → picker query → InvokeAsync),
        // where the circuit AccessService's Context AND CircuitContext have been nulled by the Blazor
        // inbound-activity finally. UpdateMeshNodeAsCircuitUser re-establishes the DURABLE circuit user on
        // the hub AccessService for Update's synchronous capture, so the composer write is attributed to
        // the real user instead of null (→ "Saving your selection: Access denied").
        void Write() => UpdateMeshNodeAsCircuitUser(path, node =>
        {
            var existing = MeshWeaver.AI.ThreadComposerNodeType.ComposerOf(node, Hub.JsonSerializerOptions, Logger);
            if (node?.Content is not null && existing is null)
                return node!;
            var updated = (existing ?? new MeshWeaver.AI.ThreadComposer()) with
            {
                Harness = harness ?? existing?.Harness,
                AgentName = agentPath ?? existing?.AgentName,
                ModelName = modelName ?? existing?.ModelName
            };
            return MeshWeaver.AI.ThreadComposerNodeType.WithComposer(
                node!, updated, Hub.JsonSerializerOptions, Logger);
        }, ex => SurfaceError(ex, errorContext));

        if (!ensure)
        {
            Write();
            return;
        }
        // Create the node (benign if it already exists), THEN merge the selection — CreateNode registers a
        // routable node, unlike Update which only patches an existing one.
        MeshQuery.CreateNode(MeshWeaver.Mesh.MeshNode.FromPath(path) with
        {
            NodeType = MeshWeaver.AI.ThreadComposerNodeType.NodeType,
            Name = "Chat Input",
            Content = new MeshWeaver.AI.ThreadComposer()
        }).Subscribe(_ => InvokeAsync(Write), _ => InvokeAsync(Write));
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

    /// <summary>
    /// Wires the reactive data binding: projects the control's thread view-model from its mesh-node
    /// stream into <c>ThreadViewModel</c> (via <c>ConvertThreadViewModel</c>) so the view re-renders
    /// live as the thread changes.
    /// </summary>
    protected override void BindData()
    {
        base.BindData();
        DataBind(ViewModel.ThreadViewModel, x => x.ThreadViewModel, ConvertThreadViewModel);
    }


    private void InitializeAgentAndModelSelections()
    {
        // /agent, /model and /harness are generic node-pick commands that query the mesh on demand
        // (OpenPicker). Two READ-ONLY snapshot subscriptions are kept: the agent snapshot (for @-reference
        // agent detection) and the model snapshot (to gate Send when empty + resolve the bound model at
        // submit). Neither writes anything.
        SubscribeToAgentNodes();
        SubscribeToModelNodes();
    }

    /// <summary>
    /// READ-ONLY subscription to the live model catalog — the SAME <see cref="AgentPickerProjection.ObserveModels"/>
    /// pipeline the /model picker binds to. Drives Send-gating (empty catalog → disable + message) and
    /// submit-time model resolution. NEVER writes a node (a prior write-from-callback NotFound-stormed the
    /// hub). A load error is logged, not surfaced, and leaves <c>_modelsLoaded</c> false so Send isn't
    /// falsely gated.
    /// </summary>
    private void SubscribeToModelNodes()
    {
        modelsSubscription?.Dispose();
        var workspace = Hub.ServiceProvider.GetService<IWorkspace>();
        if (workspace == null)
            return;
        var picker = AgentPickerProjection.DerivePickerContext(_currentNavContext, initialContext);
        modelsSubscription = AgentPickerProjection
            .ObserveModels(workspace, Hub, picker.ContextPath, picker.NodeTypePath, userPath: _userHome)
            .Subscribe(
                models => InvokeAsync(() =>
                {
                    if (_isDisposed) return;
                    availableModels = models;
                    _modelsLoaded = true;
                    StateHasChanged(); // refresh Send-gating + the "configure a model" hint
                }),
                ex => Logger.LogDebug(ex,
                    "[ThreadChat:{InstanceId}] model list subscription error (Send not gated)", _instanceId));
    }

    /// <summary>
    /// Resolves the model to actually submit with — guaranteeing it is never INVALID without ever writing a
    /// node in the background. If the bound selection is present in the live catalog (or the catalog hasn't
    /// loaded), it's used as-is; otherwise it falls back to the TOP (lowest-<c>Order</c>) model — the same
    /// Order=-1 default the picker uses. The empty-catalog case never reaches here (Send is disabled).
    /// </summary>
    private string? ResolveSubmitModel(string? bound)
    {
        if (!_modelsLoaded || availableModels.Count == 0)
            return bound; // unknown / none — leave as-is (the none case is blocked by Send-gating)
        var isValid = !string.IsNullOrEmpty(bound) && availableModels.Any(m =>
            string.Equals(m.Path, bound, StringComparison.OrdinalIgnoreCase)
            || string.Equals(LastSegment(m.Path), LastSegment(bound), StringComparison.OrdinalIgnoreCase));
        if (isValid)
            return bound;
        return availableModels
            .Where(m => !string.IsNullOrEmpty(m.Path))
            .OrderBy(m => m.Order)
            .Select(m => m.Path)
            .FirstOrDefault() ?? bound;
    }

    // Agent nodes from the reactive query, keyed by node path — used ONLY for @-reference
    // agent detection (the /agent, /model, /harness commands query the mesh on demand).
    private readonly Dictionary<string, AgentDisplayInfo> _agentsByPath = new();

    private void SubscribeToAgentNodes()
    {
        agentSubscription?.Dispose();
        _agentsByPath.Clear();

        var workspace = Hub.ServiceProvider.GetService<IWorkspace>();
        if (workspace == null)
        {
            Logger.LogWarning("[ThreadChat:{InstanceId}] No IWorkspace — synced agent/model query skipped",
                _instanceId);
            return;
        }

        // Agent snapshot subscription — kept ONLY for @-reference agent detection
        // (OnCompletionItemAccepted decides whether an @-ref is an agent). The /agent,
        // /model and /harness commands no longer load lists here: they go through the
        // GENERIC node picker (OpenPicker), which queries the mesh on demand.
        agentSubscription = AgentPickerProjection
            .ObserveAgents(Hub, _userHome, AgentPickerProjection.PartitionOf(initialContext))
            .Subscribe(agents => InvokeAsync(() => OnAgentList(agents)));
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
        // Drop background-generator agents (modelTier:utility — ThreadNamer, NodeInitializer,
        // DescriptionWriter) from every CONVERSATIONAL surface: the /agent picker, @-reference
        // selection, and the inline agent widget. They're invoked programmatically and emit
        // structured "Name:/Id:/Svg:" output, so e.g. ThreadNamer must never answer a user's
        // "hi". The projection itself keeps them (the generators resolve them); we filter here,
        // at the chat UI, so generators are unaffected.
        var conversational = agents.Where(a => !AgentPickerProjection.IsUtilityAgent(a)).ToList();

        Logger.LogDebug("[ThreadChat:{InstanceId}] Agents received: count={Count} (conversational={Conv})",
            _instanceId, agents.Count, conversational.Count);

        _agentsByPath.Clear();
        foreach (var a in conversational)
            if (!string.IsNullOrEmpty(a.Path))
                _agentsByPath[a.Path] = a;

        agentDisplayInfos = conversational;
        StateHasChanged();
    }

    private void SendMessage()
    {
        if (_isDisposed)
            return;

        // Never block on the running round: an Enter mid-round is accepted immediately and queued
        // via PendingUserMessages (drained when the round finishes) — the composer clears on submit
        // and stays usable, so the user is never locked out while a response streams. Esc still
        // cancels the in-flight round; TryBeginSubmit (in SubmitMessageCore) dedups an accidental
        // double-submit of the same text within its debounce window.

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

            // A NEW thread is allocating (composer dimmed, text retained — see the deferred
            // clear below): ignore submits entirely. The dimmed overlay blocks pointer events
            // but NOT keyboard — Monaco keeps focus, and the submission handler was
            // force-released at the end of the previous submit, so a repeated Enter would
            // otherwise StartThread AGAIN with the same text (duplicate thread). The flag
            // clears in the onCreated readable callback and in onError.
            if (showSubmissionProgress)
                return;

            // Use MessageText (updated via Monaco ValueChanged binding) — no blocking Monaco read.
            var userMessageText = MessageText;

            // Slash-skill interception: parse leading "/word args" via ChatPreParser. If it resolves to
            // a nodeType:Skill, run its action and short-circuit (don't post to the agent).
            if (!string.IsNullOrWhiteSpace(userMessageText))
            {
                var parsed = ChatParser.Parse(userMessageText);
                if (parsed.Command != null)
                {
                    // Under a CLI harness (Claude Code / Copilot) the harness's OWN commands (/login,
                    // /logout — it authenticates ITSELF) and the node-pick built-ins (/agent, /model —
                    // the harness has its own model/agent selection) are FORWARDED 1:1: the raw
                    // "/command" text flows to the harness as the message. Everything ELSE is handled by
                    // MeshWeaver via HandleSlashCommandAsync → skill resolution:
                    //   • /harness — the runtime switch (so the user is never stuck in a CLI harness);
                    //   • a custom MeshWeaver skill (a nodeType:Skill — /code, /gui, …) is injected
                    //     (its instructions are loaded into the round);
                    //   • anything that resolves to no skill surfaces "Unknown command" locally.
                    // Under the MeshWeaver harness, every slash-skill is handled this way.
                    var harness = ActiveHarness();
                    var commandName = parsed.Command.Name;
                    var isRuntimeSwitch = string.Equals(commandName, "harness", StringComparison.OrdinalIgnoreCase);
                    var harnessCmd = harness?.Commands.FirstOrDefault(
                        c => string.Equals(c.Name, commandName, StringComparison.OrdinalIgnoreCase));
                    // 🔑 A harness AUTH command (/login, /logout — Connect/Disconnect) CANNOT be performed
                    // by the headless `claude --print` CLI (no interactive OAuth), so it must NEVER be
                    // forwarded — forwarding it was exactly why login did nothing. The PORTAL owns the
                    // credential: HandleHarnessAuthCommand stores a pasted key / token (choose the method)
                    // as the harness's ModelProvider node, which the ClaudeCode client reads back.
                    var isHarnessAuthCommand = harnessCmd is
                        { Kind: MeshWeaver.AI.HarnessCommandKind.Connect or MeshWeaver.AI.HarnessCommandKind.Disconnect };
                    if (harness is not null && !isRuntimeSwitch && isHarnessAuthCommand)
                    {
                        HandleHarnessAuthCommand(harness, harnessCmd!, userMessageText);
                        MessageText = null;
                        if (monacoEditor != null)
                            _ = ClearMonacoAsync();
                        StateHasChanged();
                        return;
                    }
                    var isHarnessOwnedCommand = harnessCmd is not null;
                    var isHarnessNodePick = string.Equals(commandName, "agent", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(commandName, "model", StringComparison.OrdinalIgnoreCase);
                    // Forward only under a CLI harness, and never /harness (always MeshWeaver-owned).
                    var forwardToHarness = harness is not null && !isRuntimeSwitch
                        && (isHarnessOwnedCommand || isHarnessNodePick);
                    if (!forwardToHarness)
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
                    // else: CLI harness + a harness-owned (/login, /logout) or node-pick (/agent, /model)
                    // command → fall through, so the raw "/command" text is forwarded 1:1 to the harness.
                }
            }

            // 🚫 The composer NEVER blocks. We do not gate submission on model/harness availability here —
            // that is the "chat is chronically disabled / Enter does nothing" report. A CLI harness (Claude
            // Code / Copilot) runs its OWN subscription model; and even under the MeshWeaver harness with an
            // empty catalog the message is accepted and the "no AI model available" condition is surfaced in
            // the thread OUTPUT (the wedges-to-zero graceful sink), not by dead-ending the input box. Slash
            // commands still short-circuit above, so /model, /harness, /login work regardless.

            // Attempt to begin submission — rejects empty text and concurrent submissions
            if (!submissionHandler.TryBeginSubmit(userMessageText))
                return;

            // EXISTING thread: clear the editor immediately — the message echoes into the
            // message list via PendingUserMessages right away, so the text is never "gone".
            // NEW thread (no threadPath yet): keep the text in the editor until the thread
            // is readable (cleared in onCreated below). Clearing here left the landing-page
            // composer with NO visible trace of the message for the whole create+redirect
            // window — and permanently when the view was torn down mid-allocation (issue
            // #175: "message absorbed but disappears immediately"). The input is dimmed +
            // aria-hidden while showSubmissionProgress, so the retained text cannot be
            // edited or re-submitted during allocation; on a create error it is still
            // there for the user to retry instead of silently lost.
            var isNewThread = string.IsNullOrEmpty(threadPath);
            if (!isNewThread)
            {
                MessageText = null;
                StateHasChanged();

                // Fire-and-forget Monaco clear — no await in the submit path.
                if (monacoEditor != null)
                {
                    _ = ClearMonacoAsync();
                }
            }

            var accessService = Hub.ServiceProvider.GetService<AccessService>();
            var createdBy = accessService?.Context?.ObjectId ?? accessService?.CircuitContext?.ObjectId;
            var authorName = accessService?.Context?.Name ?? "You";
            var isCompact = ViewModel.HideEmptyState;
            var capturedAttachments = attachments.Select(a => a.Path).ToList();

            // A thread must live in a REAL partition (a space or the user's home), NEVER a rogue/reserved
            // ROUTE partition (login, welcome, …): that partition has no write policy, so StartThread there
            // is denied → onError → no thread → the side-panel chat tears down (the "login" symptom). Strip
            // a reserved nav-namespace / context and fall back to the user's own namespace.
            // 🎯 A thread must anchor under a real OWNER node, NEVER a satellite. CurrentNamespace
            // is the raw current-page address — when the user starts a chat while viewing a satellite
            // (another thread, an activity, a comment), that namespace IS the satellite, so anchoring
            // there would NEST the new thread under it (e.g. a thread under a thread). Resolve to the
            // MAIN NODE: prefer the nav context's PrimaryPath (Node.MainNode when the node is loaded),
            // fall back to CurrentNamespace, then strip any residual satellite segment.
            // NormalizeContextPath also drops reserved route partitions (login/welcome/…) to "".
            var navMainNode = _currentNavContext?.PrimaryPath ?? NavigationService.CurrentNamespace;
            var navNs = NormalizeContextPath(navMainNode ?? string.Empty);
            var safeContext = MeshWeaver.AI.AgentPickerProjection.IsReservedPartition(initialContext)
                ? null : initialContext;
            // 🎯 Carry the FULL navigation reference to the agent: the layout area + the optional
            // query parameters as KVP (the main-node address rides as safeContext/contextPath).
            // JSON-serialized with the mesh's standard options; the agent loads node content via Get.
            var navReference = MeshWeaver.AI.NavigationContextProjection.ToReference(_currentNavContext);
            var contextReference = navReference is null
                ? null
                : System.Text.Json.JsonSerializer.Serialize(navReference, Hub.JsonSerializerOptions);
            // 🎯 Normalize EACH candidate and take the first that survives, not the first non-empty RAW one.
            // safeContext (= initialContext, e.g. "User/Roland" or bare "User" when the side panel is opened
            // on a user home) and createdBy are raw; NormalizeContextPath maps User/{id} → {id} and bare
            // User / reserved partitions → "". If we picked the first raw non-empty value and normalized
            // ONCE, a safeContext of bare "User" would normalize to "" and we'd anchor on an empty namespace
            // — skipping createdBy (the owner's writable partition). Normalizing each and taking the first
            // non-empty result lands on createdBy="Roland", so the StartThread never targets the un-writable
            // 'User' partition (whose denial surfaces a blocking "Something went wrong" modal that breaks the
            // composer — the SidePanelChatTenMessagesTest "2nd message never lands" cause).
            var ns = new[] { navNs, safeContext, createdBy }
                .Select(c => NormalizeContextPath(c ?? string.Empty))
                .FirstOrDefault(c => !string.IsNullOrEmpty(c)) ?? string.Empty;

            Action<string> onError = err => InvokeAsync(() =>
            {
                if (_isDisposed) return;
                Logger.LogWarning("[ThreadChat:{InstanceId}] Submit failed: {Error}", _instanceId, err);
                // SURFACE the submit failure (modal) instead of just clearing the spinner — a silent
                // reset is the "message vanished, no idea why" symptom.
                Services.GetService<MeshWeaver.Blazor.Infrastructure.PortalErrorSink>()
                    ?.Report($"Couldn't send your message: {err}");
                showSubmissionProgress = false;
                lastSubmittedText = null;
                submissionHandler.ForceRelease();
                StateHasChanged();
            });

            // Resolve the model to submit with — never an invalid/stale selection (a removed model like a
            // deleted gpt-4o falls back to the top catalog model). Read-only: nothing is written here.
            var modelForThread = ResolveSubmitModel(boundModelPath);

            if (isNewThread)
            {
                showSubmissionProgress = true; // step 2: show progress in the composer immediately on submit
                lastSubmittedText = userMessageText; // ...and echo the submitted message under the progress
                Logger.LogInformation("[Chat] Creating thread + submitting message");
                // Selections flow as the picked node PATHS — execution normalizes to ids
                // at its boundary (SelectionId.IdOf). The composer snapshot is copied onto
                // the created thread (Thread.Composer) so the in-thread selectors continue
                // the same selection.
                Hub.StartThread(
                    namespacePath: ns,
                    userText: userMessageText!,
                    agentName: boundAgentPath,
                    modelName: modelForThread,
                    contextPath: safeContext,
                    attachments: capturedAttachments,
                    createdBy: createdBy,
                    authorName: authorName,
                    harness: boundHarness ?? Harnesses.MeshWeaver,
                    composer: new MeshWeaver.AI.ThreadComposer
                    {
                        Harness = boundHarness,
                        AgentName = boundAgentPath,
                        ModelName = modelForThread,
                        ContextPath = safeContext,
                        ContextReference = contextReference
                    },
                    onCreated: node =>
                    {
                        var path = node.Path;
                        if (string.IsNullOrEmpty(path)) { onError("Thread created with no path"); return; }
                        // 🎯 Route subsequent sends to SubmitComposer IMMEDIATELY: the thread node now
                        // exists (CreateNodeResponse succeeded), so message 2+ must drain through the
                        // existing thread (GetMeshNodeStream(threadPath).Update → PendingUserMessages), NOT
                        // re-StartThread (which would create a SECOND thread — and on the user home routes
                        // to the un-writable 'User' partition → denied → the message silently drops, the
                        // SidePanelChatTenMessagesTest "2nd user bubble never appears" bug). The submit
                        // decision (line ~998 `if (string.IsNullOrEmpty(threadPath))`) reads this field, so
                        // it must be set the moment the node exists — NOT gated on the readability wait below.
                        threadPath = path;
                        // 🚨 Redirect ONLY once the thread node is actually READABLE on its own stream.
                        // Navigating on the bare CreateNode ack races the thread's per-node hub
                        // activation: the target page subscribes to a not-yet-ready node, the render
                        // throws, and the whole side panel blanks (the "blackout"). Subscribing here is
                        // "redirect in .Subscribe() when the thread is written" — no miss.
                        Hub.GetWorkspace().GetMeshNodeStream(path)
                            .Where(n => n is not null)
                            .Take(1)
                            .Timeout(TimeSpan.FromSeconds(10))
                            .Subscribe(
                                ready => InvokeAsync(() =>
                                {
                                    if (_isDisposed) return;
                                    Logger.LogInformation(
                                        "[Chat] Thread created+readable path={Path} elapsed={Ms}ms",
                                        path, perfSw.ElapsedMilliseconds);
                                    threadPath = path;
                                    threadName = ready!.Name;
                                    UpdateSidePanelTitle();
                                    // The thread is readable — NOW release the composer text
                                    // that was kept visible during allocation (see the
                                    // deferred clear in SubmitMessageCore) and hand over to
                                    // the thread view / redirect.
                                    MessageText = null;
                                    if (monacoEditor != null)
                                        _ = ClearMonacoAsync();
                                    if (isCompact)
                                        NavigationManager.NavigateTo($"/{path}");
                                    else
                                        SidePanelState.SetContentPath(path);
                                    showSubmissionProgress = false;
                                    lastSubmittedText = null;
                                    StateHasChanged();
                                }),
                                ex => onError(ex.Message));
                    },
                    onError: onError);
            }
            else
            {
                Logger.LogInformation("[Chat] Submitting to thread {Thread}", threadPath);
                // Drain through the thread's embedded composer (Thread.Composer): the
                // harness/agent/model selection comes from the composer the selectors area
                // is data-bound to, the typed text + live context/attachments are passed
                // explicitly, and the composer empties itself in the same atomic update.
                Hub.SubmitComposer(
                    threadPath: threadPath!, // non-null: isNewThread == false ⇔ threadPath non-empty
                    userText: userMessageText!,
                    contextPath: safeContext,
                    attachments: capturedAttachments,
                    createdBy: createdBy,
                    authorName: authorName,
                    onError: onError);
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
    /// Dispatches a parsed leading "/word args" to the nodeType:Skill resolved by that slash word
    /// (<see cref="ResolveSkillNodeAndRun"/>). Updates
    /// <see cref="lastCommandStatus"/> for the breadcrumb. No await on hub calls — skill actions are
    /// in-process GUI logic (open a picker, load the content window) or reactive subscriptions.
    /// </summary>
    private async Task HandleSlashCommandAsync(ParsedCommand parsedCommand)
    {
        // Resolve a nodeType:Skill by slash word and run its action (Pick → combobox,
        // OpenContent → content window, …). Skills are declarative mesh nodes (the built-in
        // /agent /model /harness + any Space/NodeType/user-defined one) — there is no C# registry.
        ResolveSkillNodeAndRun(parsedCommand);
        await InvokeAsync(StateHasChanged);
    }

    /// <summary>
    /// Handle a harness AUTH slash-command (login/logout) LOCALLY — the portal owns the credential; the
    /// headless CLI cannot authenticate itself, so forwarding <c>/login</c> to it did nothing. Terminal-native,
    /// like Claude Code: paste the credential right in the composer and pick the method.
    /// <list type="bullet">
    /// <item><c>/login &lt;key&gt;</c> — infers the method from the token shape (Anthropic Console key
    ///   <c>sk-ant-api…</c> → ANTHROPIC_API_KEY; subscription token <c>sk-ant-oat…</c> → CLAUDE_CODE_OAUTH_TOKEN).</item>
    /// <item><c>/login key &lt;k&gt;</c> — force the API-key method; <c>/login token &lt;t&gt;</c> — force the
    ///   subscription OAuth token (from <c>claude setup-token</c>).</item>
    /// <item><c>/login gateway &lt;base-url&gt; &lt;token&gt;</c> — a gateway/proxy (ANTHROPIC_AUTH_TOKEN + ANTHROPIC_BASE_URL).</item>
    /// <item><c>/logout</c> — forget the stored credential.</item>
    /// </list>
    /// The credential is encrypted at rest (<see cref="MeshWeaver.AI.IProviderKeyProtector"/>) and stored as
    /// the harness's <c>ModelProvider</c> node at <c>{user}/_Memex/{harnessId}</c> — the SAME path
    /// <c>ChatClientCredentialResolver.ResolveConnectCredential</c> reads, so writer and reader agree.
    /// </summary>
    private void HandleHarnessAuthCommand(MeshWeaver.AI.IHarness harness, MeshWeaver.AI.HarnessCommand cmd, string? rawText)
    {
        if (cmd.Kind == MeshWeaver.AI.HarnessCommandKind.Disconnect)
        {
            ClearHarnessCredential(harness);
            return;
        }
        // Connect: everything after the leading "/login" word is the pasted argument(s).
        var rest = (rawText ?? string.Empty).Trim();
        var sp = rest.IndexOf(' ');
        var args = sp < 0 ? string.Empty : rest[(sp + 1)..].Trim();
        if (string.IsNullOrEmpty(args))
        {
            ShowSkillStatus(
                $"To connect {harness.Definition.DisplayName}, paste a credential here — "
                + "`/login <key>` (Anthropic Console key), `/login token <oauth-token>` "
                + "(from `claude setup-token`), or `/login gateway <base-url> <token>`.", false);
            return;
        }
        var parts = args.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        string method, credential;
        string? baseUrl = null;
        var head = parts[0].ToLowerInvariant();
        if (head == "key" && parts.Length >= 2) { method = "apiKey"; credential = parts[1]; }
        else if (head == "token" && parts.Length >= 2) { method = "oauth"; credential = parts[1]; }
        else if (head == "gateway" && parts.Length >= 3) { method = "authToken"; baseUrl = parts[1]; credential = parts[2]; }
        else
        {
            credential = parts[0];
            // Infer: Anthropic subscription OAuth tokens are "sk-ant-oat…"; Console keys "sk-ant-api…".
            method = credential.StartsWith("sk-ant-oat", StringComparison.OrdinalIgnoreCase) ? "oauth" : "apiKey";
        }
        StoreHarnessCredential(harness, method, credential, baseUrl);
    }

    /// <summary>Encrypt + persist the pasted credential as the harness's ModelProvider node (create-or-update).</summary>
    private void StoreHarnessCredential(MeshWeaver.AI.IHarness harness, string method, string credential, string? baseUrl)
    {
        var accessService = Hub.ServiceProvider.GetService<AccessService>();
        var owner = accessService?.Context?.ObjectId ?? accessService?.CircuitContext?.ObjectId;
        if (string.IsNullOrEmpty(owner))
        {
            ShowSkillStatus("Can't store the credential — you don't appear to be signed in.", true);
            return;
        }
        var providerPath = $"{MeshWeaver.AI.ModelProviderNodeType.UserNamespacePath(owner)}/{harness.Id}";
        var protector = Hub.ServiceProvider.GetService<MeshWeaver.AI.IProviderKeyProtector>();
        var stored = protector is null ? credential : protector.Protect(credential);
        var node = MeshWeaver.Mesh.MeshNode.FromPath(providerPath) with
        {
            NodeType = MeshWeaver.AI.ModelProviderNodeType.NodeType,
            Name = harness.Definition.DisplayName,
            Icon = "/static/NodeTypeIcons/key.svg",
            Content = new MeshWeaver.AI.ModelProviderConfiguration
            {
                Provider = harness.Id,
                ApiKey = stored,
                AuthMethod = method,
                Endpoint = baseUrl,
                CreatedAt = DateTimeOffset.UtcNow
            }
        };
        var label = method switch
        {
            "apiKey" => "API key",
            "oauth" => "subscription token",
            "authToken" => "gateway token",
            _ => "credential"
        };
        MeshQuery.CreateOrUpdateNode(node).Subscribe(
            _ => InvokeAsync(() =>
            {
                ShowSkillStatus($"{harness.Definition.DisplayName} connected with your {label} — new chats will use it.", false);
                StateHasChanged();
            }),
            ex => InvokeAsync(() =>
            {
                ShowSkillStatus($"Couldn't store the credential: {ex.Message}", true);
                StateHasChanged();
            }));
    }

    /// <summary>Delete the harness's stored credential node (the <c>/logout</c> path).</summary>
    private void ClearHarnessCredential(MeshWeaver.AI.IHarness harness)
    {
        var accessService = Hub.ServiceProvider.GetService<AccessService>();
        var owner = accessService?.Context?.ObjectId ?? accessService?.CircuitContext?.ObjectId;
        if (string.IsNullOrEmpty(owner))
        {
            ShowSkillStatus("Nothing to disconnect — you don't appear to be signed in.", true);
            return;
        }
        var providerPath = $"{MeshWeaver.AI.ModelProviderNodeType.UserNamespacePath(owner)}/{harness.Id}";
        MeshQuery.DeleteNode(providerPath).Subscribe(
            _ => InvokeAsync(() =>
            {
                ShowSkillStatus($"{harness.Definition.DisplayName} disconnected — its stored credential was removed.", false);
                StateHasChanged();
            }),
            ex => InvokeAsync(() =>
            {
                ShowSkillStatus($"Couldn't disconnect: {ex.Message}", true);
                StateHasChanged();
            }));
    }

    /// <summary>
    /// A status-bar chip was clicked — do exactly what typing the matching slash-command does:
    /// <list type="bullet">
    /// <item><c>harness</c> / mesh <c>agent</c> / mesh <c>model</c> → <see cref="HandleSlashCommandAsync"/>,
    /// which resolves the skill and pops the SAME <c>OpenPicker</c> combobox (no hand-rolled dropdown).</item>
    /// <item>a CLI harness's OWN model/effort (ClaudeCode / Copilot) → forward <c>/{command}</c> 1:1 to the
    /// CLI, exactly as the FORWARD dispatch does for a typed command.</item>
    /// </list>
    /// </summary>
    private Task OnStatusChipClick(string commandName, bool cliOwned)
    {
        if (cliOwned)
        {
            // The model/effort belong to the user's OWN CLI subscription — forward, don't pop a mesh picker.
            if (!string.IsNullOrEmpty(threadPath))
                Hub.SubmitMessage(threadPath, $"/{commandName}");
            return Task.CompletedTask;
        }
        var parsed = ChatParser.Parse($"/{commandName}").Command;
        return parsed is not null ? HandleSlashCommandAsync(parsed) : Task.CompletedTask;
    }

    /// <summary>
    /// Inline SVG glyph for a status chip / completion category — crisp + themeable (currentColor),
    /// replacing the emoji. 16×16, single path. Kinds: harness, model, effort, agent, skill.
    /// </summary>
    private static MarkupString ChipSvg(string kind)
    {
        var path = kind switch
        {
            "harness" => "M8 1.2 14 4.6v6.8L8 14.8 2 11.4V4.6z",                                  // hexagon
            "model"   => "M8 1.3l1.7 4.4 4.6.3-3.5 3 1.1 4.5L8 11l-3.9 2.5 1.1-4.5-3.5-3 4.6-.3z", // sparkle/star
            "effort"  => "M9.2 1 3.4 9.1H7l-1 5.9 5.9-8.4H8.2z",                                    // lightning bolt
            "agent"   => "M5 3h6a2 2 0 0 1 2 2v6a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2V5a2 2 0 0 1 2-2zm.8 4.2a1 1 0 1 0 0 2 1 1 0 0 0 0-2zm4.4 0a1 1 0 1 0 0 2 1 1 0 0 0 0-2z", // chip/robot
            "skill"   => "M8 1l2 4 4.4.5-3.2 3 .9 4.4L8 10.8 3.9 12.9l.9-4.4L1.6 5.5 6 5z",         // ribbon
            _         => "M8 2a6 6 0 1 0 0 12A6 6 0 0 0 8 2z",                                       // dot
        };
        return new MarkupString(
            $"<svg class=\"chip-svg\" viewBox=\"0 0 16 16\" width=\"12\" height=\"12\" fill=\"currentColor\" aria-hidden=\"true\"><path d=\"{path}\"/></svg>");
    }

    /// <summary>
    /// The harness's OWN brand icon for the status chip — the inline SVG carried on
    /// <see cref="MeshWeaver.AI.Harness.Icon"/> (e.g. Claude Code's terracotta burst). Falls back to the
    /// generic hexagon glyph when the harness is unknown or ships no inline SVG. This is why the harness
    /// logo now renders instead of the placeholder hexagon.
    /// </summary>
    private MarkupString HarnessIcon(string? harnessId)
    {
        var harness = MeshWeaver.AI.HarnessNodeType.ResolveHarness(Hub.ServiceProvider, harnessId);
        var icon = harness?.Definition.Icon;
        // Only raw inline SVG (starts with '<') is safe to emit as markup; a URL/path is not an icon here.
        return !string.IsNullOrEmpty(icon) && icon.TrimStart().StartsWith('<')
            ? new MarkupString($"<span class=\"chip-svg\" aria-hidden=\"true\">{icon}</span>")
            : ChipSvg("harness");
    }

    /// <summary>
    /// Resolves a <c>nodeType:Skill</c> mesh node by its slash word and runs its action. Built-in
    /// skills (/agent, /model, /harness — Pick behaviours shipped by
    /// <see cref="MeshWeaver.AI.BuiltInSkillProvider"/>) AND any Space/NodeType/user-defined skill
    /// resolve here, with namespace inheritance (<see cref="MeshWeaver.AI.SkillNodeType.SkillQueries"/>).
    /// Reactive: queries the mesh once, then runs the matched skill or reports "unknown command".
    /// </summary>
    private void ResolveSkillNodeAndRun(ParsedCommand parsed)
    {
        var workspace = Hub.ServiceProvider.GetService<IWorkspace>();
        if (workspace is null)
        {
            ShowSkillStatus($"Unknown command: /{parsed.Name}", true);
            return;
        }

        var queries = MeshWeaver.AI.SkillNodeType.SkillQueries(initialContext, _userHome);
        // Run the skill-resolve read under the durable circuit user — this method is reached from
        // SubmitMessageCore via InvokeAsync and the query fans out on the synced-query scheduler where
        // the AsyncLocal context is gone; context-null here makes the /command either resolve nothing
        // ("Unknown command") or surface skills the user can't read. RunUnderCircuitUser mirrors RunPicker.
        RunUnderCircuitUser(AgentPickerProjection.ObserveSnapshot(workspace, Hub, $"skills|{initialContext}|{_userHome}", queries))
            .Take(1)
            .Timeout(TimeSpan.FromSeconds(5))
            .Subscribe(
                snapshot => InvokeAsync(() =>
                {
                    var match = MeshWeaver.AI.SkillNodeType.ProjectSkills(snapshot, Hub.JsonSerializerOptions)
                        .FirstOrDefault(s => string.Equals(s.Id, parsed.Name, StringComparison.OrdinalIgnoreCase));
                    if (match is null)
                    {
                        ShowSkillStatus($"Unknown command: /{parsed.Name}", true);
                        return;
                    }
                    RunSkill(match, parsed);
                }),
                _ => InvokeAsync(() => ShowSkillStatus($"Unknown command: /{parsed.Name}", true)));
    }

    /// <summary>
    /// Runs a resolved skill's action: <c>Pick</c> → combobox (write the pick to the composer);
    /// <c>OpenContent</c> → load into the content window. A pure INSTRUCTION skill invoked with
    /// trailing text (<c>/code build a tracker</c>) digests the text into a normal round: the typed
    /// task is submitted prefixed with a <c>load_skill</c> directive
    /// (<see cref="MeshWeaver.AI.SkillInfo.ToSubmissionText"/>), so the agent loads the skill's
    /// instructions and applies them to the task. With no trailing text it shows the skill's help.
    /// </summary>
    private void RunSkill(MeshWeaver.AI.SkillInfo skill, ParsedCommand parsed)
    {
        var term = parsed.Arguments.Length == 0 ? null : LastSegment(parsed.RawArguments.Trim());
        var action = skill.Definition.Action;
        switch (action?.Kind)
        {
            case MeshWeaver.AI.SkillActionKind.Pick:
                var picker = skill.ToPickerRequest(term);
                if (picker is not null)
                {
                    lastCommandStatus = null;
                    lastCommandStatusIsError = false;
                    OpenPicker(picker);
                }
                break;
            case MeshWeaver.AI.SkillActionKind.OpenContent:
                // Load the manual/document into the content window (side panel) and make it visible.
                var path = string.IsNullOrEmpty(action.ContentPath) ? skill.Path : action.ContentPath;
                if (!string.IsNullOrEmpty(path))
                {
                    SidePanelState.SetTitle(skill.Name ?? skill.Id);
                    SidePanelState.OpenWithContent(path);
                }
                ShowSkillStatus(skill.Name ?? skill.Id, false);
                break;
            case MeshWeaver.AI.SkillActionKind.Navigate:
                // "Take me there" — resolve the row resiliently, then navigate PANE-AWARE. Target is the
                // typed argument (or, when empty, the skill's own ContentPath / path).
                var navRow = string.IsNullOrWhiteSpace(parsed.RawArguments)
                    ? (string.IsNullOrEmpty(action.ContentPath) ? skill.Path : action.ContentPath)
                    : parsed.RawArguments.Trim();
                if (string.IsNullOrWhiteSpace(navRow))
                {
                    ShowSkillStatus("Where to? Try `/navigate <path or what you're looking for>`.", true);
                    break;
                }
                lastCommandStatus = null;
                lastCommandStatusIsError = false;
                RunNavigate(navRow!);
                break;
            default:
                // Instruction skill + typed task → re-enter the normal submission path with the
                // composed message (it never starts with '/', so it cannot re-parse as a command).
                var submission = skill.ToSubmissionText(parsed.RawArguments);
                if (submission is not null)
                {
                    MessageText = submission;
                    SubmitMessageCore();
                }
                else
                    ShowSkillStatus(skill.Description ?? $"/{skill.Id}", false);
                break;
        }
    }

    /// <summary>
    /// Resolves a navigation row (resilient: direct path first, else free-text context; always the best
    /// available match) and applies the result PANE-AWARE. Reactive — resolve then Subscribe, never await.
    /// </summary>
    private void RunNavigate(string row)
    {
        var resolver = new MeshWeaver.AI.Navigation.NavigationResolver(Hub);
        RunUnderCircuitUser(resolver.Resolve(row, initialContext))
            .Take(1)
            .Timeout(TimeSpan.FromSeconds(8))
            .Subscribe(
                resolution => InvokeAsync(() =>
                {
                    if (_isDisposed) return;
                    ApplyNavigation(resolution);
                }),
                ex => InvokeAsync(() =>
                {
                    if (_isDisposed) return;
                    ShowSkillStatus($"Couldn't navigate to “{row}”: {ex.Message}", true);
                }));
    }

    /// <summary>
    /// Applies a resolved navigation. A matched SKILL is RUN (a skill does more than a route change); an app
    /// ROUTE navigates the main view by URL; a NODE navigates pane-aware; Unresolved surfaces the message.
    /// </summary>
    private void ApplyNavigation(MeshWeaver.AI.Navigation.NavigationResolution resolution)
    {
        switch (resolution.Kind)
        {
            case MeshWeaver.AI.Navigation.NavigationTargetKind.Skill:
                var skillId = LastSegment(resolution.Target);
                // Guard against re-entering /navigate; in that degenerate case just open its page.
                if (string.Equals(skillId, "navigate", StringComparison.OrdinalIgnoreCase))
                {
                    ApplyPaneAwareNavigation(resolution.Target!);
                    break;
                }
                ResolveSkillNodeAndRun(new MeshWeaver.AI.Parsing.ParsedCommand
                {
                    Name = skillId ?? string.Empty,
                    Arguments = [],
                    RawArguments = string.Empty,
                });
                break;
            case MeshWeaver.AI.Navigation.NavigationTargetKind.Route:
                // A page route (e.g. /search?…) — a page, not a renderable node, so always the main view.
                NavigationService.NavigateTo(resolution.Target!);
                ShowSkillStatus(resolution.Message ?? $"Opened {resolution.Target}", false);
                break;
            case MeshWeaver.AI.Navigation.NavigationTargetKind.Node:
                ApplyPaneAwareNavigation(resolution.Target!);
                ShowSkillStatus(resolution.Message ?? $"Opened {LastSegment(resolution.Target)}", false);
                break;
            default:
                ShowSkillStatus(resolution.Message ?? "Couldn't find anywhere to go.", true);
                break;
        }
    }

    /// <summary>
    /// The pane-aware rule (identical to <see cref="OnContextChipClicked"/>): a thread in the SIDE panel
    /// changes the URL and navigates the MAIN pane; a thread in the MAIN pane opens the target in the SIDE
    /// panel — so the conversation and the place it sent you sit side by side.
    /// </summary>
    private void ApplyPaneAwareNavigation(string path)
    {
        var clean = path.TrimStart('/');
        if (IsInSidePanel)
            NavigationService.NavigateTo($"/{clean}");
        else
        {
            SidePanelState.SetTitle(LastSegment(clean));
            SidePanelState.OpenWithContent(clean);
        }
    }

    /// <summary>Surface a status / error line under the chat input (replaces the old command status callback).</summary>
    private void ShowSkillStatus(string message, bool isError)
    {
        pendingPicker = null;
        pickerNodes = [];
        lastCommandStatus = message;
        lastCommandStatusIsError = isError;
    }

    /// <summary>The active non-MeshWeaver harness (resolved from the composer's harness selection), or
    /// null when MeshWeaver is active. Drives harness-owned command dispatch + autocomplete.</summary>
    private MeshWeaver.AI.IHarness? ActiveHarness()
    {
        var harness = MeshWeaver.AI.HarnessNodeType.ResolveHarness(Hub.ServiceProvider, boundHarness);
        return harness is null
               || string.Equals(harness.Id, MeshWeaver.AI.Harnesses.MeshWeaver, StringComparison.OrdinalIgnoreCase)
            ? null : harness;
    }

    private void DismissWidget()
    {
        pendingPicker = null;
        pickerNodes = [];
        StateHasChanged();
    }

    /// <summary>
    /// When the command picker has just opened, move keyboard focus from the Monaco editor onto the
    /// picker widget so ↑/↓/Enter/Escape operate the list (Monaco would otherwise swallow the arrows).
    /// </summary>
    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        await base.OnAfterRenderAsync(firstRender);

        // #128 auto-scroll: attach the MutationObserver-based stick-to-bottom module to the message
        // container once it exists. HideEmptyState omits the container (compact/dashboard mode), so
        // guard on the module not yet being attached rather than firstRender alone — the container may
        // first appear on a later render when a thread loads.
        if (_scrollHandle is null && !_isDisposed && !ViewModel.HideEmptyState)
        {
            try
            {
                _scrollModule ??= await JSRuntime.InvokeAsync<IJSObjectReference>(
                    "import", "./_content/MeshWeaver.Blazor.Portal/Chat/ThreadChatView.razor.js");
                _scrollHandle = await _scrollModule.InvokeAsync<IJSObjectReference>("attach", messagesContainer);
            }
            catch (Exception ex) when (!_isDisposed)
            {
                Logger.LogDebug(ex, "[ThreadChat:{InstanceId}] Failed to attach auto-scroll module", _instanceId);
            }
        }

        if (_focusPickerOnRender && pendingPicker is not null)
        {
            _focusPickerOnRender = false;
            try { await _pickerWidget.FocusAsync(); }
            catch { /* widget may not be in the DOM yet — harmless */ }
        }
        // Focus the inner element via the ElementReference (an awaitable ValueTask) rather than the
        // component's own FocusAsync() — that overload is `async void` (await base.Element.FocusAsync()),
        // so a post-await JS-interop failure would escape on the circuit's sync context and a surrounding
        // try could never catch it. Awaiting the ElementReference here keeps the failure catchable.
        if (_focusTitleOnRender && _titleEditField is not null)
        {
            _focusTitleOnRender = false;
            try { await _titleEditField.Element.FocusAsync(); }
            catch { /* field may not be focusable yet — harmless */ }
        }
        if (_focusDescOnRender && _descEditField is not null)
        {
            _focusDescOnRender = false;
            try { await _descEditField.Element.FocusAsync(); }
            catch { /* field may not be focusable yet — harmless */ }
        }
        if (_seedMonacoOnRender && monacoEditor is not null)
        {
            _seedMonacoOnRender = false;
            try { await monacoEditor.SetValueAsync(MessageText ?? ""); }
            catch { /* editor may not be ready yet — the Value binding still seeds it */ }
        }
    }

    /// <summary>
    /// Generic node picker for any <see cref="NodePickerRequest"/>: queries the mesh for the
    /// command's node query and either auto-selects an exact name match (when the user typed an
    /// argument, e.g. <c>/model gpt-4o</c>) or shows the picker list. One render path serves every
    /// node-pick command — agent, model, harness, and any module-defined one.
    /// </summary>
    private void OpenPicker(NodePickerRequest picker)
    {
        var workspace = Hub.ServiceProvider.GetService<IWorkspace>();
        if (workspace == null)
            return;

        // 🚨 Resolve the circuit USER from the DURABLE per-circuit source. OpenPicker itself is reached
        // AFTER the skill-resolve Rx hop (ResolveSkillNodeAndRun → ObserveSnapshot.Subscribe → InvokeAsync
        // → RunSkill → OpenPicker), so the circuit AccessService's AsyncLocals (Context / CircuitContext)
        // are ALREADY nulled here — capturing off them yields null and the picker query runs context-null
        // → WrapWithPerUserRls BYPASS (combobox shows agents/models the user has no Read on) or DENY.
        // ResolveCircuitUser reads ICircuitContextAccessor.UserContext, which survives every hop; RunPicker
        // re-establishes it at the query's subscribe so RLS filters correctly.
        var accessService = Hub.ServiceProvider.GetService<AccessService>();
        var pickerUser = ResolveCircuitUser();

        var field = picker.ComposerField;
        var isAgent = string.Equals(field, nameof(MeshWeaver.AI.ThreadComposer.AgentName), StringComparison.OrdinalIgnoreCase);
        var isModel = string.Equals(field, nameof(MeshWeaver.AI.ThreadComposer.ModelName), StringComparison.OrdinalIgnoreCase);

        // Harness / custom Command pickers carry no context-scoped union — run them
        // straight off their declared query (no navigation read needed).
        if (!isAgent && !isModel)
        {
            RunPicker(workspace, picker, new[] { picker.Query },
                $"picker|{field}|{picker.Query}", accessService, pickerUser);
            return;
        }

        // Agent and model pickers must surface NOT ONLY the built-in catalog but also the ones declared
        // in the CURRENT context's namespace (+ ancestors) AND the context node's NodeType namespace
        // (+ ancestors) — the SAME context-scoped query union AgentChatClient resolves agents/models
        // from at execution time. AgentPickerProjection.BuildAgentQueries / BuildModelQueries is the
        // single source of truth for that union (built-in + path:{current} ancestors +
        // namespace:{nodeType} selfAndAncestors); inlining the single global `namespace:Agent` query
        // here is exactly why a Space's own agent/model never appeared in the picker.
        //
        // 🚨 TIMING-SAFE: the navigation context resolves ASYNCHRONOUSLY, so the stale
        // `_currentNavContext` field (and the seeded `initialContext`) are frequently NULL in the
        // floating side-panel chat — collapsing the union to the built-in query only (the atioz
        // "Space agent missing from /agent" bug). We instead read the LATEST RESOLVED context off
        // NavigationService.NavigationContext (ReplaySubject(1) → the last value replays
        // immediately). Take(1) after a short Timeout so a still-resolving context can't hang the
        // picker; the timeout/error branch falls back to the seeded initialContext (never null →
        // never block). AgentPickerProjection.DerivePickerContext is the single source of truth for
        // turning that resolved context into (currentPath, nodeTypePath).
        NavigationService.NavigationContext
            .Select(ctx => AgentPickerProjection.DerivePickerContext(ctx, initialContext))
            .Take(1)
            .Timeout(TimeSpan.FromSeconds(2))
            .Catch<AgentPickerProjection.PickerContext, Exception>(_ =>
                System.Reactive.Linq.Observable.Return(
                    AgentPickerProjection.DerivePickerContext(null, initialContext)))
            .Subscribe(pc => InvokeAsync(() =>
            {
                if (_isDisposed) return;
                var queries = isAgent
                    ? AgentPickerProjection.BuildAgentQueries(_userHome, AgentPickerProjection.PartitionOf(pc.ContextPath))
                    : AgentPickerProjection.BuildModelQueries(pc.ContextPath, pc.NodeTypePath, userPath: _userHome);
                var cacheKey = $"picker|{field}|{pc.ContextPath}|{pc.NodeTypePath}|{string.Join("|", queries)}";
                RunPicker(workspace, picker, queries, cacheKey, accessService, pickerUser);
            }));
    }

    /// <summary>
    /// Runs the resolved picker query union: snapshots the mesh once, orders by the node's
    /// universal Order field (then Name), auto-selects an exact term match or shows the list.
    /// Shared by the context-scoped agent/model branch and the declared-query harness/custom branch.
    /// </summary>
    private void RunPicker(IWorkspace workspace, NodePickerRequest picker, string[] queries, string cacheKey,
        AccessService? accessService, AccessContext? pickerUser)
    {
        // 🚨 Run the picker query under the CAPTURED circuit user, re-established HERE at the
        // subscribe via Observable.Using(SwitchAccessContext). ObserveSnapshot subscribes on the
        // NavigationContext→InvokeAsync hop where the ambient AccessContext may be cleared/leaked;
        // running the synced query context-null makes WrapWithPerUserRls BYPASS → the combobox
        // surfaces agents/models the user has no Read on (wrong access rights). The scope flows
        // through ObserveSnapshot's IIoPool hops (the pool carries the AsyncLocal), so RLS filters
        // the picker to exactly what this user can read.
        Observable.Using(
                () => pickerUser is not null && accessService is not null
                    ? accessService.SwitchAccessContext(pickerUser)
                    : (IDisposable)System.Reactive.Disposables.Disposable.Empty,
                _ => AgentPickerProjection.ObserveSnapshot(workspace, Hub, cacheKey, queries))
            .Take(1)
            .Subscribe(snapshot => InvokeAsync(() =>
            {
                if (_isDisposed) return;
                // Order by the node's universal Order field (then Name) so the picker leads with the
                // catalog head — Assistant (order:-1) for agents, the flagship model for models. This
                // is NOT command-specific logic: it's the generic "order nodes by Order" every picker
                // wants. (The query's `sort:order` is lost when the synced-query snapshot re-buckets by
                // path into a dict, so the order must be re-applied here on the node data.)
                var nodes = snapshot.Where(n => !string.IsNullOrEmpty(n.Path))
                    .OrderBy(n => n.Order ?? 0)
                    .ThenBy(n => n.Name ?? n.Id, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                if (!string.IsNullOrEmpty(picker.SearchTerm))
                {
                    // Exact name/last-segment match on a SELECTABLE node → switch immediately.
                    // Group-title nodes (a model provider) are never a selection target.
                    var exact = nodes.FirstOrDefault(n =>
                        !IsPickerHeaderNode(n) && PickerNodeMatches(n, picker.SearchTerm, exact: true));
                    if (exact != null)
                    {
                        SelectFromPicker(picker, exact);
                        return;
                    }
                    // Pre-filter selectable nodes to the term, keeping each surviving node's group
                    // title (a provider whose models all filtered out drops together with them).
                    var keepGroups = nodes
                        .Where(n => !IsPickerHeaderNode(n) && PickerNodeMatches(n, picker.SearchTerm!, exact: false))
                        .Select(n => ParentPath(n.Path))
                        .ToHashSet(StringComparer.OrdinalIgnoreCase);
                    nodes = nodes.Where(n => IsPickerHeaderNode(n)
                        ? keepGroups.Contains(n.Path)
                        : PickerNodeMatches(n, picker.SearchTerm!, exact: false)).ToList();
                }

                // Group selectable nodes under their non-selectable title (model providers → their
                // nested models). No titles present (the /agent, /harness pickers) → the flat
                // Order/Name list above stays as-is.
                if (nodes.Any(IsPickerHeaderNode))
                    nodes = ArrangePickerGroups(nodes);

                pendingPicker = picker;
                pickerNodes = nodes;
                _pickerHighlight = FirstSelectablePickerIndex(); // skip a leading group title
                _focusPickerOnRender = true; // move focus off Monaco onto the widget so ↑/↓ reach it
                lastCommandStatus = null;
                StateHasChanged();
            }));
    }

    /// <summary>
    /// Keyboard navigation of the command picker list: ↑/↓ move the highlight (wrapping), Enter commits
    /// the highlighted node, Escape dismisses. Fires on the focused widget (see <see cref="_focusPickerOnRender"/>),
    /// so the arrow keys are handled here instead of by the Monaco editor.
    /// </summary>
    private void OnPickerKeyDown(Microsoft.AspNetCore.Components.Web.KeyboardEventArgs e)
    {
        if (pendingPicker is null || pickerNodes.Count == 0)
            return;

        switch (e.Key)
        {
            case "ArrowDown":
                _pickerHighlight = NextSelectablePickerIndex(_pickerHighlight, +1);
                StateHasChanged();
                break;
            case "ArrowUp":
                _pickerHighlight = NextSelectablePickerIndex(_pickerHighlight, -1);
                StateHasChanged();
                break;
            case "Enter":
                if (_pickerHighlight >= 0 && _pickerHighlight < pickerNodes.Count
                    && !IsPickerHeaderNode(pickerNodes[_pickerHighlight]))
                    SelectFromPicker(pendingPicker, pickerNodes[_pickerHighlight]);
                break;
            case "Escape":
                DismissWidget();
                break;
        }
    }

    private static bool PickerNodeMatches(MeshNode node, string term, bool exact)
    {
        var name = node.Name ?? node.Id ?? "";
        var seg = LastSegment(node.Path) ?? "";
        return exact
            ? name.Equals(term, StringComparison.OrdinalIgnoreCase) || seg.Equals(term, StringComparison.OrdinalIgnoreCase)
            : name.Contains(term, StringComparison.OrdinalIgnoreCase) || seg.Contains(term, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A picker node that is a non-selectable GROUP TITLE rather than a selection target —
    /// currently a model provider (<c>ModelProvider</c>): the /model picker lists each provider
    /// as a heading with its models underneath. Other pickers (/agent, /harness) have no such
    /// nodes, so every entry stays selectable. Selecting a provider would write a provider PATH
    /// into the model field (a non-model selection) — exactly the bug this prevents.
    /// </summary>
    private static bool IsPickerHeaderNode(MeshNode node) =>
        string.Equals(node.NodeType, ModelProviderNodeType.NodeType, StringComparison.OrdinalIgnoreCase);

    /// <summary>Parent path (everything before the last '/') — a nested model's provider path.</summary>
    private static string ParentPath(string? path)
    {
        if (string.IsNullOrEmpty(path)) return "";
        var i = path.LastIndexOf('/');
        return i <= 0 ? path : path[..i];
    }

    /// <summary>
    /// Orders selectable nodes under their group title. A title's path is the group key; a nested
    /// model's key is its parent path (== its provider's path), so each title is immediately
    /// followed by its own models (title first, then models by Order then Name).
    /// </summary>
    private static List<MeshNode> ArrangePickerGroups(List<MeshNode> nodes) =>
        nodes
            .OrderBy(n => IsPickerHeaderNode(n) ? n.Path : ParentPath(n.Path), StringComparer.OrdinalIgnoreCase)
            .ThenBy(n => IsPickerHeaderNode(n) ? 0 : 1)
            .ThenBy(n => n.Order ?? 0)
            .ThenBy(n => n.Name ?? n.Id, StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>First selectable (non-title) index, or -1 when the list is titles-only / empty.</summary>
    private int FirstSelectablePickerIndex()
    {
        for (var i = 0; i < pickerNodes.Count; i++)
            if (!IsPickerHeaderNode(pickerNodes[i]))
                return i;
        return -1;
    }

    /// <summary>
    /// Next selectable index from <paramref name="from"/> in direction <paramref name="dir"/> (±1),
    /// wrapping and skipping group titles. Returns <paramref name="from"/> when no other selectable
    /// node exists.
    /// </summary>
    private int NextSelectablePickerIndex(int from, int dir)
    {
        if (pickerNodes.Count == 0) return -1;
        for (var step = 1; step <= pickerNodes.Count; step++)
        {
            var i = ((from + dir * step) % pickerNodes.Count + pickerNodes.Count) % pickerNodes.Count;
            if (!IsPickerHeaderNode(pickerNodes[i]))
                return i;
        }
        return from;
    }

    /// <summary>Writes the selected node's PATH onto the picker's composer field and dismisses.</summary>
    private void SelectFromPicker(NodePickerRequest picker, MeshNode node)
    {
        WriteComposerSelection(picker.ComposerField, node.Path);
        lastCommandStatus = $"{picker.Title}: {node.Name ?? node.Id}";
        lastCommandStatusIsError = false;
        pendingPicker = null;
        pickerNodes = [];
        StateHasChanged();
    }

    /// <summary>
    /// Generic composer-field writer — maps a camelCase <c>ThreadComposer</c> field name to the
    /// typed write. This is the ONE place that knows the composer's selectable fields; commands
    /// stay generic (they only name the field).
    /// </summary>
    private void WriteComposerSelection(string field, string? path)
    {
        switch (field)
        {
            case "harness": WriteComposerSelection(harness: path); break;
            case "agentName": WriteComposerSelection(agentPath: path); break;
            case "modelName": WriteComposerSelection(modelName: path); break;
            default:
                Logger.LogWarning("[ThreadChat:{InstanceId}] pick command targeted unknown composer field '{Field}'",
                    _instanceId, field);
                break;
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

    /// <summary>
    /// Click anywhere in the composer region focuses the editor (issue #199). The
    /// surrounding thread-nav-shell is <c>tabindex="0"</c> with a suppressed outline
    /// (for Alt-Tab thread cycling), so a click that misses Monaco's hit-target would
    /// otherwise focus the shell and every printable keystroke would be silently
    /// discarded while the input looks active. Focusing an already-focused editor is
    /// a no-op; failures are debug-logged (a disposed editor mid-teardown).
    /// </summary>
    private async Task FocusComposerAsync()
    {
        try
        {
            if (monacoEditor != null)
                await monacoEditor.FocusAsync();
        }
        catch (Exception ex) when (!_isDisposed)
        {
            Logger.LogDebug(ex, "[ThreadChat:{InstanceId}] Failed to focus Monaco editor", _instanceId);
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
                    if (updated.ContentAs<MeshWeaver.AI.Thread>(Hub.JsonSerializerOptions)?.RequestedStatus is null)
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

    // Navigation-context → composer-context chip is now a single reactive pipeline in OnInitialized
    // (NavigationService.NavigationContext → ResolveComposerContext → GetMeshNodeStream → chip). The
    // old imperative OnNavigationContextChanged handler was removed — the stream owns the chip.

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

        var normalized = path;
        var segments = path.Split('/');
        for (var i = 0; i < segments.Length; i++)
        {
            if (segments[i].StartsWith('_'))
            {
                normalized = string.Join('/', segments, 0, i);
                break;
            }
        }
        // 🎯 The user's home is addressed `User/{id}` (the User catalog entry), but a thread started
        // there belongs in the OWNER's writable partition `{id}` — NEVER the system-managed `User`
        // partition, which the user lacks Thread permission on (StartThread there is denied → the denial
        // is a DeliveryFailure that the global portal error handler surfaces as a BLOCKING "Something went
        // wrong" modal that overlays + breaks the composer, and on Postgres the `user` schema may not even
        // exist → 42P01). Map `User/{id}[/…]` → `{id}`; bare `User` (no id) → "" so the ns derivation falls
        // through to createdBy (the owner's own partition). Both forms must leave the `User` partition.
        var ownerSegments = normalized.Split('/');
        if (string.Equals(ownerSegments[0], "User", StringComparison.OrdinalIgnoreCase))
            normalized = ownerSegments.Length >= 2 ? ownerSegments[1] : "";

        // A rogue/reserved ROUTE partition (login, welcome, settings, …) is NOT a real node — never use
        // it as a chat context. Reading it sends a GetDataRequest to a hub that never opens its init
        // gates (DataContextInit/MeshNodeInit) and the read hangs >30s. Treat a reserved context as none.
        return MeshWeaver.AI.AgentPickerProjection.IsReservedPartition(normalized) ? "" : normalized;
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

        // 🚨 A slash-command completion (/login, /logout, /harness, /agent, /model, …) is NOT a node
        // reference. Its Path defaults to its Label ("/login") because command items carry no node path
        // (AutocompleteToCompletion: Path = item.Path ?? item.Label). Accepting it must ONLY leave the
        // inserted "/word " text in the editor to be SUBMITTED as a command — it must NEVER be added as
        // an attachment and resolved as a node path: RequestDisplayName("/login") routes to address
        // 'login' (no such node) → an un-awaited DeliveryFailure → the "No node found at 'login'" modal,
        // the recurring "harness selection / login is broken" report. Node paths never start with '/'.
        if (path.StartsWith('/'))
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

    // Plain @-reference chips open the referenced node in a modal preview LAYERED OVER the thread
    // (issue #131), never navigating the main view away or replacing the side-panel thread.
    private Task OnChipClicked(string path) => OpenNodePreviewAsync(path);

    // The CONTEXT chip opens the context node in the OPPOSITE panel, so the thread and its subject
    // are visible side by side: a main-view thread peeks the context in the side panel (the same
    // peek ToggleSidePanel uses), a side-panel chat brings the context to the main view. Neither
    // direction replaces the conversation — the thread stays where it is.
    private Task OnContextChipClicked(string? path)
    {
        if (string.IsNullOrEmpty(path))
            return Task.CompletedTask;

        if (IsInSidePanel)
        {
            NavigationService.NavigateTo($"/{path.TrimStart('/')}");
            return Task.CompletedTask;
        }

        SidePanelState.SetTitle(LastSegment(path));
        SidePanelState.OpenWithContent(path);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Opens <paramref name="path"/> in a modal dialog on top of the thread so the user can inspect the
    /// full node — its own default layout area, exactly as its page renders it — without leaving the
    /// conversation. The thread stays mounted underneath; dismissing the dialog returns to it untouched.
    /// </summary>
    private async Task OpenNodePreviewAsync(string? path)
    {
        if (string.IsNullOrEmpty(path)) return;
        var parameters = new DialogParameters<string>
        {
            Title = LastSegment(path),
            PrimaryAction = string.Empty,   // no primary/confirm button — this is a read-only preview
            SecondaryAction = "Close",
            Width = "min(1100px, 90vw)",
            TrapFocus = true,
            Modal = true,
            PreventScroll = true,
        };
        try
        {
            await DialogService.ShowDialogAsync<NodePreviewDialog, string>(path, parameters);
        }
        catch (Exception ex) when (!_isDisposed)
        {
            Logger.LogDebug(ex, "[ThreadChat:{InstanceId}] Failed to open node preview for {Path}", _instanceId, path);
        }
    }

    // ─── Hero-header: inline title / description editing + Mark Done ───

    /// <summary>Enter title edit mode, seeding the editor with the current name and focusing it.</summary>
    // Rendered true on the first pass and set true before every StateHasChanged that MUST paint (entering
    // or leaving an inline title/description edit, focus). See ShouldRender.
    private bool _forceRender = true;

    /// <summary>
    /// Render isolation for the inline title/description editors. While an edit is in flight this large
    /// component would otherwise re-diff the WHOLE thread on every <c>Immediate</c> keystroke AND on every
    /// background thread-stream emission — visibly "bumping" the caret and making typing feel janky. We
    /// therefore SUPPRESS re-renders while editing, painting only when a real state transition needs it
    /// (flagged via <see cref="_forceRender"/>). The edited text is still captured server-side by the
    /// two-way binding regardless of ShouldRender, so Enter/blur commit the latest value. (GUI/DataBinding.md
    /// render-isolation — the sanctioned alternative to extracting a node-bound child component here.)
    /// </summary>
    protected override bool ShouldRender()
    {
        if (_forceRender)
        {
            _forceRender = false;
            return true;
        }
        return !(isEditingTitle || isEditingDescription);
    }

    private void BeginTitleEdit()
    {
        if (IsReadOnlyThread || isEditingTitle) return;
        editTitleText = ThreadViewModel?.Name ?? "";
        isEditingTitle = true;
        _focusTitleOnRender = true;
        _forceRender = true;
        StateHasChanged();
    }

    /// <summary>Enter commits the new title; Escape cancels without writing.</summary>
    private void OnTitleEditKeyDown(Microsoft.AspNetCore.Components.Web.KeyboardEventArgs e)
    {
        if (e.Key == "Enter") CommitTitleEdit();
        else if (e.Key == "Escape") CancelTitleEdit();
    }

    /// <summary>
    /// Writes the edited title to the thread node's <c>Name</c> (under the durable circuit user), but
    /// only when it actually changed and is non-empty — an empty title falls back to "Untitled thread"
    /// in the display rather than blanking the node name. Idempotent: a no-op when nothing changed.
    /// </summary>
    private void CommitTitleEdit()
    {
        if (!isEditingTitle) return;
        isEditingTitle = false;
        var trimmed = editTitleText?.Trim();
        if (!string.IsNullOrEmpty(trimmed)
            && !string.Equals(trimmed, ThreadViewModel?.Name, StringComparison.Ordinal)
            && !string.IsNullOrEmpty(threadPath))
        {
            var newName = trimmed;
            UpdateMeshNodeAsCircuitUser(threadPath, node => node with { Name = newName },
                ex => SurfaceError(ex, "Saving the title"));
        }
        _forceRender = true;
        StateHasChanged();
    }

    private void CancelTitleEdit()
    {
        isEditingTitle = false;
        _forceRender = true;
        StateHasChanged();
    }

    /// <summary>Enter description edit mode, seeding the editor with the current description.</summary>
    private void BeginDescriptionEdit()
    {
        if (IsReadOnlyThread || isEditingDescription) return;
        editDescriptionText = ThreadViewModel?.Description ?? "";
        isEditingDescription = true;
        _focusDescOnRender = true;
        _forceRender = true;
        StateHasChanged();
    }

    /// <summary>Escape cancels; Enter is left to insert newlines (the abstract is multi-line). Commit on blur.</summary>
    private void OnDescriptionEditKeyDown(Microsoft.AspNetCore.Components.Web.KeyboardEventArgs e)
    {
        if (e.Key == "Escape") CancelDescriptionEdit();
    }

    /// <summary>
    /// Writes the edited description to the thread node's <c>Description</c>. Unlike the title, an empty
    /// description is allowed and clears the field (writes null). No-op when unchanged.
    /// </summary>
    private void CommitDescriptionEdit()
    {
        if (!isEditingDescription) return;
        isEditingDescription = false;
        var trimmed = editDescriptionText?.Trim();
        var normalized = string.IsNullOrEmpty(trimmed) ? null : trimmed;
        if (!string.Equals(normalized, ThreadViewModel?.Description, StringComparison.Ordinal)
            && !string.IsNullOrEmpty(threadPath))
        {
            UpdateMeshNodeAsCircuitUser(threadPath, node => node with { Description = normalized },
                ex => SurfaceError(ex, "Saving the description"));
        }
        _forceRender = true;
        StateHasChanged();
    }

    private void CancelDescriptionEdit()
    {
        isEditingDescription = false;
        _forceRender = true;
        StateHasChanged();
    }

    /// <summary>
    /// Toggles the thread's Done state via the canonical <c>MarkThreadDone</c> hub extension (writes
    /// <c>RequestedStatus</c>; the owning hub reacts). Hidden while executing / read-only, so no guard race.
    /// </summary>
    private void ToggleThreadDone()
    {
        if (IsReadOnlyThread || string.IsNullOrEmpty(threadPath)) return;
        Hub.MarkThreadDone(threadPath, !(ThreadViewModel?.IsDone ?? false));
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
                    // Pick up a one-shot draft from "new thread from cell" (already-mounted composer case).
                    SeedPendingDraftIfAny();
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

        var query = string.IsNullOrEmpty(ns)
            ? "nodeType:Thread sort:LastModified-desc"
            : $"nodeType:Thread namespace:{ns}/_Thread sort:LastModified-desc";

        viewMode = ChatViewMode.ResumeThreads;
        resumeLoading = true;

        // 🚨 Live synced surface (hub.GetQuery), NOT IMeshService.Query. GetQuery is the
        // write-consistent, RLS-aware, snapshot stream backed by the IMeshNodeStreamCache — every
        // emission is the COMPLETE current set, so a thread UPDATE on submit re-emits the whole
        // list with the thread still present. The previous binding (MeshSearchView →
        // IMeshService.Query) was the eventually-consistent delta feed: it re-queries the store
        // per change and, before the submit write reached that read path, emitted a spurious
        // Removed that dropped the just-used thread from the list until a full reload (F5). Same
        // surface + pattern the agent/model pickers use (AgentPickerProjection.ObserveSnapshot).
        resumeSubscription?.Dispose();
        resumeSubscription = Hub.GetQuery($"resume-threads|{ns}", query)
            .Subscribe(
                snapshot => InvokeAsync(() =>
                {
                    if (_isDisposed) return;
                    var projected = snapshot
                        .Where(n => !string.IsNullOrEmpty(n.Path)
                            && string.Equals(n.NodeType, ThreadNodeType.NodeType, StringComparison.OrdinalIgnoreCase))
                        .OrderByDescending(n => n.LastModified)
                        .ToList();
                    var changed = VisibleThreadListChanged(ref _resumeThreadsKey, projected);
                    if (changed) resumeThreads = projected;
                    var wasLoading = resumeLoading;
                    resumeLoading = false;
                    if (changed || wasLoading) StateHasChanged();   // re-render on a real change, or to clear the spinner
                }),
                ex => InvokeAsync(() =>
                {
                    if (_isDisposed) return;
                    Logger.LogDebug(ex, "[ThreadChat:{InstanceId}] resume thread query failed", _instanceId);
                    resumeThreads = [];
                    resumeLoading = false;
                    StateHasChanged();
                }));

        StateHasChanged();
        return Task.CompletedTask;
    }

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

    // ─── In-thread navigation menu: data + actions ───

    /// <summary>
    /// Live subscription to MY open threads across EVERY partition — the "other open threads" the nav
    /// menu lists and Option-Tab cycles. Factored as its own query (the user's request): the synced
    /// <c>Hub.GetQuery</c> surface, server-filtered to non-Done threads, then client-filtered to the
    /// ones I created. RLS already scopes the snapshot to what I can read. Independent of the current
    /// thread, so it's set up once in <see cref="OnInitialized"/>.
    /// </summary>
    // Visible-shape dedup keys for the thread-list subscriptions (Path|Name sequence). Hub.GetQuery
    // re-emits the WHOLE set on ANY thread content change, and a streaming round churns a thread's content
    // many times/second — the nav menu only shows path+name, so a content-only churn must NOT re-render
    // (that per-token StateHasChanged was a storm cascading down to the header area). Only re-render when
    // the visible sequence actually changes.
    private string? _myThreadsKey, _childThreadsKey, _resumeThreadsKey;

    private static bool VisibleThreadListChanged(ref string? lastKey, IReadOnlyList<MeshNode> projected)
    {
        var key = string.Join(";", projected.Select(n => n.Path + "|" + n.Name));
        if (key == lastKey) return false;
        lastKey = key;
        return true;
    }

    private void SetupMyThreadsSubscription()
    {
        myThreadsSubscription?.Dispose();
        var me = _userHome;
        myThreadsSubscription = Hub.GetQuery("my-threads", "nodeType:Thread -content.status:Done sort:LastModified-desc")
            .Subscribe(
                snapshot => InvokeAsync(() =>
                {
                    if (_isDisposed) return;
                    var projected = snapshot
                        .Where(n => !string.IsNullOrEmpty(n.Path)
                            && string.Equals(n.NodeType, ThreadNodeType.NodeType, StringComparison.OrdinalIgnoreCase)
                            && (string.IsNullOrEmpty(me)
                                || string.Equals(n.CreatedBy, me, StringComparison.OrdinalIgnoreCase)))
                        .OrderByDescending(n => n.LastModified)
                        .ToList();
                    if (!VisibleThreadListChanged(ref _myThreadsKey, projected)) return;
                    myThreads = projected;
                    StateHasChanged();
                }),
                ex => Logger.LogDebug(ex, "[ThreadChat:{InstanceId}] my-threads query failed", _instanceId));
    }

    /// <summary>
    /// Live subscription to THIS thread's sub-threads (delegations) — the "children" in the hierarchy.
    /// Delegations nest at <c>{threadPath}/{responseMsgId}/{subThreadId}</c>, so we query descendants,
    /// not immediate children. Re-opened whenever the thread path changes.
    /// </summary>
    private string? _childThreadsPath;
    private void SetupChildThreadsSubscription(string path)
    {
        if (string.Equals(_childThreadsPath, path, StringComparison.OrdinalIgnoreCase))
            return;                                       // already subscribed for this thread
        _childThreadsPath = path;
        childThreadsSubscription?.Dispose();
        childThreads = [];
        if (string.IsNullOrEmpty(path))
            return;
        childThreadsSubscription = Hub.GetQuery($"child-threads|{path}",
                $"namespace:{path} scope:descendants nodeType:Thread sort:LastModified-desc")
            .Subscribe(
                snapshot => InvokeAsync(() =>
                {
                    if (_isDisposed) return;
                    var projected = snapshot
                        .Where(n => !string.IsNullOrEmpty(n.Path)
                            && !string.Equals(n.Path, path, StringComparison.OrdinalIgnoreCase)
                            && string.Equals(n.NodeType, ThreadNodeType.NodeType, StringComparison.OrdinalIgnoreCase))
                        .OrderByDescending(n => n.LastModified)
                        .ToList();
                    if (!VisibleThreadListChanged(ref _childThreadsKey, projected)) return;
                    childThreads = projected;
                    StateHasChanged();
                }),
                ex => Logger.LogDebug(ex, "[ThreadChat:{InstanceId}] child-threads query failed", _instanceId));
    }

    /// <summary>
    /// This thread's ancestors (root → … → immediate parent), derived purely from the path. A sub-thread
    /// path is <c>{parentThread}/{8-hex responseMsgId}/{subThreadId}</c>; walking up two segments at a
    /// time yields each ancestor thread. Empty for a top-level thread.
    /// </summary>
    private IReadOnlyList<(string Path, string Label)> ThreadAncestors()
    {
        var result = new List<(string, string)>();
        var path = threadPath;
        while (!string.IsNullOrEmpty(path))
        {
            var segs = path.Split('/');
            if (segs.Length < 3) break;
            var parentMsgId = segs[^2];
            if (parentMsgId.Length != 8) break;          // not a delegation boundary → stop
            var parent = string.Join('/', segs[..^2]);
            if (string.IsNullOrEmpty(parent)) break;
            result.Insert(0, (parent, LastSegment(parent) ?? parent));
            path = parent;
        }
        return result;
    }

    /// <summary>My other open threads (all partitions) — <see cref="myThreads"/> minus the current one.</summary>
    private IReadOnlyList<MeshNode> OtherOpenThreads() =>
        myThreads.Where(n => !string.Equals(n.Path, threadPath, StringComparison.OrdinalIgnoreCase)).ToList();

    /// <summary>True when the in-thread navigation menu should render — the full-page thread view with
    /// a real thread (never the side panel / new-chat composer).</summary>
    private bool ShowThreadNav => ViewModel.ShowFullHeader && !string.IsNullOrEmpty(threadPath);

    /// <summary>Cycles the nav-menu layout: TopBar → LeftRail → Hidden → TopBar.</summary>
    private void CycleNavLayout()
    {
        _navLayout = _navLayout switch
        {
            ThreadNavLayout.TopBar => ThreadNavLayout.LeftRail,
            ThreadNavLayout.LeftRail => ThreadNavLayout.Hidden,
            _ => ThreadNavLayout.TopBar
        };
        StateHasChanged();
    }

    /// <summary>Opens a thread in the MAIN view (full-page navigation).</summary>
    private void NavigateToThread(string? path)
    {
        if (string.IsNullOrEmpty(path)) return;
        NavigationManager.NavigateTo($"/{path}");
    }

    /// <summary>
    /// Option-Tab handler: cycles to the NEXT of my other open threads and opens it. Wraps around;
    /// no-op when there are none. Bound on the thread container's keydown (Alt+Tab / Option-Tab).
    /// </summary>
    private void CycleToNextOpenThread()
    {
        var others = OtherOpenThreads();
        if (others.Count == 0) return;
        // Anchor on the current thread's position in the FULL list so repeated Option-Tab walks forward.
        var all = myThreads.ToList();
        var idx = all.FindIndex(n => string.Equals(n.Path, threadPath, StringComparison.OrdinalIgnoreCase));
        var next = all.Count > 0 ? all[(idx + 1 + all.Count) % all.Count] : others[0];
        if (string.Equals(next.Path, threadPath, StringComparison.OrdinalIgnoreCase))
            next = others[0];
        NavigateToThread(next.Path);
    }

    /// <summary>Keydown on the thread shell — Option/Alt+Tab cycles to the next open thread.</summary>
    private void OnThreadShellKeyDown(Microsoft.AspNetCore.Components.Web.KeyboardEventArgs e)
    {
        if (e.AltKey && string.Equals(e.Key, "Tab", StringComparison.OrdinalIgnoreCase))
            CycleToNextOpenThread();
    }

    // ─── New thread from a cell ───

    /// <summary>
    /// "New thread from this cell": opens the side-panel composer PREFILLED with the cell's text — the
    /// thread is created only when the user submits. The CURRENT thread stays open in the main view. The
    /// draft rides through <see cref="SidePanelState"/>; the side-panel new-chat composer consumes it.
    /// </summary>
    private void NewThreadFromCell(string msgId)
    {
        var text = GetMessageState(msgId)?.Text;
        if (string.IsNullOrWhiteSpace(text)) return;
        SidePanelState.OpenNewThreadWithDraft(text);
    }

    private bool _seedMonacoOnRender;

    /// <summary>
    /// Seeds the composer (Monaco) from a one-shot pending draft on <see cref="SidePanelState"/>, if any —
    /// the "new thread from cell" hand-off. ONE-SHOT (the draft is cleared on read) and only in NEW-CHAT
    /// mode (no thread) with an empty editor, so it never clobbers what the user is typing.
    /// </summary>
    private void SeedPendingDraftIfAny()
    {
        if (!string.IsNullOrEmpty(threadPath) || !string.IsNullOrEmpty(MessageText))
            return;
        var draft = SidePanelState.ConsumePendingComposerDraft();
        if (string.IsNullOrEmpty(draft)) return;
        MessageText = draft;
        _seedMonacoOnRender = true;   // push into Monaco after render (it may not be mounted yet)
        StateHasChanged();
    }

    // Thread creation is handled server-side via CreateNodeRequest.

    private CompletionProviderConfig GetCompletionConfig()
    {
        return new CompletionProviderConfig
        {
            // "@" → node/agent references; "/" → slash-commands (handled by GetCommandCompletions).
            TriggerCharacters = ["@", "/"],
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
        // Slash-commands: route straight to the command catalog (nodeType:Command + the registry),
        // bypassing the @-oriented node-search orchestrator so a "/" query lists ONLY commands.
        // RunUnderCircuitUser: this observable is subscribed from the GetAsyncCompletions JS-interop
        // callback and fans out on the mesh-query scheduler — re-establish the user so the command /
        // node list is RLS-correct (not context-null → bypass/deny).
        if (query?.StartsWith("/") == true)
            return RunUnderCircuitUser(GetCommandCompletions(query));

        if (string.IsNullOrWhiteSpace(query) || !query.StartsWith("@"))
            return Observable.Return<IReadOnlyList<CompletionItem>>(Array.Empty<CompletionItem>());

        // 🎯 Autocomplete context = the SPACE root, never a satellite. When viewing a thread at
        // {space}/_Thread/abc, NavigationService.CurrentNamespace resolves to that FULL thread path;
        // passed verbatim it points the orchestrator's Source A/B at the thread hub → a 2s per-source
        // timeout and no space-level suggestions. NormalizeContextPath strips the satellite suffix
        // (_Thread/_Activity/_Access/…) so the query targets the owning space.
        var currentAddress = NormalizeContextPath(NavigationService.CurrentNamespace ?? initialContext ?? "");

        return Observable.Defer(() =>
        {
            SetCompletionsInflight(true);
            var lastSnapshot = (IReadOnlyList<CompletionItem>)Array.Empty<CompletionItem>();
            return RunUnderCircuitUser(CompletionOrchestrator.GetCompletions(query, currentAddress)
                .SelectMany(batch => batch.Items
                    .Select(item => AutocompleteToCompletion(item, batch.Category, batch.CategoryPriority)))
                .ScanTopN(CompletionTopN, CompletionBySortKey)
                .DistinctUntilChanged(SnapshotKey)
                .Do(snapshot => lastSnapshot = snapshot)
                // When the search finishes with nothing, surface a single non-interactive "No results"
                // row rather than an empty list (Monaco hides an empty suggest widget). This confirms to
                // the user that the @ trigger DID fire and the search ran — distinguishing it from a
                // suppressed trigger. Emitted only at completion, so real results never flash a placeholder.
                .Concat(Observable.Defer(() => lastSnapshot.Count == 0
                    ? Observable.Return(NoResultsPlaceholder(query))
                    : Observable.Empty<IReadOnlyList<CompletionItem>>()))
                .Catch<IReadOnlyList<CompletionItem>, Exception>(ex =>
                {
                    Logger.LogError(ex, "Error streaming completions for query: {Query}", query);
                    return Observable.Return<IReadOnlyList<CompletionItem>>(Array.Empty<CompletionItem>());
                })
                .Finally(() => SetCompletionsInflight(false)));
        });
    }

    /// <summary>
    /// A single non-interactive placeholder row shown when an @-query returns no matches.
    /// <c>filterText</c> resolves to the query (MonacoEditorView.razor.js sets filterText ← insertText),
    /// so Monaco does not fuzzy-filter it away; <c>InsertText</c> = the query makes accidental acceptance
    /// a no-op (the typed text is left untouched), and the empty <c>Path</c> makes
    /// <see cref="OnCompletionItemAccepted"/> return early without adding a chip.
    /// </summary>
    private static IReadOnlyList<CompletionItem> NoResultsPlaceholder(string query) =>
        new[]
        {
            new CompletionItem
            {
                Label = $"No results for '{query}'",
                InsertText = query,
                Path = string.Empty,
                Kind = CompletionItemKind.Text,
                Category = "No results",
                SortKey = "9999_noresults",
            }
        };

    /// <summary>
    /// Slash-skill completions: lists <c>nodeType:Skill</c> nodes (built-ins imported to PG plus any
    /// Space/NodeType/user skill via namespace inheritance), straight from
    /// <see cref="MeshWeaver.AI.Completion.SkillAutocompleteProvider"/> — NOT through the @-oriented
    /// node-search orchestrator. Monaco filters the list by the typed "/word".
    /// </summary>
    private IObservable<IReadOnlyList<CompletionItem>> GetCommandCompletions(string query)
    {
        // When a non-MeshWeaver harness is active, IT is the authority for the slash-command list:
        // show its own commands (/login, /logout) plus /harness (to switch back), NOT MeshWeaver's
        // /agent /model. Monaco filters the list by the typed "/word".
        var harness = ActiveHarness();
        if (harness is not null)
            return Observable.Return(BuildHarnessCommandCompletions(harness));

        // Construct the provider directly against the chat hub's service provider — it self-resolves
        // its deps (IWorkspace + IMessageHub for the nodeType:Skill catalog). Resolving via
        // GetServices<IAutocompleteProvider>() does NOT work here: the provider is only registered in
        // the Agents-application hub's container (ConfigureAgentsApplication), never on the chat hub —
        // so the enumerable lookup returned null and typing "/" showed nothing.
        var provider = new MeshWeaver.AI.Completion.SkillAutocompleteProvider(Hub.ServiceProvider);

        return provider.GetItems(query, initialContext)
            .Select(items => (IReadOnlyList<CompletionItem>)items
                .Select(i => AutocompleteToCompletion(i, "Commands", 2000))
                .OrderBy(c => c.SortKey, StringComparer.Ordinal)
                .ToList())
            .Catch<IReadOnlyList<CompletionItem>, Exception>(ex =>
            {
                Logger.LogDebug(ex, "[ThreadChat:{InstanceId}] command completions failed", _instanceId);
                return Observable.Return<IReadOnlyList<CompletionItem>>(Array.Empty<CompletionItem>());
            });
    }

    /// <summary>
    /// Builds the slash-command completion list for an active non-MeshWeaver harness: the harness's
    /// OWN commands (the harness is the autocomplete authority) plus <c>/harness</c> so the user can
    /// still switch back. No mesh query — the list is the harness's declared <see cref="MeshWeaver.AI.IHarness.Commands"/>.
    /// </summary>
    private IReadOnlyList<CompletionItem> BuildHarnessCommandCompletions(MeshWeaver.AI.IHarness harness)
    {
        CompletionItem Item(string name, string description) =>
            AutocompleteToCompletion(
                new Data.Completion.AutocompleteItem(
                    Label: $"/{name}", InsertText: $"/{name} ", Description: description,
                    Category: "Commands", Priority: 2000, Kind: Data.Completion.AutocompleteKind.Command),
                "Commands", 2000);

        var items = harness.Commands.Select(c => Item(c.Name, c.Description)).ToList();
        // /harness stays available (it falls through to the node-pick path) so a CLI harness isn't a
        // one-way door — the user can switch runtime back to MeshWeaver or another harness.
        items.Add(Item("harness", "Switch the harness (runtime)"));

        // ALL of the harness's OWN slash-commands AND skills, probed from the CLI's init/hello message
        // (IHarnessRuntimeInfo → harnessRuntimes). The user gets Claude Code / Copilot's native command +
        // skill autocomplete; each is forwarded 1:1 to the CLI by the FORWARD path in the dispatch.
        var label = harness.Definition.DisplayName ?? harness.Id;
        var seen = new HashSet<string>(items.Select(i => (i.Label ?? "").TrimStart('/')), StringComparer.OrdinalIgnoreCase);
        if (harnessRuntimes.GetValueOrDefault(harness.Id) is { } rt)
        {
            foreach (var cmd in rt.SlashCommands)
            {
                var name = cmd.TrimStart('/');
                if (name.Length > 0 && seen.Add(name))
                    items.Add(Item(name, $"{label} command"));
            }
            foreach (var skill in rt.Skills)
            {
                var name = skill.TrimStart('/');
                if (name.Length > 0 && seen.Add(name))
                    items.Add(Item(name, $"{label} skill"));
            }
        }
        return items.OrderBy(c => c.SortKey, StringComparer.Ordinal).ToList();
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
    private ThreadViewModel? ConvertThreadViewModel(object? value, ThreadViewModel? previous)
    {
        var result = value switch
        {
            null => null,
            JsonElement je => je.Deserialize<ThreadViewModel?>(Hub.JsonSerializerOptions),
            AI.ThreadViewModel m => m,
            _ => throw new ArgumentException($"Cannot convert type {value.GetType().Name}.")
        };
        // 🎯 BEST-EVER keep-last-good. The data-bound value transiently arrives null (a JsonElement that
        // deserialises to null) or as an empty/default ThreadViewModel — the cross-hub node stream
        // momentarily reduces to an empty node. Binding that empties the conversation → HasNoMessages → the
        // composer blanks mid-chat: the intermittent round-N "chat vanishes" (footer-gate createdBy=<null>
        // noMsgs=True). A LIVE thread never loses its content, so remember the last viewmodel that HAD
        // content and fall back to it on any transient empty/null. Unlike a 'previous'-value guard this
        // CANNOT be poisoned — the field is ONLY ever assigned a content-bearing value, so one empty slipping
        // through never defeats it. "Content" counts BOTH committed Messages AND PendingMessageTexts (the
        // just-sent optimistic user message): a flap that coincides with a fresh submission has Messages=0
        // but Pending=[text], and masking that would HIDE the new user bubble (the "saw N-1" failure). A new
        // thread re-creates this component (field resets); we also guard on ThreadPath against a thread switch.
        static bool HasContent(ThreadViewModel? vm)
            => vm is not null && (vm.Messages.Count > 0 || vm.PendingMessageTexts.Count > 0);
        if (HasContent(result))
            _lastNonEmptyThreadVm = result;
        else if (HasContent(_lastNonEmptyThreadVm)
                 && (result is null || string.IsNullOrEmpty(result.ThreadPath)
                     || string.Equals(result.ThreadPath, _lastNonEmptyThreadVm!.ThreadPath, StringComparison.Ordinal)))
            return _lastNonEmptyThreadVm;
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
    // MessageBubbleState moved to its own file (MessageBubbleState.cs) with SEQUENCE-based
    // value equality so the UpdateMessageState dedup actually fires on re-emissions — see the
    // "vanishes when pushing the output message" render-storm fix.

    private readonly Dictionary<string, MessageBubbleState> messageStates = new();
    private readonly Dictionary<string, IDisposable> messageSubs = new();
    private readonly HashSet<string> editingMessages = new();
    /// <summary>Message ids whose tool-calls section is expanded to show the collapsed
    /// (finished / overflow) entries. Per-message UI toggle — same circuit-scoped HashSet
    /// idiom as <see cref="editingMessages"/>.</summary>
    private readonly HashSet<string> expandedToolCalls = new();
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

    private bool IsToolCallsExpanded(string id) => expandedToolCalls.Contains(id);

    private void ToggleToolCalls(string id)
    {
        if (!expandedToolCalls.Remove(id))
            expandedToolCalls.Add(id);
        StateHasChanged();
    }

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
        // Picked node PATHS flow through — execution normalizes to ids at its boundary.
        Hub.ResubmitMessage(threadPath, id, newUserText: state.Text ?? "",
            agentName: boundAgentPath, modelName: boundModelPath, harness: boundHarness);
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
    private LayoutAreaControl? _headerCell;
    private string? _headerCellPath;
    private LayoutAreaControl? GetHeaderCell()
    {
        if (string.IsNullOrEmpty(threadPath))
            return null;
        // Cache by threadPath. Returning a FRESH control each render gave the header area a new ViewModel
        // reference every parent render → it re-bound every time (defeating the OnParametersSet re-bind
        // guard). A stable reference while the thread is unchanged ⇒ the header area binds once.
        if (_headerCell is not null && _headerCellPath == threadPath)
            return _headerCell;
        _headerCellPath = threadPath;
        return _headerCell = new LayoutAreaControl(
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

    /// <summary>
    /// Tears down the component exactly once: marks it disposed, disposes the elapsed-time ticker and
    /// every live subscription (resume, connect, navigation context, composer, agent/model snapshots,
    /// per-message / missing-probe / delegation streams), clears the tracking dictionaries, and unhooks
    /// the side-panel action handler before delegating to the base implementation.
    /// </summary>
    public override async ValueTask DisposeAsync()
    {
        if (!_isDisposed)
        {
            _isDisposed = true;
            elapsedTicker?.Dispose();
            resumeSubscription?.Dispose();
            myThreadsSubscription?.Dispose();
            childThreadsSubscription?.Dispose();
            _navContextSubscription?.Dispose();
            composerSubscription?.Dispose();
            composerDefaultsSubscription?.Dispose();
            foreach (var sub in harnessRuntimeSubs) sub.Dispose();
            harnessRuntimeSubs.Clear();
            agentSubscription?.Dispose();
            modelsSubscription?.Dispose();
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

            // #128: tear down the JS auto-scroll observer, then release the interop references.
            // On a full circuit teardown the JS side is already gone (JSDisconnectedException) —
            // that is expected and needs no cleanup.
            try
            {
                if (_scrollHandle is not null)
                {
                    await _scrollHandle.InvokeVoidAsync("dispose");
                    await _scrollHandle.DisposeAsync();
                }
                if (_scrollModule is not null)
                    await _scrollModule.DisposeAsync();
            }
            catch (JSDisconnectedException) { /* circuit already gone — nothing to tear down */ }
            catch (Exception) { /* best-effort cleanup on teardown */ }
        }

        await base.DisposeAsync();
    }
}
