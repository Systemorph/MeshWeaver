using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Messaging;

namespace MeshWeaver.AI;

/// <summary>
/// Layout view for the per-user <see cref="ThreadComposer"/> node. Registers the chat
/// composer as the node's <b>default area</b> (<c>""</c>) so it can be addressed and
/// mounted as
/// <c>new LayoutAreaControl("{user}/_Memex/ThreadComposer", new LayoutAreaReference(""))</c>
/// and shown at the bottom of the chat panel.
///
/// <para>The composer is the SAME <see cref="ThreadChatControl"/> the thread view uses,
/// in out-of-thread (new-chat) compose mode. <c>WithHideEmptyState</c> drops the empty
/// message-history pane so only the input box renders. The control self-binds its draft
/// + harness/agent/model selection to this node via <c>ThreadChatView</c>'s template
/// logic (LoadTemplate / WriteTemplate), which targets <c>{user}/_Memex/ThreadComposer</c>.</para>
///
/// <para>Mirrors the static-extension shape of <see cref="AgentView.AddAgentView"/> and
/// <see cref="ThreadLayoutAreas.AddThreadLayoutAreas"/>.</para>
/// </summary>
public static class ThreadComposerView
{
    /// <summary>The composer area name — registered as the node's default area.</summary>
    public const string ComposerArea = "Composer";

    /// <summary>
    /// Adds the ThreadComposer composer view to the hub's layout. <see cref="ComposerArea"/>
    /// is the default area, so an empty <see cref="LayoutAreaReference"/> (<c>""</c>)
    /// resolves to it.
    /// </summary>
    public static MessageHubConfiguration AddThreadComposerView(this MessageHubConfiguration configuration)
        => configuration.AddLayout(layout => layout
            .WithDefaultArea(ComposerArea)
            .WithView(ComposerArea, Composer));

    /// <summary>
    /// Renders the new-chat composer (message text + harness/agent/model comboboxes +
    /// attachments). No thread is bound, so the composer runs in compose mode and
    /// persists draft + selection to this <see cref="ThreadComposer"/> node.
    /// </summary>
    public static UiControl? Composer(LayoutAreaHost host, RenderingContext context)
        => new ThreadChatControl().WithHideEmptyState();
}
