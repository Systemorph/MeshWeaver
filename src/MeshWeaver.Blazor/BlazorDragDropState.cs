namespace MeshWeaver.Blazor;

/// <summary>
/// Per-circuit scratch state for drag-and-drop: the payload of the draggable currently being
/// dragged. <see cref="Components.DraggableView"/> records its payload here on <c>dragstart</c> and
/// <see cref="Components.DropTargetView"/> reads it on <c>drop</c>. Scoped to the Blazor circuit, so
/// the payload travels server-side and a synthetic drag (e.g. a Playwright <c>dragTo</c>, which does
/// not reliably populate the browser drag data-transfer) still works end to end.
/// </summary>
public sealed class BlazorDragDropState
{
    /// <summary>The payload of the draggable currently under the cursor, or <c>null</c> when nothing is dragging.</summary>
    public object? Payload { get; set; }
}
