using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;

namespace MeshWeaver.AI;

public static class AgentChatLayoutArea
{
    public const string AreaName = "AgentChat";
    public const string ContextPointer = "/context";

    public static LayoutDefinition AddAgentChat(this LayoutDefinition layout)
    {
        return layout.WithView(AreaName, RenderChat);
    }

    private static UiControl RenderChat(LayoutAreaHost host, RenderingContext ctx)
    {
        // Get the context from the store using the pointer
        var contextReference = ctx.Area + ContextPointer;

        var control = new AgentChatControl
        {
            Context = contextReference
        };

        return control;
    }
}
