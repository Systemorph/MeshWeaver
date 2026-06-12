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

/// <summary>
/// Hub-level "answer everything I can't handle with an error" policy — the
/// fallback-hub contract for nodes whose NodeType cannot produce a real hub
/// configuration (non-compiling source, unregistered type, enrichment fault).
///
/// <para>Set on the hub via <c>MessageHubConfiguration.Set(new UnhandledMessageNack(...))</c>.
/// The hub still serves whatever its (default/overlay) configuration DOES handle
/// — e.g. the error Overview layout, Ping — but every message that reaches the
/// end of the handler chain unhandled is answered with a
/// <see cref="DeliveryFailure"/> carrying <see cref="ErrorType"/> and
/// <see cref="NodeTypePath"/>, instead of being silently
/// <c>Ignored()</c>. Without this, a typed request to a broken-type hub arrives
/// as RawJson (type not in the hub's registry), fails the <c>IRequest&lt;&gt;</c>
/// check, and the caller parks forever — the atioz wedge of 2026-06-12.</para>
/// </summary>
public record UnhandledMessageNack(
    string Reason,
    ErrorType ErrorType = ErrorType.Failed,
    string? NodeTypePath = null);

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


[SystemMessage]
public record DisposeRequest;
public record PingRequest : IRequest<PingResponse>;

public record PingResponse;
