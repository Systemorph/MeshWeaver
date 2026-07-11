using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Layout.Composition;

namespace MeshWeaver.Layout;

/// <summary>
/// A generic drop target: wraps arbitrary <see cref="Content"/> as a drop zone. When a
/// <see cref="DraggableControl"/> is dropped onto it, the client posts a <see cref="DropEvent"/>
/// carrying the dragged payload and the framework invokes the server-side <see cref="DropAction"/>
/// (mirroring how a <see cref="ButtonControl"/> click invokes its click action).
/// </summary>
public record DropTargetControl
    : UiControl<DropTargetControl>
{
    internal DropTargetControl(object content)
        : base(ModuleSetup.ModuleName, ModuleSetup.ApiVersion)
    {
        Content = content;
    }

    /// <summary>Initializes a <see cref="DropTargetControl"/> with default placeholder content.</summary>
    public DropTargetControl()
        : this("Drop here")
    {
    }

    /// <summary>The control rendered inside the drop zone. Rendered into <see cref="ContentArea"/>.</summary>
    internal object Content { get; init; }

    /// <summary>
    /// Optional drag type filter surfaced to the client (e.g. a category the target accepts). When set,
    /// the client only signals a valid drop for matching draggables. <c>null</c> accepts everything.
    /// </summary>
    public object? AcceptTypes { get; init; }

    /// <summary>
    /// The sub-area into which <see cref="Content"/> is rendered. Set during rendering to a
    /// per-instance path (<c>{area}/Content</c>) and referenced by the client views.
    /// </summary>
    public NamedAreaControl? ContentArea { get; init; }

    /// <summary>Server-side handler invoked when a draggable is dropped on this target. Not serialized.</summary>
    internal Func<DropContext, Task>? DropAction { get; init; }

    /// <summary>Returns a copy with <paramref name="content"/> as the drop-zone body.</summary>
    /// <param name="content">The control to render inside the drop zone.</param>
    public DropTargetControl WithContent(object content)
        => this with { Content = content };

    /// <summary>Returns a copy accepting only draggables tagged with <paramref name="types"/>.</summary>
    /// <param name="types">The drag type(s) this target accepts.</param>
    public DropTargetControl WithAcceptTypes(object types)
        => this with { AcceptTypes = types };

    /// <summary>Returns a copy with <paramref name="onDrop"/> invoked when a draggable is dropped here.</summary>
    /// <param name="onDrop">An asynchronous handler receiving the dropped payload.</param>
    public DropTargetControl WithDropAction(Func<DropContext, Task> onDrop)
        => this with { DropAction = onDrop };

    /// <summary>Returns a copy with a synchronous <paramref name="onDrop"/> handler (wrapped as a completed task).</summary>
    /// <param name="onDrop">A synchronous handler receiving the dropped payload.</param>
    public DropTargetControl WithDropAction(Action<DropContext> onDrop)
        => WithDropAction(c =>
        {
            onDrop(c);
            return Task.CompletedTask;
        });

    /// <summary>Sets the per-instance <see cref="ContentArea"/> path before the control is stored.</summary>
    protected override DropTargetControl PrepareRendering(RenderingContext context)
        => this with { ContentArea = new NamedAreaControl($"{context.Area}/{nameof(Content)}") };

    /// <summary>Writes this control, then renders <see cref="Content"/> into its <see cref="ContentArea"/> sub-area.</summary>
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
