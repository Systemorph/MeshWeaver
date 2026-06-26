using System.Collections.Generic;
using MeshWeaver.Graph;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;

namespace MeshWeaver.AI;

/// <summary>
/// Per-user <b>Chat</b> node at <c>{user}/Chat</c> — the home-page chat surface. Its default
/// <see cref="OverviewArea"/> returns the <em>same</em> <see cref="ThreadChatControl"/> the side panel
/// mounts for a new chat, so the User Page composer is 1:1 the side-panel composer (Monaco editor,
/// harness/agent/model selectors, attachments, Send) — not a parallel, plain-textarea rendering.
///
/// <para><b>Submit starts a thread:</b> the control is created with no <see cref="ThreadChatControl.ThreadPath"/>,
/// so it is the new-chat composer. When the user sends, <c>ThreadChatView.SubmitMessageCore</c> sees no
/// thread and calls <c>StartThread</c> — creating the thread under the user's home and opening it. The
/// home composer therefore creates-and-opens a conversation exactly like the side panel.</para>
///
/// <para><b>Materialisation:</b> the node is created on demand by the User dashboard
/// (<c>UserActivityLayoutAreas</c>), which ensures <c>{user}/Chat</c> exists before embedding its Overview
/// area — so the area resolves for brand-new and pre-existing users alike, with no onboarding back-fill.</para>
/// </summary>
public static class ChatNodeType
{
    /// <summary>NodeType discriminator for the per-user home chat node.</summary>
    public const string NodeType = "Chat";

    /// <summary>The default area — returns the side-panel <see cref="ThreadChatControl"/>.</summary>
    public const string OverviewArea = "Overview";

    /// <summary>The per-user home chat node path: <c>{user}/Chat</c>.</summary>
    public static string PathFor(string user) => $"{user}/{NodeType}";

    /// <summary>Registers the Chat type node so a <c>{user}/Chat</c> hub exposes the Overview area.</summary>
    public static TBuilder AddChatType<TBuilder>(this TBuilder builder) where TBuilder : MeshBuilder
    {
        builder.AddMeshNodes(CreateMeshNode());
        return builder;
    }

    /// <summary>The type-definition node for nodeType="Chat".</summary>
    public static MeshNode CreateMeshNode() => new(NodeType)
    {
        Name = "Chat",
        Icon = "/static/NodeTypeIcons/message.svg",
        // A pure UI surface — never hand-created and never surfaced in search.
        ExcludeFromContext = new HashSet<string> { "search", "create" },
        HubConfiguration = config => config
            .AddMeshDataSource(source => source.WithContentType<Chat>())
            .AddLayout(layout => layout
                .WithDefaultArea(OverviewArea)
                .WithView(OverviewArea, Overview))
    };

    /// <summary>
    /// The Chat node's default area — returns the same control the side panel uses for a new chat.
    /// No thread path → new-chat composer; sending starts a thread (<c>ThreadChatView</c> → StartThread).
    /// The control is self-contained: the rendering <c>ThreadChatView</c> resolves the signed-in user
    /// from the circuit, so the area needs no node content.
    /// </summary>
    public static UiControl Overview(LayoutAreaHost host, RenderingContext context)
        => new ThreadChatControl();
}

/// <summary>Marker content for the per-user home <see cref="ChatNodeType"/> node — a UI surface only.</summary>
public record Chat;
