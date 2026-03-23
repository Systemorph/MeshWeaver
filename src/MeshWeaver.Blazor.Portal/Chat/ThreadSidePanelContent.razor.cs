using MeshWeaver.AI;
using MeshWeaver.Data;
using MeshWeaver.Layout;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Blazor.Portal.SidePanel;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

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
    [Inject] private IMeshService MeshQuery { get; set; } = null!;

    [Parameter] public EventCallback OnCloseRequested { get; set; }

    private bool showThreadList;
    private bool positionMenuVisible;
    private string? selectedThreadPath;

    // Thread list
    private List<MeshNode> threadList = [];
    private bool isLoadingThreads;

    protected override void OnInitialized()
    {
        base.OnInitialized();
        selectedThreadPath = SidePanelState.ContentPath;
        SidePanelState.OnStateChanged += OnSidePanelStateChanged;
    }

    private void OnSidePanelStateChanged()
    {
        var newPath = SidePanelState.ContentPath;
        if (newPath != selectedThreadPath)
        {
            selectedThreadPath = newPath;
            showThreadList = false;
            InvokeAsync(StateHasChanged);
        }
    }

    public void Dispose()
    {
        SidePanelState.OnStateChanged -= OnSidePanelStateChanged;
    }

    /// <summary>
    /// LayoutAreaControl pointing to the thread hub's Thread area.
    /// LayoutAreaView handles stream, data binding, and cleanup automatically.
    /// </summary>
    private LayoutAreaControl GetThreadLayoutArea()
        => new LayoutAreaControl(selectedThreadPath!, new LayoutAreaReference(ThreadNodeType.ThreadArea));

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

    private void SelectThread(MeshNode thread)
    {
        selectedThreadPath = thread.Path;
        SidePanelState.SetContentPath(thread.Path);
        showThreadList = false;
        StateHasChanged();
    }

    private void OnNewChat()
    {
        selectedThreadPath = null;
        SidePanelState.SetContentPath(null);
        showThreadList = false;
        StateHasChanged();
    }

    private async Task ToggleThreadList()
    {
        showThreadList = !showThreadList;
        if (showThreadList)
            await LoadThreadListAsync();
        StateHasChanged();
    }

    private async Task LoadThreadListAsync()
    {
        isLoadingThreads = true;
        StateHasChanged();

        try
        {
            var ns = NavigationService.CurrentNamespace;
            var query = string.IsNullOrEmpty(ns)
                ? "nodeType:Thread limit:20 sort:LastModified-desc"
                : $"nodeType:Thread namespace:{ns}/_Thread limit:20 sort:LastModified-desc";
            threadList = await MeshQuery.QueryAsync<MeshNode>(query).ToListAsync();
        }
        catch
        {
            threadList = [];
        }
        finally
        {
            isLoadingThreads = false;
            StateHasChanged();
        }
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
