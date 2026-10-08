namespace MeshWeaver.Messaging;

/// <summary>
/// The ONE definition of "the target hub did not answer IN TIME" as it reaches a sender — whether
/// the bound fired in this process (a <see cref="TimeoutException"/>) or in the router, which
/// flattens the transport's own timeout into a <see cref="DeliveryFailureException"/> carrying text.
///
/// <para><b>Why text, and only these two phrases.</b> Across the grain boundary the
/// <see cref="TimeoutException"/> OBJECT does not survive: <c>RoutingGrain</c> posts
/// <c>"Delivery to 'X' failed: Response did not arrive on time in 00:00:30 …"</c> (Orleans'
/// <c>ResponseTimeout</c> on <c>IMessageHubGrain.DeliverMessage</c>) or
/// <c>"… Grain placement operation timed out for grain …"</c> as a <see cref="DeliveryFailure"/>
/// whose <see cref="DeliveryFailure.ErrorType"/> is <see cref="ErrorType.Failed"/>. Both are the
/// transport's own statement that no answer arrived — never that the target refused or cannot serve
/// the request. Nothing else is matched; in particular a NACK carrying a verdict
/// (<see cref="ErrorType.NotFound"/>, <see cref="ErrorType.Unauthorized"/>, …) never is.</para>
///
/// <para><b>Who asks.</b> The view retry (<c>AreaErrorClassifier.IsTransientHubFailure</c>) and the
/// pre-warmer's build-claim handshake (<c>BuildProtocolDriver.IsUnreachable</c>). Two private copies
/// of these phrases had already drifted: the handshake matched only the in-process type, so the
/// router's flattened 30 s timeout faulted the whole warm-up on its first occurrence instead of
/// being retried and then referred to the durable witness (MeshWeaver#5716).</para>
/// </summary>
public static class TransportTimeout
{
    /// <summary>Orleans' response timeout, as the router's NACK carries it.</summary>
    public const string ResponseTimeoutPhrase = "Response did not arrive on time";

    /// <summary>Orleans' placement timeout, as the router's NACK carries it.</summary>
    public const string PlacementTimeoutPhrase = "Grain placement operation timed out";

    /// <summary>
    /// True when <paramref name="exception"/> — or anything along its inner-exception chain, or any
    /// branch of an <see cref="AggregateException"/> — says the target did not answer in time.
    /// </summary>
    /// <param name="exception">The fault to classify; may be null.</param>
    public static bool IsNoAnswerInTime(Exception? exception)
    {
        var seen = new HashSet<Exception>(ReferenceEqualityComparer.Instance);
        return IsNoAnswerInTime(exception, seen);
    }

    /// <summary>
    /// True when <paramref name="exception"/> is the ROUTER's flattened transport timeout: a
    /// <see cref="DeliveryFailureException"/> stamped <see cref="ErrorType.Failed"/> — the stamp the
    /// router gives a transport fault — whose text carries one of the transport-timeout phrases.
    /// A NACK carrying a VERDICT (<see cref="ErrorType.NotFound"/>, <see cref="ErrorType.Forbidden"/>,
    /// …) is never one, whatever its reason text happens to quote.
    /// </summary>
    /// <param name="exception">One exception (its inner chain is not walked).</param>
    public static bool IsRoutedTransportTimeout(Exception? exception) =>
        exception is DeliveryFailureException { Failure.ErrorType: ErrorType.Failed, Message: { } message }
        && (message.Contains(ResponseTimeoutPhrase, StringComparison.OrdinalIgnoreCase)
            || message.Contains(PlacementTimeoutPhrase, StringComparison.OrdinalIgnoreCase));

    private const int MaxNodes = 256;

    private static bool IsNoAnswerInTime(Exception? exception, HashSet<Exception> seen)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (!seen.Add(current) || seen.Count > MaxNodes)
                return false;
            if (current is TimeoutException || IsRoutedTransportTimeout(current))
                return true;
            if (current is AggregateException aggregate)
                return aggregate.InnerExceptions.Any(branch => IsNoAnswerInTime(branch, seen));
        }
        return false;
    }
}
