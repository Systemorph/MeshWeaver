using System.Reactive.Linq;
using MeshWeaver.AI;
using MeshWeaver.Data;
using MeshWeaver.Layout;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using MeshWeaver.Blazor.Portal.SidePanel;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Blazor.Portal.Chat;

/// <summary>
/// Side panel for threads. For existing threads, renders a LayoutAreaView
/// pointing to the thread hub's Thread layout area — identical to main panel.
/// For new chats (no thread yet), renders ThreadChatControl directly.
/// Switching threads = changing the LayoutAreaView key → full garbage collection + re-render.
/// </summary>
public partial class ThreadSidePanelContent : ComponentBase, IDisposable
{
    [Inject] private NavigationManager NavigationManager { get; set; } = null!;
    [Inject] private SidePanelStateService SidePanelState { get; set; } = null!;
    [Inject] private INavigationService NavigationService { get; set; } = null!;
    [Inject] private AccessService AccessService { get; set; } = null!;
    [Inject] private IMeshNodeStreamCache StreamCache { get; set; } = null!;
    [Inject] private IMeshService MeshQuery { get; set; } = null!;
    [Inject] private IMessageHub Hub { get; set; } = null!;

    /// <summary>
    /// Raised when the user dismisses the panel (e.g. the close button), letting the host
    /// collapse or hide the thread side panel.
    /// </summary>
    [Parameter] public EventCallback OnCloseRequested { get; set; }

    private bool positionMenuVisible;
    private string? selectedThreadPath;
    private string? selectedThreadName;

    private string? lastPrimaryPath;
    private IDisposable? _navContextSubscription;
    private NavigationContext? _currentNavContext;
    private IDisposable? _composerObserver;
    private string? _observedComposerPath;
    private ILogger<ThreadSidePanelContent>? _logger;

    /// <summary>
    /// Component initialization: resolves the logger, seeds the selected thread from
    /// <c>SidePanelState</c>, subscribes to side-panel state changes and the navigation-context
    /// stream, and ensures the per-user composer observer is running.
    /// </summary>
    protected override void OnInitialized()
    {
        base.OnInitialized();
        _logger = Hub.ServiceProvider.GetService<ILogger<ThreadSidePanelContent>>();
        selectedThreadPath = SidePanelState.ContentPath;
        SidePanelState.OnStateChanged += OnSidePanelStateChanged;
        // Subscribe to navigation-context stream — emits current value on subscribe
        // (ReplaySubject(1)) so we don't need a separate snapshot read.
        _navContextSubscription = NavigationService.NavigationContext
            .Subscribe(ctx => { _currentNavContext = ctx; OnNavigationContextChanged(ctx); });
        EnsureComposerObserver();
    }

    private void OnSidePanelStateChanged()
    {
        var newPath = SidePanelState.ContentPath;
        if (newPath != selectedThreadPath)
        {
            selectedThreadPath = newPath;
            InvokeAsync(StateHasChanged);
        }
    }

    private void OnNavigationContextChanged(NavigationContext? ctx)
    {
        var newPrimary = ctx?.PrimaryPath;
        if (newPrimary == lastPrimaryPath) return;
        lastPrimaryPath = newPrimary;
        EnsureComposerObserver();
        // Only rebuild when showing the new-chat control (no selected thread).
        // An in-flight thread keeps its own context. The composer node carries the
        // context so Send (a server-side click with no circuit access) creates the
        // thread under {MainNodeOf(context)}/_Thread/… — write it on every change.
        if (string.IsNullOrEmpty(selectedThreadPath))
        {
            WriteComposerContext(newPrimary);
            InvokeAsync(StateHasChanged);
        }
    }

