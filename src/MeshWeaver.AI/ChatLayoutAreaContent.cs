using Microsoft.Extensions.AI;
using MeshWeaver.Layout;

namespace MeshWeaver.AI;

/// <summary>
/// Represents a layout area content in chat messages
/// </summary>
public class ChatLayoutAreaContent : AIContent
{
    /// <summary>
    /// The layout area control ready for rendering
    /// </summary>
    public LayoutAreaControl LayoutAreaControl { get; }

    public ChatLayoutAreaContent(LayoutAreaControl layoutAreaControl)
    {
        LayoutAreaControl = layoutAreaControl;
    }
}