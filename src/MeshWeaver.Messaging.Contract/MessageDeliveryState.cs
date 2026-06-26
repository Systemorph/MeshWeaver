namespace MeshWeaver.Messaging;

/// <summary>
/// Lifecycle state of a message delivery as it moves through the hub pipeline.
/// </summary>
public enum MessageDeliveryState
{
    /// <summary>
    /// The delivery has been submitted but not yet routed or processed.
    /// </summary>
    Submitted,
    /// <summary>
    /// The delivery has been forwarded to another address.
    /// </summary>
    Forwarded,
    /// <summary>
    /// The delivery was successfully processed by a handler.
    /// </summary>
    Processed,
    /// <summary>
    /// No target or handler was found for the delivery.
    /// </summary>
    NotFound,
    /// <summary>
    /// The delivery was explicitly rejected by a handler.
    /// </summary>
    Rejected,
    /// <summary>
    /// Processing of the delivery failed.
    /// </summary>
    Failed,
    /// <summary>
    /// The delivery was ignored — no handler chose to process it.
    /// </summary>
    Ignored
}
