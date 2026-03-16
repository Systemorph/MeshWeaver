using MeshWeaver.Messaging;

namespace MeshWeaver.AI;

/// <summary>
/// Request to create a new Thread node under a given namespace.
/// Handled server-side on the mesh hub. Creates the thread under
/// {Namespace}/_Thread/{speakingId} with proper satellite MainNode.
/// </summary>
public record CreateThreadRequest : IRequest<CreateThreadResponse>
{
    /// <summary>
    /// The namespace (context path) under which to create the thread.
    /// Thread will be created at {Namespace}/_Thread/{speakingId}.
    /// </summary>
    public required string Namespace { get; init; }

    /// <summary>
    /// The first message text, used to derive the thread name and speaking ID.
    /// </summary>
    public required string UserMessageText { get; init; }

    /// <summary>
    /// Optional initial context path for the thread.
    /// </summary>
    public string? InitialContext { get; init; }

    /// <summary>
    /// Optional model name for agent initialization.
    /// </summary>
    public string? ModelName { get; init; }
}

public record CreateThreadResponse
{
    public string? ThreadPath { get; init; }
    public string? ThreadName { get; init; }
    public bool Success { get; init; }
    public string? Error { get; init; }
}
