using MeshWeaver.Layout;
using Microsoft.Extensions.AI;

namespace MeshWeaver.AI;

/// <summary>
/// Represents a layout area content in chat messages
/// </summary>
public class ChatLayoutAreaContent(LayoutAreaControl layoutAreaControl) : AIContent
{
    /// <summary>
    /// The layout area control ready for rendering
    /// </summary>
    public LayoutAreaControl LayoutAreaControl { get; } = layoutAreaControl;
}