    /// <summary>
    /// Persists the current navigation context onto the per-user composer node
    /// (<see cref="ThreadComposer.ContextPath"/>), normalized to the main node (satellite
    /// segments like <c>_Thread</c>/<c>_Comment</c> stripped). Idempotent — skips the write
    /// when unchanged; unreadable content is left alone, never clobbered.
    /// </summary>
    private void WriteComposerContext(string? primaryPath)
    {
        var userHome = ResolveUserHome();
        if (string.IsNullOrEmpty(userHome)) return;
        var contextPath = NormalizeContextPath(primaryPath);
        StreamCache.Update(ThreadComposerNodeType.PathFor(userHome), n =>
        {
            var c = n.ContentAs<ThreadComposer>(Hub.JsonSerializerOptions, _logger);
            if (n.Content is not null && c is null) return n;
            c ??= new ThreadComposer();
            return c.ContextPath == contextPath ? n : n with { Content = c with { ContextPath = contextPath } };
        }, Hub.JsonSerializerOptions).Subscribe(
            _ => { },
            ex => _logger?.LogDebug(ex, "[ThreadSidePanel] composer context write failed"));
    }

    /// <summary>
    /// The main-node form of a navigation path: everything before the first satellite
    /// (<c>_</c>-prefixed) segment — e.g. <c>acme/X/_Thread/y</c> → <c>acme/X</c>.
    /// </summary>
    private static string? NormalizeContextPath(string? path)
    {
        if (string.IsNullOrEmpty(path)) return null;
        var segments = path.Split('/');
        for (var i = 0; i < segments.Length; i++)
            if (segments[i].StartsWith('_'))
                return i == 0 ? null : string.Join('/', segments, 0, i);
        return path;
    }

    /// <summary>
    /// Observes the per-user composer node for <see cref="ThreadComposer.OpenThreadPath"/> —
    /// the data-bound navigation signal the Send click stamps (the click runs on the composer
    /// node's server hub and cannot reach circuit services). On signal: show the thread in the
    /// panel and clear the field. The node is ensured present first (CreateNode is a no-op
    /// "already exists" for the onboarding-seeded singleton) so the stream read never points
    /// at a maybe-absent node.
    /// </summary>
    private void EnsureComposerObserver()
    {
        var userHome = ResolveUserHome();
        if (string.IsNullOrEmpty(userHome)) return;
        var path = ThreadComposerNodeType.PathFor(userHome);
        if (_observedComposerPath == path) return;
        _observedComposerPath = path;

        MeshQuery.CreateNode(MeshNode.FromPath(path) with
            {
                NodeType = ThreadComposerNodeType.NodeType,
                Name = "Chat Input",
                Content = new ThreadComposer()
            })
            .Subscribe(
                _ => InvokeAsync(() => OpenComposerObserver(path)),
                _ => InvokeAsync(() => OpenComposerObserver(path)));
    }

    private void OpenComposerObserver(string path)
    {
        if (_observedComposerPath != path) return;
        _composerObserver?.Dispose();
        _composerObserver = Hub.GetMeshNodeStream(path)
            .Select(n => ThreadComposerNodeType.ComposerOf(n, Hub.JsonSerializerOptions, _logger)?.OpenThreadPath)
            .Where(p => !string.IsNullOrEmpty(p))
            .DistinctUntilChanged()
            .Subscribe(
                threadPath => InvokeAsync(() =>
                {
                    SidePanelState.SetContentPath(threadPath);
                    selectedThreadPath = threadPath;
                    StateHasChanged();
                    // Consume the signal so it doesn't re-fire on the next subscribe.
                    StreamCache.Update(path, n =>
                    {
                        var c = n.ContentAs<ThreadComposer>(Hub.JsonSerializerOptions, _logger);
                        return c?.OpenThreadPath is null ? n : n with { Content = c with { OpenThreadPath = null } };
                    }, Hub.JsonSerializerOptions).Subscribe(_ => { }, _ => { });
                }),
                ex => _logger?.LogDebug(ex, "[ThreadSidePanel] composer observer errored for {Path}", path));
    }

    /// <summary>
    /// Unhooks the side-panel state handler and disposes the navigation-context and
    /// composer-observer subscriptions.
    /// </summary>
    public void Dispose()
    {
        SidePanelState.OnStateChanged -= OnSidePanelStateChanged;
        _navContextSubscription?.Dispose();
        _composerObserver?.Dispose();
    }

    /// <summary>
    /// LayoutAreaControl pointing to the thread hub's ThreadChat area (no header).
    /// LayoutAreaView handles stream, data binding, and cleanup automatically.
    /// </summary>
    private LayoutAreaControl GetThreadLayoutArea()
        => new LayoutAreaControl(selectedThreadPath!, new LayoutAreaReference(ThreadNodeType.ThreadChatArea));

