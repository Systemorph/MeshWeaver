namespace MeshWeaver.Messaging;

public class DeliveryFailureException : Exception
{
    internal DeliveryFailureException()
        : base() { }

    public DeliveryFailureException(DeliveryFailure failure)
        : base($"Delivery of message {failure.Delivery.Id} failed : {failure.Delivery.Message}")
    {
        Failure = failure;
    }

    internal DeliveryFailureException(string message)
        : base(message) { }

    internal DeliveryFailureException(string message, Exception innerException)
        : base(message, innerException) { }

    internal DeliveryFailure Failure { get; }
}

public record DeliveryFailure(IMessageDelivery Delivery)
{
    public ErrorType ErrorType { get; init; }
    public string ExceptionType { get; init; }
    public string Message { get; init; }
    public string StackTrace { get; init; }

}

public enum ErrorType
{
    Unknown,
    Exception,
    NotFound,
    Rejected,
    Ignored,
    Failed,
    CompilationFailed,
    StartupScriptFailed
}

public record PersistenceAddress();

public record HeartbeatEvent(SyncDelivery Route);

public record DisposeRequest;
