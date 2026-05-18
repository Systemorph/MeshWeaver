using MeshWeaver.Messaging;

namespace MeshWeaver.AI;

/// <summary>
/// <b>DEPRECATED — flip <see cref="Thread.RequestedCancellationAt"/> via
/// <c>workspace.GetMeshNodeStream(threadPath).Update(...)</c> instead.</b>
/// The thread hub's cancellation watcher reacts to the flip and propagates
/// the cancel into delegation sub-threads. This type is retained only so
/// existing wire-level routing tests can still build; the legacy handler
/// has been removed. See <c>Doc/Architecture/RequestViaStreamUpdate.md</c>.
/// </summary>
[System.Obsolete("Flip MeshThread.RequestedCancellationAt via stream.Update — see RequestViaStreamUpdate.md.")]
public record CancelThreadStreamRequest : IRequest<CancelThreadStreamResponse>
{
    public required string ThreadPath { get; init; }
}

[System.Obsolete("Flip MeshThread.RequestedCancellationAt via stream.Update — see RequestViaStreamUpdate.md.")]
public record CancelThreadStreamResponse
{
    public required string ThreadPath { get; init; }
}
