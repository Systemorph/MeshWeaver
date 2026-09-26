using System.Collections.Immutable;

namespace MeshWeaver.Payments;

/// <summary>
/// One webhook delivery as the provider read it: whether it AUTHENTICATED, and what it is ABOUT.
///
/// <para>🚨 Both halves in one answer, deliberately. The caller has to distinguish an
/// unauthenticated delivery that names consequential work — someone may have PAID and not been
/// served, so it is retained as evidence and replayed once the secret is fixed — from junk, which
/// is discarded so nobody can fill the container and drown the signal
/// (MeshWeaver.Plugins#1069).</para>
/// </summary>
public sealed record PaymentDelivery
{
    /// <summary>Nothing readable: neither a checkout nor a subscription event, and unauthenticated.</summary>
    public static PaymentDelivery None { get; } = new();

    /// <summary>
    /// Whether the provider holds the signing secret it would need to authenticate ANY delivery.
    ///
    /// <para>🚨 Distinct from <see cref="Authentic"/>, and the two must never be collapsed. "This
    /// deployment cannot check signatures at all" is a configuration state whose right answer is to
    /// LEAVE the delivery where it is — it processes once the secret lands. "This delivery failed a
    /// check that could have passed" is a security event whose right answer is to retain it as
    /// evidence and shout. Treating the first as the second discards deliveries a portal was simply
    /// not yet configured to read.</para>
    /// </summary>
    public bool CanAuthenticate { get; init; }

    /// <summary>Whether the signature verified — i.e. the delivery genuinely came from the provider.</summary>
    public bool Authentic { get; init; }

    /// <summary>The checkout this delivery is about, or null when it is not a checkout event.</summary>
    public PaymentCheckoutEvent? Checkout { get; init; }

    /// <summary>The subscription lifecycle this delivery is about, or null when it is not one.</summary>
    public PaymentSubscriptionEvent? Subscription { get; init; }

    /// <summary>
    /// Whether this delivery names work the portal would actually DO — a checkout naming an order,
    /// or a subscription event that renews a plan, ends it, or reports a failed charge on it. The predicate the retention decision
    /// turns on: a renewal discarded as noise is a subscriber whose plan silently lapses next month
    /// with nothing anywhere to say why. Pure.
    /// </summary>
    public bool NamesWork =>
        Checkout is { OrderPath: { Length: > 0 } } || Subscription is { IsActionable: true };
}

/// <summary>What a checkout delivery says happened to the session.</summary>
public enum PaymentCheckoutOutcome
{
    /// <summary>Something else about a checkout session — nothing the portal acts on.</summary>
    Other = 0,

    /// <summary>The buyer PAID. This is what fulfils an order.</summary>
    Completed = 1,

    /// <summary>The session expired unpaid. This is what cancels an abandoned order.</summary>
    Expired = 2,
}

/// <summary>
/// One parsed CHECKOUT delivery — the facts fulfilment runs on, taken from the session's metadata
/// as stamped at submit, never from a buyer-editable node.
/// </summary>
/// <param name="Outcome">What happened to the session.</param>
/// <param name="SessionId">The checkout session; paired against the order's stamped session id.</param>
/// <param name="Metadata">The metadata the session carries, verbatim.</param>
public sealed record PaymentCheckoutEvent(
    PaymentCheckoutOutcome Outcome,
    string SessionId,
    ImmutableDictionary<string, string> Metadata)
{
    /// <summary>The subscription the completed session created, when it created one.</summary>
    public string? SubscriptionId { get; init; }

    /// <summary>
    /// The processor's CUSTOMER the session was paid by, when it names one — the handle a hosted
    /// billing portal is opened for (<see cref="IPaymentProvider.OpenBillingPortal"/>). Recorded at
    /// fulfilment so "Manage billing" needs no second lookup.
    /// </summary>
    public string? CustomerId { get; init; }

    /// <summary>The order this purchase is for.</summary>
    public string? OrderPath => Meta(PaymentMetadata.OrderPath);

    /// <summary>The package bought, on a one-off purchase.</summary>
    public string? PluginPath => Meta(PaymentMetadata.PluginPath);

    /// <summary>The viewer who bought.</summary>
    public string? Buyer => Meta(PaymentMetadata.Buyer);

    /// <summary>The coupon applied, when one was.</summary>
    public string? CouponCode => Meta(PaymentMetadata.CouponCode);

    /// <summary>
    /// The plan bought, when this session was a subscription checkout. This is what makes a PLAN
    /// checkout distinguishable from a package one: the same delivery shape carries both, and only
    /// the metadata says which was bought.
    /// </summary>
    public string? PlanTier => Meta(PaymentMetadata.PlanTier);

    /// <summary>The billing cadence the plan was bought on.</summary>
    public string? Cadence => Meta(PaymentMetadata.Cadence);

    /// <summary>Whether this session bought a PLAN rather than a package. Pure.</summary>
    public bool IsPlan => !string.IsNullOrWhiteSpace(PlanTier);

    /// <summary>One metadata value, or null when absent or blank. Pure.</summary>
    public string? Meta(string key) =>
        Metadata.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value) ? value : null;
}

