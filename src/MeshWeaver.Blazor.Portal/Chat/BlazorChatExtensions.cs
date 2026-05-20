using MeshWeaver.AI;
using MeshWeaver.Layout;
using MeshWeaver.Messaging;

namespace MeshWeaver.Blazor.Portal.Chat;

/// <summary>
/// Extensions for adding Chat Blazor views to the application.
/// </summary>
public static class BlazorChatExtensions
{
    /// <summary>
    /// Adds the Chat Blazor views (ThreadChatView) to the configuration.
    /// Types are registered centrally via AddAI() — no duplicate registration here.
    /// </summary>
    public static MessageHubConfiguration AddChatViews(this MessageHubConfiguration configuration)
    {
        configuration.TypeRegistry.AddAITypes();
        return configuration
            .AddViews(registry => registry
                .WithView<ThreadChatControl, ThreadChatView>()
                // Per-item dispatcher inside the chat's ItemTemplateControl — opens
                // ONE IMeshNodeStreamCache subscription per visible message and
                // role-dispatches in Razor. See Doc/GUI/ItemTemplateMeshNodeStreamBinding.
                .WithView<ThreadMessageItemControl, ThreadMessageItemView>()
                // Delegation tool-call card — opens TWO cache subscriptions (the
                // parent's response cell for live Status / Result, the sub-thread
                // for live Name). Rendered inline by ThreadMessageItemView's output
                // branch for tool calls with non-null DelegationPath.
                .WithView<DelegationToolCallCardControl, DelegationToolCallCardView>());
    }
}
