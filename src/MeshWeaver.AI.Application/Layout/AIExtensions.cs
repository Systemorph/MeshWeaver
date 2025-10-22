#nullable enable
using MeshWeaver.Application.Styles;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Messaging;

namespace MeshWeaver.AI.Application.Layout;

public static class AIExtensions
{
    public static MessageHubConfiguration AddAIViews(this MessageHubConfiguration config)
        => config.AddLayout(AddAILayouts);

    public static LayoutDefinition AddAILayouts(this LayoutDefinition layout)
        => layout.AddAgentOverview().AddAgentDetails().AddAgentChat().AddChatNavigation();

    private static LayoutDefinition AddChatNavigation(this LayoutDefinition layout)
        => layout.WithNavMenu("Chat", "/chat", FluentIcons.Chat());
}
