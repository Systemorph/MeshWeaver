namespace MeshWeaver.AI;

/// <summary>
/// Request to cancel the active streaming response for a thread.
/// </summary>
public record CancelThreadStreamRequest
{
    public required string ThreadPath { get; init; }
}
