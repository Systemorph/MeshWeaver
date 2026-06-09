using Microsoft.AspNetCore.Components;

namespace MeshWeaver.Blazor.Portal.SidePanel;

/// <summary>
/// Generic side panel container with a header (title + toolbar + close button)
/// and a content area. Content-specific logic lives in child components.
/// </summary>
public partial class SidePanel : ComponentBase, IDisposable
{
    [Inject] private SidePanelStateService SidePanelState { get; set; } = null!;
    [Inject] private NavigationManager NavigationManager { get; set; } = null!;

    /// <summary>
    /// Main content rendered inside the panel body.
    /// </summary>
    [Parameter] public RenderFragment? ChildContent { get; set; }

    /// <summary>
    /// Additional toolbar buttons rendered in the header, before the close button.
    /// </summary>
    [Parameter] public RenderFragment? ToolbarContent { get; set; }

    /// <summary>
    /// Callback invoked when the close button is clicked.
    /// </summary>
    [Parameter] public EventCallback OnCloseRequested { get; set; }

    private string DisplayTitle => SidePanelState.Title ?? "New Thread";
    private bool HasThread => !string.IsNullOrEmpty(SidePanelState.ContentPath);

    protected override void OnInitialized()
    {
        base.OnInitialized();
        SidePanelState.OnStateChanged += OnStateChanged;
    }

    private void OnStateChanged()
    {
        InvokeAsync(StateHasChanged);
    }

    private void OnNewThread()
    {
        // Clear the active thread so the new-chat composer renders. This must happen
        // here (the always-mounted panel), not only via RequestAction: when a thread
        // is displayed the panel body is a LayoutAreaView, so no ThreadChatView is
        // subscribed to OnActionRequested and the click would otherwise do nothing
        // ("clicking + keeps me on the thread").
        SidePanelState.SetContentPath(null);
        // Notify a mounted composer to reset its view mode (e.g. Resume → Chat).
        SidePanelState.RequestAction("New");
    }

    private void OnResumeThread()
    {
        SidePanelState.RequestAction("Resume");
    }

    private void MoveToMainPanel()
    {
        var contentPath = SidePanelState.ContentPath;
        SidePanelState.SetContentPath(null);
        SidePanelState.SetTitle(null);
        SidePanelState.SetVisible(false);
        if (!string.IsNullOrEmpty(contentPath))
            NavigationManager.NavigateTo($"/{contentPath}");
    }

    private async Task CloseAsync()
    {
        if (OnCloseRequested.HasDelegate)
        {
            await OnCloseRequested.InvokeAsync();
        }
    }

    public void Dispose()
    {
        SidePanelState.OnStateChanged -= OnStateChanged;
    }
}
