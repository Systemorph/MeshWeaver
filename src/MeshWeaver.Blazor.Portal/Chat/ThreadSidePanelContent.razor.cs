using MeshWeaver.AI;
using MeshWeaver.Data;
using MeshWeaver.Layout;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Blazor.Portal.SidePanel;
using Microsoft.AspNetCore.Components;

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

    [Parameter] public EventCallback OnCloseRequested { get; set; }

    private bool positionMenuVisible;
    private string? selectedThreadPath;
    private string? selectedThreadName;

    private string? lastPrimaryPath;

    protected override void OnInitialized()
    {
        base.OnInitialized();
        selectedThreadPath = SidePanelState.ContentPath;
        lastPrimaryPath = NavigationService.Context?.PrimaryPath;
        SidePanelState.OnStateChanged += OnSidePanelStateChanged;
        // React to navigation changes: when the user browses to a thread or another node,
        // the side panel's new-chat context (MainNode / PrimaryPath) changes and the
        // ThreadChatControl must be rebuilt with the new context attachment.
        NavigationService.OnNavigationContextChanged += OnNavigationContextChanged;
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
        // Only rebuild when showing the new-chat control (no selected thread).
        // An in-flight thread keeps its own context.
        if (string.IsNullOrEmpty(selectedThreadPath))
            InvokeAsync(StateHasChanged);
    }

    public void Dispose()
    {
        SidePanelState.OnStateChanged -= OnSidePanelStateChanged;
        NavigationService.OnNavigationContextChanged -= OnNavigationContextChanged;
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
        var context = NavigationService.Context;
        return new ThreadChatControl()
            .WithInitialContext(context?.PrimaryPath ?? string.Empty)
            .WithInitialContextDisplayName(context?.Node?.Name ?? context?.Node?.Id ?? string.Empty);
    }

    private string SidePanelTitle => selectedThreadName ?? "New Chat";

    private void OnNewChat()
    {
        selectedThreadPath = null;
        selectedThreadName = null;
        SidePanelState.SetContentPath(null);
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
