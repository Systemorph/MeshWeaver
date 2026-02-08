using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;

namespace MeshWeaver.AI;

public static class AgentChatLayoutArea
{
    public const string AreaName = "AgentChat";
    public const string ContextPointer = "/context";
    public const string TitlePointer = "/title";

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
