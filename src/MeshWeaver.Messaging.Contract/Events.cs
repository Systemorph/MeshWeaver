namespace MeshWeaver.Messaging;

/// <summary>
/// Exception that surfaces a <see cref="DeliveryFailure"/> as a throwable error,
/// so a request/response caller can observe a NACK as an exception on its stream.
/// </summary>
public class DeliveryFailureException : Exception
{
    internal DeliveryFailureException()
    { }

    /// <summary>
    /// Creates an exception wrapping the given delivery failure, using the failure's
    /// message as the exception message.
    /// </summary>
    /// <param name="failure">The delivery failure to wrap.</param>
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

/// <summary>
/// Negative-acknowledgement (NACK) describing why a message could not be delivered
/// or processed, carrying the offending delivery and diagnostic detail.
/// </summary>
/// <param name="Delivery">The message delivery that failed.</param>
/// <param name="Message">A human-readable failure message, when available.</param>
public record DeliveryFailure(IMessageDelivery Delivery, string? Message = null)
{
    /// <summary>
    /// The category of failure (see <see cref="MeshWeaver.Messaging.ErrorType"/>).
    /// </summary>
    public ErrorType ErrorType { get; init; }
    /// <summary>
    /// Type name of the originating exception, when the failure came from one.
    /// </summary>
    public string ExceptionType { get; init; } = string.Empty;
    /// <summary>
    /// Stack trace of the originating exception, when available.
    /// </summary>
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

    /// <summary>
    /// Builds a delivery failure from a caught exception, capturing its type name,
    /// message, and stack trace under <see cref="MeshWeaver.Messaging.ErrorType.Exception"/>.
    /// </summary>
    /// <param name="request">The delivery that was being processed when the exception occurred.</param>
    /// <param name="e">The exception to record.</param>
    /// <returns>A delivery failure describing the exception.</returns>
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

/// <summary>
/// Hub-level "forward, don't deserialize" policy for PROXY hubs that stand in for a remote
/// participant (the gRPC connection registry's hosted hub is the canonical case). Such a hub is the
/// delivery <em>target</em> only nominally — its catch-all route re-serializes every delivery onto
/// the participant's wire, so inbound payloads whose <c>$type</c> is not in the server's
/// TypeRegistry (a participant's own protocol, e.g. the Python pandas node's <c>PandasCommand</c>)
/// must stay <c>RawJson</c> and forward verbatim instead of failing with
/// "type not registered in this hub's TypeRegistry".
///
/// <para>Set on the proxy hub via <c>MessageHubConfiguration.Set(new RawJsonPassThrough())</c>.
/// Registered types still deserialize normally; only the unregistered-type fallback changes from
/// fail-the-delivery to pass-through. Do NOT set this on hubs that dispatch to typed handlers —
/// there the fail-fast is what keeps callers from parking forever (see
/// <see cref="UnhandledMessageNack"/>).</para>
/// </summary>
public record RawJsonPassThrough;

/// <summary>
/// Classifies why a message delivery failed, carried on a <see cref="DeliveryFailure"/>.
/// </summary>
public enum ErrorType
{
    /// <summary>
    /// The failure cause could not be classified.
    /// </summary>
    Unknown,
    /// <summary>
    /// Processing threw an exception (details in <see cref="DeliveryFailure.ExceptionType"/>
    /// and <see cref="DeliveryFailure.StackTrace"/>).
    /// </summary>
    Exception,
    /// <summary>
    /// No handler or target node was found for the message.
    /// </summary>
    NotFound,
    /// <summary>
    /// The message was explicitly rejected by a handler.
    /// </summary>
    Rejected,
    /// <summary>
    /// The message was ignored — no handler chose to process it.
    /// </summary>
    Ignored,
    /// <summary>
    /// Processing failed for a general, non-exception reason.
    /// </summary>
    Failed,
    /// <summary>
    /// The target hub's NodeType source failed to compile.
    /// </summary>
    CompilationFailed,
    /// <summary>
    /// Target hub cannot activate because its NodeType is mid-compile (or has
    /// not yet started one). The grain returns this NACK immediately instead of
    /// blocking activation; <see cref="DeliveryFailure.NodeTypePath"/> tells the
    /// GUI where to data-bind for progress (the per-NodeType hub's
    /// <c>"Progress"</c> layout area, which in turn embeds the activity log).
    /// </summary>
    CompilationInProgress,
    /// <summary>
    /// The hub's startup script failed to run.
    /// </summary>
    StartupScriptFailed,
    /// <summary>
    /// The message was caught in a routing loop and could not reach a target.
    /// </summary>
    RoutingLoop,
    /// <summary>
    /// The caller is not authenticated for this operation.
    /// </summary>
    Unauthorized,
    /// <summary>
    /// The caller is authenticated but lacks permission for this operation.
    /// </summary>
    Forbidden
}


/// <summary>
/// Request asking a hub to dispose itself and tear down its resources.
/// </summary>
[SystemMessage]
[CanBeIgnored]
public record DisposeRequest;
/// <summary>
/// Liveness probe requesting a <see cref="PingResponse"/> from the target hub.
/// </summary>
public record PingRequest : IRequest<PingResponse>;

/// <summary>
/// Reply to a <see cref="PingRequest"/>, confirming the hub is reachable.
/// </summary>
public record PingResponse;
