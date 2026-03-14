using MeshWeaver.Application.Styles;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Messaging;

namespace MeshWeaver.AI.Application.Layout;

public static class AIExtensions
{
    public static MessageHubConfiguration AddAIViews(this MessageHubConfiguration config)
        => config.AddLayout(AddAILayouts)
    ;

    extension(LayoutDefinition layout)
    {
        public LayoutDefinition AddAILayouts()
            => layout.AddAgentDetails().AddAgentChat().AddChatNavigation();

        private LayoutDefinition AddChatNavigation()
            => layout.WithNavMenu("Chat", "/chat", FluentIcons.Chat());
    }
}
