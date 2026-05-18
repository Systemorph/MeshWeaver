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

    /// <summary>
    /// Path of the NodeType whose compile state caused this NACK. Populated when
    /// <see cref="ErrorType"/> is <see cref="MeshWeaver.Messaging.ErrorType.CompilationInProgress"/>
    /// or <see cref="MeshWeaver.Messaging.ErrorType.CompilationFailed"/>. GUI clients
    /// data-bind to <c>LayoutAreaControl({NodeTypePath}, "Progress")</c> on the
    /// per-NodeType hub to render compile-progress UI (which embeds the activity
    /// log automatically when one is in flight).
    /// </summary>
    public string? NodeTypePath { get; init; }

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
    /// <summary>
    /// Target hub cannot activate because its NodeType is mid-compile (or has
    /// not yet started one). The grain returns this NACK immediately instead of
    /// blocking activation; <see cref="DeliveryFailure.NodeTypePath"/> tells the
    /// GUI where to data-bind for progress (the per-NodeType hub's
    /// <c>"Progress"</c> layout area, which in turn embeds the activity log).
    /// </summary>
    CompilationInProgress,
    StartupScriptFailed,
    RoutingLoop,
    Unauthorized,
    Forbidden
}


public record DisposeRequest;
public record PingRequest : IRequest<PingResponse>;

public record PingResponse;
