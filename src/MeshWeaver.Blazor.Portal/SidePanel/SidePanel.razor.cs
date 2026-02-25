using Microsoft.AspNetCore.Components;

namespace MeshWeaver.Blazor.Portal.SidePanel;

/// <summary>
/// Generic side panel container with a header (title + toolbar + close button)
/// and a content area. Content-specific logic lives in child components.
/// </summary>
public partial class SidePanel : ComponentBase, IDisposable
{
    [Inject] private SidePanelStateService SidePanelState { get; set; } = null!;

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

    private bool isMenuOpen;

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

    private void ToggleMenu()
    {
        isMenuOpen = !isMenuOpen;
    }

    private void OnMenuOpenChanged(bool open)
    {
        isMenuOpen = open;
    }

    private void OnNewThread()
    {
        isMenuOpen = false;
        SidePanelState.RequestAction("New");
    }

    private void OnResumeThread()
    {
        isMenuOpen = false;
        SidePanelState.RequestAction("Resume");
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
