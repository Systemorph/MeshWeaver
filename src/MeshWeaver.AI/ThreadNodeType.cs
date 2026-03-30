using System.Text.RegularExpressions;
using MeshWeaver.Data;
using MeshWeaver.Graph;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.AI;

/// <summary>
/// Constants and configuration for Thread node types.
/// </summary>
public static class ThreadNodeType
{
    /// <summary>
    /// The NodeType value used to identify thread nodes.
    /// </summary>
    public const string NodeType = "Thread";

    /// <summary>
    /// Satellite partition name for threads (like _Comment for comments).
    /// Threads are created at {contextPath}/_Thread/{speakingId}.
    /// </summary>
    public const string ThreadPartition = "_Thread";

    /// <summary>
    /// Layout area for thread content and message history (default).
    /// </summary>
    public const string ThreadArea = "Thread";

    /// <summary>
    /// Layout area for just the chat control (messages + input), without the header.
    /// Used by the side panel to avoid rendering the full-page header.
    /// </summary>
    public const string ThreadChatArea = "ThreadChat";

    /// <summary>
    /// Layout area showing current execution progress: streaming response message
    /// and active tool calls / delegations. Parent threads subscribe to this.
    /// Composed of: OutputCell + ToolCallsArea.
    /// </summary>
    public const string StreamingArea = "Streaming";

    /// <summary>
    /// Layout area showing only the active tool calls and delegation sub-threads.
    /// Rendered below the message list in the chat view (without duplicating the output cell).
    /// Hidden when no tool calls are active.
    /// </summary>
    public const string ToolCallsArea = "ToolCalls";

    /// <summary>
    /// Layout area for delegation sub-thread history.
    /// </summary>
    public const string HistoryArea = "History";

    /// <summary>
    /// Generates a human-readable speaking ID from message text.
    /// Takes the first few words, lowercases, replaces non-alphanumeric with hyphens,
    /// and appends a short unique suffix.
    /// Example: "Hello, can you help me with this?" → "hello-can-you-help-me-a3f9"
    /// </summary>
    public static string GenerateSpeakingId(string messageText)
    {
        // Take first ~50 chars, lowercase, replace non-alphanumeric with hyphens
        var slug = messageText.Length > 50 ? messageText[..50] : messageText;
        slug = Regex.Replace(slug.ToLowerInvariant(), @"[^a-z0-9]+", "-").Trim('-');

        // Truncate to reasonable length
        if (slug.Length > 40)
            slug = slug[..40].TrimEnd('-');

        // Append short unique suffix to avoid collisions
        var suffix = Guid.NewGuid().ToString("N")[..4];
        return string.IsNullOrEmpty(slug) ? suffix : $"{slug}-{suffix}";
    }

    /// <summary>
    /// Builds a MeshNode for a new thread. Use with CreateNodeRequest.
    /// </summary>
    /// <param name="contextPath">The namespace/context path (e.g., "User/Roland")</param>
    /// <param name="messageText">First message text — used for name and speaking ID</param>
    /// <param name="createdBy">User ID who creates the thread</param>
    public static MeshNode BuildThreadNode(string contextPath, string messageText, string? createdBy = null)
    {
        var speakingId = GenerateSpeakingId(messageText);
        var ns = string.IsNullOrEmpty(contextPath)
            ? ThreadPartition
            : $"{contextPath}/{ThreadPartition}";
        var name = messageText.Length > 60
            ? messageText[..57] + "..."
            : messageText;

        return new MeshNode(speakingId, ns)
        {
            Name = name,
            NodeType = NodeType,
            MainNode = contextPath,
            Content = new Thread { CreatedBy = createdBy }
        };
    }

    /// <summary>
    /// Checks if a MeshNode is a Thread by checking its NodeType.
    /// </summary>
    /// <param name="nodeType">The node type to check.</param>
    /// <returns>True if the node type is Thread.</returns>
    public static bool IsThreadNodeType(string? nodeType)
    {
        return string.Equals(nodeType, NodeType, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Registers the built-in "Thread" MeshNode on the mesh builder.
    /// </summary>
    /// <param name="builder">The mesh builder.</param>
    /// <param name="hubConfiguration">Hub configuration for thread nodes (views, data sources, etc.).</param>
    public static TBuilder AddThreadType<TBuilder>(this TBuilder builder,
        Func<MessageHubConfiguration, MessageHubConfiguration>? hubConfiguration = null)
        where TBuilder : MeshBuilder
    {
        builder.AddMeshNodes(CreateMeshNode(hubConfiguration));
        builder.AddAutocompleteExcludedTypes(NodeType);
        return builder;
    }

    /// <summary>
    /// Creates a MeshNode definition for the Thread node type.
    /// </summary>
    /// <param name="hubConfiguration">Hub configuration for thread nodes (views, data sources, etc.).</param>
    public static MeshNode CreateMeshNode(
        Func<MessageHubConfiguration, MessageHubConfiguration>? hubConfiguration = null) => new(NodeType)
        {
            Name = "Thread",
            Icon = "/static/NodeTypeIcons/chat.svg",
            IsSatelliteType = true,
            ExcludeFromContext = new HashSet<string> { "search" },
            AssemblyLocation = typeof(ThreadNodeType).Assembly.Location,
            HubConfiguration = config => config
                .AddThreadLayoutAreas()
                .AddThreadExecution()
                .AddMeshDataSource(source => source
                    .WithContentType<Thread>())
        };
}