    /// <summary>
    /// For new chats when no thread exists yet.
    /// </summary>
    private ThreadChatControl GetNewChatControl()
    {
        var context = _currentNavContext;
        return new ThreadChatControl()
            .WithInitialContext(context?.PrimaryPath ?? string.Empty)
            // Label the OWNER, never the navigated satellite (a thread "hi"): ContextChipLabel returns
            // null for a satellite so the chip falls back to the main-node path's last segment.
            .WithInitialContextDisplayName(
                MeshWeaver.AI.NavigationContextProjection.ContextChipLabel(context) ?? string.Empty);
    }

    /// <summary>
    /// Out-of-thread composer: the per-user ThreadComposer node's default ("") layout area —
    /// the databound compose box (message text + harness/agent/model + attachments),
    /// backed by <c>{userHome}/_Thread/ThreadComposer</c> (see <see cref="ThreadComposerView"/>).
    /// Null until the user identity resolves, in which case the caller degrades to the
    /// direct <see cref="GetNewChatControl"/> ThreadChatControl.
    /// </summary>
    private LayoutAreaControl? GetThreadComposerLayoutArea()
    {
        var userHome = ResolveUserHome();
        return string.IsNullOrEmpty(userHome)
            ? null
            : new LayoutAreaControl(ThreadComposerNodeType.PathFor(userHome), new LayoutAreaReference(string.Empty));
    }

    /// <summary>
    /// The signed-in user's partition — the main node that owns
    /// <c>{user}/_Thread/ThreadComposer</c>. Prefer <see cref="AccessService.CircuitContext"/>
    /// (the durable per-circuit identity); <see cref="AccessService.Context"/>
    /// (AsyncLocal) is only a fallback and is filtered for a leaked
    /// <c>system-security</c> / hub principal. Trusting <c>Context</c> first pointed
    /// the composer at a non-existent <c>system-security/_Thread/ThreadComposer</c>, so the
    /// "+" new-chat rendered nothing — the "+ not working" bug.
    /// </summary>
    private string? ResolveUserHome()
    {
        foreach (var candidate in new[] { AccessService.CircuitContext?.ObjectId, AccessService.Context?.ObjectId })
        {
            if (!string.IsNullOrEmpty(candidate)
                && candidate != WellKnownUsers.System
                && !AccessService.LooksLikeHubPrincipal(candidate))
                return candidate;
        }
        return null;
    }

    private string SidePanelTitle => selectedThreadName ?? "New Chat";

    private void OnNewChat()
    {
        selectedThreadPath = null;
        selectedThreadName = null;
        SidePanelState.SetContentPath(null);

        // Fresh empty chat: clear the in-progress draft (message text + attachments)
        // on the per-user ThreadComposer node but KEEP the harness/agent/model selection —
        // "copy the thread input from _Memex except content". No thread is created
        // here; the thread is created on submit. The composer rebinds to the cleared
        // content via its live stream.
        var userHome = ResolveUserHome();
        if (!string.IsNullOrEmpty(userHome))
        {
            StreamCache.Update(ThreadComposerNodeType.PathFor(userHome), n =>
            {
                // ContentAs: tolerate a degraded JsonElement; unreadable → leave alone.
                var ci = n.ContentAs<ThreadComposer>(Hub.JsonSerializerOptions, _logger);
                if (ci is null) return n;
                if (string.IsNullOrEmpty(ci.MessageContent)
                    && (ci.Attachments is null || ci.Attachments.Count == 0)
                    && ci.OpenThreadPath is null)
                    return n;
                return n with { Content = ci with { MessageContent = null, Attachments = null, OpenThreadPath = null } };
            }, Hub.JsonSerializerOptions).Subscribe(_ => { }, _ => { });
        }

        StateHasChanged();
    }

    private void OpenThreadFullScreen()
    {
        if (!string.IsNullOrEmpty(selectedThreadPath))
            NavigationManager.NavigateTo($"/{selectedThreadPath}");
    }

    private void ChangeChatPosition(SidePanelPosition newPosition)
    {
        positionMenuVisible = false;
        SidePanelState.SetPosition(newPosition);
        StateHasChanged();
    }
}
