namespace MeshWeaver.Messaging;

/// <summary>
/// Reads a delivery's payload IDENTITY — the <see cref="IDiagnosticKeyed.DiagnosticKey"/> — without
/// deserializing anything and without casting <c>delivery.Message</c> to a domain type.
///
/// <para>🚨 <b>Why this is a shared primitive and not two copies.</b> Two layers need the same
/// answer on the two hottest paths in the mesh, and they must agree on it:</para>
/// <list type="bullet">
///   <item><c>MessageStormBreaker</c> keys its per-second rate counters on
///     <c>(sender, target, type, key)</c> — without the key, N legitimate writes to N DISTINCT
///     things fold into one bucket and the fan-out is DROPPED as if it were one thing looping
///     (issue #1200).</item>
///   <item><c>OrderedRouteDispatcher</c> keys its FIFO channel on <c>(destination, key)</c> —
///     without the key, N streams that share a destination hub are serialised into ONE channel
///     although no ordering relationship exists between them (issue #5009).</item>
/// </list>
/// <para>Those are the same statement about the same thing: the key is what says which messages are
/// ABOUT one thing. Resolving it in one place is what keeps "one channel" and "one rate bucket"
/// from drifting apart.</para>
///
/// <para><b>Two sources, in the order the type erasure happens</b> — the typed message when it opts
/// in (one interface check), then the envelope property <c>MessageDelivery.Package</c> stamped
/// immediately before the payload became <see cref="RawJson"/>. <c>null</c> means this message
/// exposes no identity; every caller must degrade to its pre-key behaviour on <c>null</c>, never to
/// a guess.</para>
/// </summary>
public static class DeliveryIdentity
{
    /// <summary>
    /// The delivery's payload identity, or <c>null</c> when the payload exposes none.
    /// </summary>
    /// <param name="delivery">The delivery whose envelope may carry the stamped key.</param>
    /// <param name="message">
    /// The payload to inspect first. Pass the re-typed message where the caller has already
    /// deserialized one; omit it to inspect <see cref="IMessageDelivery.Message"/>.
    /// </param>
    public static string? Read(IMessageDelivery delivery, object? message = null)
    {
        var payload = message ?? delivery.Message;
        if (payload is IDiagnosticKeyed keyed)
            return keyed.DiagnosticKey is { Length: > 0 } key ? key : null;
        // The property is stamped only where the type was erased, so it is read only there. A typed
        // payload that is not IDiagnosticKeyed has no identity by construction, and looking for one
        // on the envelope would read a stamp that belongs to a payload this delivery no longer
        // carries (a re-typed or re-wrapped message).
        if (payload is not RawJson)
            return null;
        if (!delivery.Properties.TryGetValue(IDiagnosticKeyed.DeliveryProperty, out var value))
            return null;
        // In-process the value is the string we stamped; across a JSON wire hop it arrives as a
        // JsonElement, whose ToString() is the string content (the same read PatchDataResponse's
        // RequestId correlation uses).
        return value switch
        {
            string s => s.Length > 0 ? s : null,
            null => null,
            _ => value.ToString() is { Length: > 0 } s ? s : null
        };
    }
}
