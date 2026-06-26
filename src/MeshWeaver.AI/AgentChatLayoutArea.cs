using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;

namespace MeshWeaver.AI;

/// <summary>
/// Registers and renders the shared "AgentChat" layout area, which hosts the
/// thread-based chat UI (<c>ThreadChatControl</c>).
/// </summary>
public static class AgentChatLayoutArea
{
    /// <summary>Name of the chat layout area ("AgentChat").</summary>
    public const string AreaName = "AgentChat";

    /// <summary>JSON pointer to the area's context parameter ("/context").</summary>
    public const string ContextPointer = "/context";

    /// <summary>JSON pointer to the area's title parameter ("/title").</summary>
    public const string TitlePointer = "/title";

    /// <summary>
    /// Adds the AgentChat view to the given layout definition.
    /// </summary>
    /// <param name="layout">The layout definition to extend.</param>
    /// <returns>The same layout definition with the AgentChat view registered.</returns>
    public static LayoutDefinition AddAgentChat(this LayoutDefinition layout)
    {
        return layout.WithView(AreaName, RenderChat);
    }

    private static UiControl RenderChat(LayoutAreaHost host, RenderingContext ctx)
    {
        // Use ThreadChatControl for the chat layout area
        var control = new ThreadChatControl();
        return control;
    }
}
