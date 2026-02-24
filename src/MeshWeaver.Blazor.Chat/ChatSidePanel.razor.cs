using MeshWeaver.Layout;
using MeshWeaver.Mesh.Services;
using Microsoft.AspNetCore.Components;

namespace MeshWeaver.Blazor.Chat;

public partial class ChatSidePanel : ComponentBase
{
    [Inject] private NavigationManager NavigationManager { get; set; } = null!;
    [Inject] private ChatWindowStateService ChatWindowState { get; set; } = null!;
    [Inject] private INavigationService NavigationService { get; set; } = null!;

    [Parameter] public EventCallback OnCloseRequested { get; set; }
    [Parameter] public EventCallback<ChatPosition> OnPositionChanged { get; set; }

    private ChatHistorySelector? chatHistorySelector;
    private bool showChatHistory;
    private bool positionMenuVisible;
    private ChatPosition currentPosition = ChatPosition.Right;
    private string? selectedThreadPath;
    private string threadChatKey = Guid.NewGuid().ToString("N")[..8];

    protected override void OnInitialized()
    {
        base.OnInitialized();
        currentPosition = ChatWindowState.Position;

        // Restore thread from state if available
        selectedThreadPath = ChatWindowState.CurrentThreadPath;
    }

    private ThreadChatControl GetThreadChatControl()
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
            ChatWindowState.SetCurrentThread(threadPath);

            // Force re-render of ThreadChatView with new key
            threadChatKey = Guid.NewGuid().ToString("N")[..8];

            showChatHistory = false;
            StateHasChanged();
        }
    }

    private Task StartNewConversationAsync()
    {
        selectedThreadPath = null;
        ChatWindowState.SetCurrentThread(null);

        // Force re-render of ThreadChatView with new key
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

    private async Task ChangeChatPositionAsync(ChatPosition newPosition)
    {
        positionMenuVisible = false;

        if (currentPosition != newPosition)
        {
            currentPosition = newPosition;
            StateHasChanged();

            if (OnPositionChanged.HasDelegate)
            {
                await OnPositionChanged.InvokeAsync(currentPosition);
            }
        }
        else
        {
            StateHasChanged();
        }
    }

    private async Task CloseChatAsync()
    {
        if (OnCloseRequested.HasDelegate)
        {
            await OnCloseRequested.InvokeAsync();
        }
    }
}
