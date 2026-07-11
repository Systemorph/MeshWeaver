using MeshWeaver.Data;
using MeshWeaver.Layout.Composition;

namespace MeshWeaver.Layout;

/// <summary>
/// A generic drag source: wraps arbitrary <see cref="Content"/> and makes it draggable.
/// When the user drops it on a <see cref="DropTargetControl"/>, the <see cref="Payload"/> is
/// carried to that target (client-side, via the native drag data transfer) and surfaced to the
/// target's drop handler. Compose <see cref="DraggableControl"/> + <see cref="DropTargetControl"/>
/// to build reorderable lists, kanban boards, trees, etc.
/// </summary>
public record DraggableControl
    : UiControl<DraggableControl>
{
    internal DraggableControl(object content, object? payload)
        : base(ModuleSetup.ModuleName, ModuleSetup.ApiVersion)
    {
        Content = content;
        Payload = payload;
    }

    /// <summary>Initializes a <see cref="DraggableControl"/> with default placeholder content.</summary>
    public DraggableControl()
        : this("Draggable content", null)
    {
    }

    /// <summary>
    /// The control rendered inside the draggable wrapper. Rendered into the <see cref="ContentArea"/>
    /// sub-area exactly like <see cref="DialogControl"/> renders its content, so any control tree can
    /// be made draggable.
    /// </summary>
    internal object Content { get; init; }

    /// <summary>
    /// The payload carried to the drop target when this element is dropped. Serialized to the client,
    /// stashed in the drag data transfer on <c>dragstart</c>, and echoed back in the
    /// <see cref="DropEvent"/> the target posts on drop.
    /// </summary>
    public object? Payload { get; init; }

    /// <summary>
    /// The sub-area into which <see cref="Content"/> is rendered. Set during rendering to a
    /// per-instance path (<c>{area}/Content</c>) and referenced by the client views.
    /// </summary>
    public NamedAreaControl? ContentArea { get; init; }

    /// <summary>Returns a copy with <paramref name="content"/> as the draggable body.</summary>
    /// <param name="content">The control to render inside the draggable wrapper.</param>
    public DraggableControl WithContent(object content)
        => this with { Content = content };

    /// <summary>Returns a copy with <paramref name="payload"/> carried to the drop target on drop.</summary>
    /// <param name="payload">The payload delivered to the target's drop handler.</param>
    public DraggableControl WithPayload(object payload)
        => this with { Payload = payload };

    /// <summary>Sets the per-instance <see cref="ContentArea"/> path before the control is stored.</summary>
    protected override DraggableControl PrepareRendering(RenderingContext context)
        => this with { ContentArea = new NamedAreaControl($"{context.Area}/{nameof(Content)}") };

    /// <summary>
    /// Writes this control, then renders <see cref="Content"/> into its <see cref="ContentArea"/>
    /// sub-area (the <see cref="DialogControl"/> / <see cref="ItemTemplateControl"/> pattern).
    /// </summary>
    protected override EntityStoreAndUpdates Render(LayoutAreaHost host, RenderingContext context, EntityStore store)
    {
        var ret = base.Render(host, context, store);
        if (Content is UiControl contentControl)
        {
            var rendered = host.RenderArea(GetContextForArea(context, nameof(Content)), contentControl, ret.Store);
            ret = rendered with { Updates = ret.Updates.Concat(rendered.Updates) };
        }
        return ret;
    }
}
