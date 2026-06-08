namespace MeshWeaver.Data.Serialization;

/// <summary>
/// Options governing the lifecycle of remote-subscriber sync streams.
/// </summary>
public class SyncStreamOptions
{
    /// <summary>
    /// Interval between HeartBeatEvents posted to the owner hub. Doubles as the resubscribe
    /// detection window: when the owner is gone (recycled / idle / crashed), the next
    /// heartbeat returns DeliveryFailure and the subscriber re-issues SubscribeRequest to
    /// pick up a fresh snapshot from the new grain. Default: 45 seconds.
    /// Tests use a much shorter interval to verify the resubscribe path without waiting.
    /// </summary>
    public TimeSpan HeartbeatInterval { get; set; } = TimeSpan.FromSeconds(45);
}
