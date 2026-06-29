using MeshWeaver.Application.Styles;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Messaging;

namespace MeshWeaver.AI.Application.Layout;

/// <summary>
/// Extension methods that register the AI layout areas (agent details, agent chat,
/// and chat navigation) with a message hub.
/// </summary>
public static class AIExtensions
{
    /// <summary>
    /// Adds the AI layout areas to the message hub by configuring its layout.
    /// </summary>
    /// <param name="config">The message hub configuration to extend.</param>
    /// <returns>The same configuration with the AI layouts registered.</returns>
    public static MessageHubConfiguration AddAIViews(this MessageHubConfiguration config)
        => config.AddLayout(AddAILayouts)
    ;

    extension(LayoutDefinition layout)
    {
        /// <summary>
        /// Registers all AI layout areas — agent details, agent chat, and chat navigation —
        /// on the layout definition.
        /// </summary>
        /// <returns>The same layout definition with the AI areas registered.</returns>
        public LayoutDefinition AddAILayouts()
            => layout.AddAgentDetails().AddAgentChat().AddChatNavigation();

        private LayoutDefinition AddChatNavigation()
            => layout.WithNavMenu("Chat", "/chat", FluentIcons.Chat());
    }
}
