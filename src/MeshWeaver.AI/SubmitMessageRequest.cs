using MeshWeaver.Mesh.Security;
using MeshWeaver.Messaging;
using MeshWeaver.Messaging.Security;

namespace MeshWeaver.AI;

/// <summary>
/// Request to submit a user message to a thread.
/// The thread hub creates the user message node, response node, and streams the agent response.
/// Thread must exist before submitting — create via IMeshService.CreateNodeAsync.
/// Requires Thread permission on the thread's parent path (partition scope),
/// not on the thread path itself, since access assignments are at partition level.
/// </summary>
[SubmitMessagePermission]
public record SubmitMessageRequest : IRequest<SubmitMessageResponse>
{
    public required string ThreadPath { get; init; }
    public required string UserMessageText { get; init; }
    public string? AgentName { get; init; }
    public string? ModelName { get; init; }
    public string? ContextPath { get; init; }
    public IReadOnlyList<string>? Attachments { get; init; }

    /// <summary>
    /// Set by HandleSubmitMessage after creating the response node.
    /// The execution hub uses this to post streaming progress updates.
    /// </summary>
    public string? ResponsePath { get; init; }

}

/// <summary>
/// Checks Thread permission on the thread's parent scope (before _Thread segment).
/// Thread paths follow the pattern {parentPath}/_Thread/{threadId}.
/// Access assignments are at the parent partition level, not on individual threads.
/// </summary>
public class SubmitMessagePermissionAttribute() : RequiresPermissionAttribute(Permission.Thread)
{
    public override IEnumerable<(string Path, Permission Permission)> GetPermissionChecks(
        IMessageDelivery delivery, string hubPath)
    {
        // Extract parent path: User/rbuergi/_Thread/hello → User/rbuergi
        var threadIndex = hubPath.IndexOf("/_Thread", StringComparison.Ordinal);
        var parentPath = threadIndex > 0 ? hubPath[..threadIndex] : hubPath;
        yield return (parentPath, Permission.Thread);
    }
}

public record SubmitMessageResponse
{
    public bool Success { get; init; }
    public string? Error { get; init; }
    public SubmitMessageStatus Status { get; init; } = SubmitMessageStatus.CellsCreated;
    public string? ResponseText { get; init; }
}

public enum SubmitMessageStatus
{
    CellsCreated,
    ExecutionCompleted,
    ExecutionFailed,
    ExecutionCancelled
}
