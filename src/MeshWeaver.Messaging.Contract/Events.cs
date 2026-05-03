namespace MeshWeaver.Messaging;

public class DeliveryFailureException : Exception
{
    internal DeliveryFailureException()
    { }

    public DeliveryFailureException(DeliveryFailure failure)
        : base(failure.Message)
    {
        Failure = failure;
    }

    internal DeliveryFailureException(string message)
        : base(message) { }

    internal DeliveryFailureException(string message, Exception innerException)
        : base(message, innerException) { }

    /// <summary>
    /// The underlying <see cref="DeliveryFailure"/> when this exception was
    /// constructed from one. Surfaces the original failure's <c>ErrorType</c>,
    /// <c>ExceptionType</c>, <c>StackTrace</c>, and the offending <c>Delivery</c>
    /// — callers can map these back to domain-level rejection reasons without
    /// relying on the exception's user-facing message.
    /// </summary>
    public DeliveryFailure Failure { get; } = null!;
}

public record DeliveryFailure(IMessageDelivery Delivery, string? Message = null)
{
    public ErrorType ErrorType { get; init; }
    public string ExceptionType { get; init; } = string.Empty;
    public string StackTrace { get; init; } = string.Empty;

    public static DeliveryFailure FromException(IMessageDelivery request, Exception e) =>
        new(request)
        {
            ErrorType = ErrorType.Exception,
            ExceptionType = e.GetType().Name,
            Message = e.Message,
            StackTrace = e.StackTrace ?? string.Empty
        };
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
    StartupScriptFailed,
    RoutingLoop,
    Unauthorized,
    Forbidden
}


public record DisposeRequest;
public record PingRequest : IRequest<PingResponse>;

public record PingResponse;
