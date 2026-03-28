namespace MeshWeaver.AI;

/// <summary>
/// Request to cancel the active streaming response for a thread.
/// Propagates bottom-up: sub-threads cancel first, then parent.
/// </summary>
public record CancelThreadStreamRequest
{
    public required string ThreadPath { get; init; }
}

/// <summary>
/// Response confirming thread cancellation is complete.
/// Sent after all sub-threads have confirmed and own execution is stopped.
/// </summary>
public record CancelThreadStreamResponse
{
    public required string ThreadPath { get; init; }
}