/// <summary>What a subscription-lifecycle delivery does to the plan it names.</summary>
public enum PaymentSubscriptionChange
{
    /// <summary>Nothing the portal acts on — including an event that names no viewer and no plan.</summary>
    None = 0,

    /// <summary>
    /// A new billing period was PAID. The plan is topped up.
    ///
    /// <para>🚨 The FIRST charge of a subscription is deliberately not a renewal — it is already
    /// granted by the completed checkout, and counting it twice grants two periods for one payment.
    /// Which invoice is which is the provider's own discriminator, so the provider decides this,
    /// never the caller.</para>
    /// </summary>
    Renewed = 1,

    /// <summary>The provider has stopped billing the subscription. This is what ends the plan.</summary>
    Ended = 2,

    /// <summary>
    /// A charge for the subscription FAILED — the card was declined, expired, or needs the
    /// subscriber's action. The plan is NOT ended: the processor retries, and the paid-through
    /// period still stands. It marks the plan past-due so the subscriber can be told to fix the
    /// card; the next <see cref="Renewed"/> clears it, and a processor that gives up reports
    /// <see cref="Ended"/>.
    /// </summary>
    PaymentFailed = 3,
}

/// <summary>
/// One parsed SUBSCRIPTION-LIFECYCLE delivery — a renewal, a FAILED charge (the plan goes past-due
/// while the processor retries) or the ending — which arrive long after the checkout that started
/// them and carry their facts on the SUBSCRIPTION's own metadata.
/// </summary>
/// <param name="Change">What this delivery does to the plan; <see cref="PaymentSubscriptionChange.None"/>
/// when it names no viewer and no plan, or is not one the portal acts on.</param>
/// <param name="SubscriptionId">The subscription this is about.</param>
/// <param name="Metadata">The subscription's metadata as this payload carries it.</param>
public sealed record PaymentSubscriptionEvent(
    PaymentSubscriptionChange Change,
    string? SubscriptionId,
    ImmutableDictionary<string, string> Metadata)
{
    /// <summary>The processor's CUSTOMER the subscription belongs to, when the payload names one.</summary>
    public string? CustomerId { get; init; }

    /// <summary>
    /// The processor's handle for the INVOICE this delivery is about (renewal and failed-payment
    /// deliveries), when it names one — the identity of ONE billing period's charge.
    /// </summary>
    public string? InvoiceId { get; init; }

    /// <summary>
    /// The end of the billing period this delivery's invoice PAID FOR, when the payload states it.
    ///
    /// <para>🚨 This is what makes a renewal idempotent BY VALUE. "Extend by one period from the
    /// current expiry" grants a second period each time the same payment is delivered twice (a
    /// redelivery, a duplicate queue entry); "the plan is paid through this instant" is the same
    /// answer however often it arrives. A caller extends the plan TO this instant — never beyond
    /// what is already stamped — whenever it is present, and falls back to the cadence arithmetic
    /// only when the processor did not say.</para>
    /// </summary>
    public DateTimeOffset? PaidThrough { get; init; }

    /// <summary>The viewer whose plan it is.</summary>
    public string? Buyer => Meta(PaymentMetadata.Buyer);

    /// <summary>The plan.</summary>
    public string? PlanTier => Meta(PaymentMetadata.PlanTier);

    /// <summary>The billing cadence.</summary>
    public string? Cadence => Meta(PaymentMetadata.Cadence);

    /// <summary>Whether this event RENEWS the plan. Pure.</summary>
    public bool Renews => Change is PaymentSubscriptionChange.Renewed;

    /// <summary>Whether this event ENDS the plan. Pure.</summary>
    public bool Ends => Change is PaymentSubscriptionChange.Ended;

    /// <summary>Whether this event reports a FAILED charge — the plan goes past-due. Pure.</summary>
    public bool FailsPayment => Change is PaymentSubscriptionChange.PaymentFailed;

    /// <summary>Whether this event is one the portal acts on at all. Pure.</summary>
    public bool IsActionable => Renews || Ends || FailsPayment;

    /// <summary>One metadata value, or null when absent or blank. Pure.</summary>
    public string? Meta(string key) =>
        Metadata.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value) ? value : null;
}
