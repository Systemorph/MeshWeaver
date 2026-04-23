using MeshWeaver.Messaging;

namespace MeshWeaver.Mesh;

/// <summary>
/// Asks a Code node's hub to run its own script. Posted to the Code node's address;
/// the Code hub reads its own <c>CodeConfiguration</c> from its workspace, checks
/// <c>IsExecutable</c>, and dispatches execution through its internal kernel. The
/// kernel layer is intentionally NOT exposed in this request — callers (MCP,
/// agents) never address the kernel directly; they only speak to the Code node.
/// </summary>
public record ExecuteScriptRequest : IRequest<ExecuteScriptResponse>
{
    /// <summary>
    /// Optional submission id. Used as the layout-area reference the kernel pushes
    /// output into, so the caller can subscribe to live progress afterwards. Left
    /// empty, the handler generates one and returns it in the response.
    /// </summary>
    public string? SubmissionId { get; init; }
}

/// <summary>
/// Response to <see cref="ExecuteScriptRequest"/>. Emitted by the Code node hub's
/// handler after it has dispatched the code to its kernel. The response is a
/// dispatch acknowledgement, not a run-completion signal — live progress and
/// terminal status come through the kernel's layout-area stream at
/// <see cref="OutputAreaReference"/>.
/// </summary>
public record ExecuteScriptResponse
{
    /// <summary>True if the node was executable and the dispatch succeeded.</summary>
    public bool Success { get; init; }

    /// <summary>The submission id the kernel is using for this run's output area.</summary>
    public string? SubmissionId { get; init; }

    /// <summary>
    /// Layout-area reference (on the Code node hub) the caller can subscribe to
    /// for live progress + stdout. Null when <c>Success == false</c>.
    /// </summary>
    public string? OutputAreaReference { get; init; }

    /// <summary>Human-readable error when <c>Success == false</c>.</summary>
    public string? Error { get; init; }
}
