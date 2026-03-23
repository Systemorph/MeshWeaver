using MeshWeaver.Layout;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Blazor.Portal.SidePanel;
using Microsoft.AspNetCore.Components;

namespace MeshWeaver.Blazor.Portal.Chat;

/// <summary>
/// Thread-specific content for the side panel.
/// Wraps ChatSidePanel and provides both toolbar buttons and chat content.
/// Manages thread selection, chat history, and position.
/// </summary>
public partial class ThreadSidePanelContent : ComponentBase
{
    [Inject] private NavigationManager NavigationManager { get; set; } = null!;
    [Inject] private SidePanelStateService SidePanelState { get; set; } = null!;
    [Inject] private INavigationService NavigationService { get; set; } = null!;

    /// <summary>
    /// Callback invoked when the panel should be closed. Passed through to ChatSidePanel.
    /// </summary>
    [Parameter] public EventCallback OnCloseRequested { get; set; }

    private ChatHistorySelector? chatHistorySelector;
    private bool showChatHistory;
    private bool positionMenuVisible;
    private string? selectedThreadPath;
    private string threadChatKey = Guid.NewGuid().ToString("N")[..8];
    private ThreadChatControl? cachedChatControl;

    protected override void OnInitialized()
    {
        base.OnInitialized();

        // Restore thread from state if available
        selectedThreadPath = SidePanelState.ContentPath;
        cachedChatControl = BuildThreadChatControl();
    }

    private ThreadChatControl GetThreadChatControl()
        => cachedChatControl ??= BuildThreadChatControl();

    private ThreadChatControl BuildThreadChatControl()
    {
        var context = NavigationService.Context;
        var contextPath = context?.PrimaryPath;
        var contextDisplayName = context?.Node?.Name ?? context?.Node?.Id;

        return new ThreadChatControl()
            .WithThreadPath(selectedThreadPath ?? string.Empty)
            .WithInitialContext(contextPath ?? string.Empty)
            .WithInitialContextDisplayName(contextDisplayName ?? string.Empty);
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

    private async Task OnThreadSelected(string threadPath)
    {
        if (threadPath != selectedThreadPath)
        {
            selectedThreadPath = threadPath;
            SidePanelState.SetContentPath(threadPath);

            // Rebuild control and force re-render with new key
            cachedChatControl = BuildThreadChatControl();
            threadChatKey = Guid.NewGuid().ToString("N")[..8];

            showChatHistory = false;
            StateHasChanged();
        }
    }

    private Task StartNewConversationAsync()
    {
        selectedThreadPath = null;
        SidePanelState.SetContentPath(null);

        // Rebuild control and force re-render with new key
        cachedChatControl = BuildThreadChatControl();
        threadChatKey = Guid.NewGuid().ToString("N")[..8];

        showChatHistory = false;
        StateHasChanged();
        return Task.CompletedTask;
    }

    private void OpenThreadFullScreen()
    {
        if (!string.IsNullOrEmpty(selectedThreadPath))
        {
            NavigationManager.NavigateTo($"/{selectedThreadPath}");
        }
    }

    private void ChangeChatPosition(SidePanelPosition newPosition)
    {
        positionMenuVisible = false;
        SidePanelState.SetPosition(newPosition);
        StateHasChanged();
    }
}
