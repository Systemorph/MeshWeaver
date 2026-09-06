using System.Reactive;

namespace MeshWeaver.Payments;

/// <summary>
/// The platform's provider-neutral PAYMENT contract: everything a commerce surface must be able to
/// ask of "whatever takes the money on this deployment", and nothing about any particular card
/// processor.
///
/// <para>🚨 <b>The absence of a provider is a MODELLED STATE, not a null to guard.</b> Taking card
/// payments is an outbound-call surface to a named third party, so it ships as an optional module
/// (<c>MeshWeaver.Payments.Stripe</c> is the first implementation) and a great many deployments
/// mount none at all. A portal with no provider registered is a portal that <b>does not sell</b> —
/// a perfectly good configuration that must compile, install and run, with the payment features
/// absent or inert rather than broken. Callers therefore resolve
/// <see cref="HubPaymentExtensions.PaymentProvider"/> (nullable by construction) and answer the
/// no-provider case explicitly; they never reflect for an assembly, never <c>try/catch</c> around a
/// payment type, and never hold a provider they have not checked (MeshWeaver.Plugins#1328).</para>
///
/// <para>The contract lives here — beside <see cref="MeshWeaver.Mesh.IEmailSender"/>, and for the
/// identical reason. Content compiled INSIDE the mesh (the Store's NodeTypes) has to bind
/// something that is present in every portal image; binding a module assembly instead is what made
/// every consumer that did not mount that module fail to compile its Store types at all
/// (<c>CS0234: the type or namespace name 'Payments' does not exist in the namespace
/// 'MeshWeaver'</c>). An abstraction in the platform plus an implementation in a module is the one
/// shape where "not installed" degrades instead of breaking.</para>
///
/// <para>🚨 <b>Reactive end to end, and every returned observable is COLD.</b> Nothing here returns
/// a <c>Task</c>; the request is made on <c>Subscribe</c>, never on the call. An implementation
/// bridges its HTTP leaf through the mesh's bounded <c>IIoPool</c> and never
/// <c>Observable.FromAsync</c>.</para>
/// </summary>
public interface IPaymentProvider
{
    /// <summary>
    /// What this provider is called, for operator-facing prose ("Stripe"). A product name, not a
    /// translated one — the surrounding sentence is localized, the processor's name is not.
    /// </summary>
    string DisplayName { get; }

    /// <summary>
    /// The reason a payment cannot be taken on this portal right now — <c>null</c> when one can.
    /// Resolved in the VIEWER's language by the implementation, because it is shown to a buyer at
    /// the till.
    ///
    /// <para>Distinct from <see cref="Sells"/>: a provider can be installed and hold a credential
    /// (so the portal <i>does</i> sell) and still refuse a specific checkout for a reason of its
    /// own — no public base URL to return the buyer to, for instance.</para>
    /// </summary>
    string? Unavailable { get; }

    /// <summary>
    /// Whether this portal takes money at all — i.e. the provider holds the credential it would
    /// need to create a checkout.
    ///
    /// <para>This is the out-of-scope answer an operator health board needs: a portal that creates
    /// no checkout sessions has no payment path that can be broken, which is a different statement
    /// from "the payment path was measured and is fine".</para>
    /// </summary>
    bool Sells { get; }

    /// <summary>
    /// The configuration key holding this provider's server-side credential — named in operator
    /// findings so a diagnosis carries its own remedy. The VALUE is never read into a log, a
    /// message or a rendered page.
    /// </summary>
    string SecretSettingName { get; }

    /// <summary>
    /// The configuration key holding the signing secret this provider verifies deliveries against.
    /// Named in operator findings for the same reason as <see cref="SecretSettingName"/>.
    /// </summary>
    string WebhookSecretSettingName { get; }

    /// <summary>
    /// The HTTP header a delivery from this provider carries its signature in
    /// (<c>Stripe-Signature</c> for Stripe). The generic webhook inbox stores every header
    /// verbatim; this names the one that authorizes the delivery.
    /// </summary>
    string DeliverySignatureHeader { get; }

    /// <summary>
    /// Creates a hosted checkout — one-off when <see cref="PaymentCheckoutRequest.Recurrence"/> is
    /// null, recurring when it is not. Cold; errors carry a viewer-language message.
    ///
    /// <para>The provider is told everything and decides nothing about the domain: it receives a
    /// reference, a product name, an amount, two return URLs and a metadata map, and it knows
    /// nothing of plugins, plans or covers. Where the buyer returns to is the CALLER's policy, so a
    /// moved checkout surface can never leave a payment provider redirecting at a dead URL.</para>
    /// </summary>
    IObservable<PaymentCheckout> CreateCheckout(PaymentCheckoutRequest request);

    /// <summary>
    /// Asks the provider to stop renewing a subscription AT THE END of the period already paid
    /// for, never immediately: the buyer paid for this period, so this period is theirs. The
    /// provider then reports the actual ending as a delivery
    /// (<see cref="PaymentSubscriptionChange.Ended"/>), and THAT is what files the cancellation —
    /// the portal never revokes access on its own guess about what the processor did.
    ///
    /// <para>Cold; emits once on success.</para>
    /// </summary>
    IObservable<Unit> CancelAtPeriodEnd(string subscriptionId);

    /// <summary>
    /// Reads one stored webhook delivery: VERIFIES its signature and PARSES what it is about, in
    /// one answer.
    ///
    /// <para>🚨 The two halves are returned together on purpose. A caller has to be able to say
    /// "this delivery did not authenticate AND it names a paid order", because an unauthenticated
    /// delivery that names consequential work is retained as evidence while junk is discarded — the
    /// distinction that keeps "someone paid and was not served" from looking exactly like "the
    /// processor never called" (MeshWeaver.Plugins#1069). Pure: the secret comes from the
    /// implementation's configuration, the payload and the clock from the caller.</para>
    /// </summary>
    /// <param name="signatureHeader">The delivery's <see cref="DeliverySignatureHeader"/> value.</param>
    /// <param name="body">The raw body, exactly as stored.</param>
    /// <param name="now">The instant to bound the delivery's timestamp against.</param>
    PaymentDelivery ReadDelivery(string? signatureHeader, string body, DateTimeOffset now);

    /// <summary>
    /// The provider's own account of whether it can DELIVER events back to this portal — the half
    /// of the payment path that lives at the processor and is invisible from the mesh.
    ///
    /// <para>A checkout can complete at the processor and nothing ever call the mesh: the order
    /// stays unpaid-looking, both delivery containers stay empty, and every surface reports health.
    /// This is what sees that. Cold; it never faults — a read that failed comes back as
    /// <see cref="PaymentDeliveryPath.ReadProblem"/>, because "I could not look" must stay
    /// distinguishable from "there is nothing there".</para>
    ///
    /// <para>It reports what the provider HAS, and never what the caller expected: comparing the
    /// account's endpoints against this portal's own hook URL is the caller's decision, so a
    /// provider cannot quietly answer the question it finds easier.</para>
    /// </summary>
    IObservable<PaymentDeliveryPath> DescribeDeliveryPath();
}
