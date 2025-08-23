namespace MeshWeaver.Messaging;

public enum MessageDeliveryState
{
    Submitted,
    Forwarded,
    Processed,
    NotFound,
    Rejected,
    Failed,
    Ignored
}
