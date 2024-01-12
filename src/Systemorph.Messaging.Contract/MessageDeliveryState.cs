namespace Systemorph.Messaging;

public static class MessageDeliveryState
{
    public const string Submitted = nameof(Submitted);
    public const string Forwarded = nameof(Forwarded);
    public const string Processed = nameof(Processed);
    public const string NotFound = nameof(NotFound);
    public const string Rejected = nameof(Rejected);
    public const string Failed = nameof(Failed);
    public const string Ignored = nameof(Ignored);
}