using Microsoft.AspNetCore.Components;

namespace MeshWeaver.Blazor.Portal.SidePanel;

/// <summary>
/// Generic side panel container with a header (title + toolbar + close button)
/// and a content area. Content-specific logic lives in child components.
/// </summary>
public partial class SidePanel : ComponentBase
{
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

    private async Task CloseAsync()
    {
        if (OnCloseRequested.HasDelegate)
        {
            await OnCloseRequested.InvokeAsync();
        }
    }
}
