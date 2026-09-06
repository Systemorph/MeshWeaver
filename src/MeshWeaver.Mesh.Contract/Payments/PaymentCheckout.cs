using System.Collections.Immutable;

namespace MeshWeaver.Payments;

/// <summary>
/// Everything a payment provider must be TOLD to open a hosted checkout — provider-neutral by
/// construction: a reference, what the buyer is buying, what it costs, where to send them back to,
/// and the facts that must survive the round trip.
///
/// <para>🚨 <see cref="Metadata"/> is the load-bearing part. It is stamped at submit and handed
/// back on the verified delivery — months later for a subscription renewal — and fulfilment trusts
/// THAT snapshot rather than a node the buyer can edit. Its keys are
/// <see cref="PaymentMetadata"/>'s constants, never literals: producer and consumer are hours or
/// weeks apart, so a typo is not a compile error and not a test failure, it is a subscriber who
/// lapses with nothing anywhere to say why.</para>
/// </summary>
public sealed record PaymentCheckoutRequest
{
    /// <summary>
    /// The caller's own handle for this purchase (an order path), for the PROVIDER's records — it is
    /// what makes a session traceable to an order in the processor's own dashboard.
    ///
    /// <para>🚨 It is NOT how the facts come back. A delivery hands back
    /// <see cref="PaymentCheckoutEvent.Metadata"/>, and nothing else; a caller that needs its
    /// reference on fulfilment stamps it there too — which is exactly what
    /// <see cref="PaymentMetadata.OrderPath"/> is. Deliberate: one map, read one way, rather than
    /// two channels that can disagree about which purchase a payment was for.</para>
    /// </summary>
    public required string Reference { get; init; }

    /// <summary>What the buyer sees on the provider's page.</summary>
    public required string ProductName { get; init; }

    /// <summary>The price in the currency's MAJOR unit; the provider converts if it bills in minor units.</summary>
    public required decimal Amount { get; init; }

    /// <summary>ISO-4217, any case.</summary>
    public required string Currency { get; init; }

    /// <summary>Where the provider returns a buyer who paid.</summary>
    public required string SuccessUrl { get; init; }

    /// <summary>Where the provider returns a buyer who walked away.</summary>
    public required string CancelUrl { get; init; }

    /// <summary>Facts the verified delivery must hand back, verbatim. Keyed by <see cref="PaymentMetadata"/>.</summary>
    public ImmutableDictionary<string, string> Metadata { get; init; } =
        ImmutableDictionary<string, string>.Empty;

    /// <summary>
    /// Null for a ONE-OFF purchase; set for a recurring one. The single discriminator between the
    /// two checkout shapes, so a caller cannot ask for a subscription and forget to say on what
    /// cadence.
    /// </summary>
    public PaymentRecurrence? Recurrence { get; init; }
}

/// <summary>
/// What makes a checkout RECURRING: the cadence it bills on, and optionally the buyer's email to
/// prefill the provider's page with.
/// </summary>
/// <param name="Cadence">The caller's cadence word (e.g. <c>monthly</c>, <c>annual</c>); the
/// provider maps it onto its own billing interval.</param>
/// <param name="CustomerEmail">Prefills the provider's page; omitted when blank. The billing
/// profile already asked for it, and asking twice is how a receipt ends up at a second address.</param>
public sealed record PaymentRecurrence(string Cadence, string? CustomerEmail = null);

/// <summary>One created hosted-checkout session.</summary>
/// <param name="SessionId">The provider's session id — stamped on the order so a delivery can be paired.</param>
/// <param name="Url">Where to send the buyer.</param>
public sealed record PaymentCheckout(string SessionId, string Url);
