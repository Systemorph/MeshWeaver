using Microsoft.AspNetCore.Components;

namespace MeshWeaver.Blazor.Chat;

/// <summary>
/// Generic side panel container with a header (title + toolbar + close button)
/// and a content area. Thread-specific logic lives in ThreadSidePanelContent.
/// </summary>
public partial class ChatSidePanel : ComponentBase
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

    private async Task CloseChatAsync()
    {
        if (OnCloseRequested.HasDelegate)
        {
            await OnCloseRequested.InvokeAsync();
        }
    }
}
