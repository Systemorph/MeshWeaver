using System.Reactive;
using System.Reactive.Linq;

namespace MeshWeaver.Payments;

/// <summary>
/// The OPTIONAL capability beside <see cref="IPaymentProvider"/>: save a payment method without
/// taking a payment, and — only on a separate, explicit renewal consent — schedule one renewal that
/// charges nothing today.
///
/// <para><b>Why a second interface and not more members on the first.</b> A setup is not a checkout.
/// <see cref="IPaymentProvider.CreateCheckout"/> models an amount and an optional recurrence, and
/// its completed delivery fulfils an order; expressing "save only" there as an amount of zero would
/// make a finished setup indistinguishable from a paid purchase. Keeping the capability apart also
/// keeps it optional twice over: a portal may mount a provider that sells and cannot save methods,
/// and every existing provider keeps compiling untouched.</para>
///
/// <para>🚨 <b>Absence is a modelled state.</b> Callers resolve
/// <see cref="HubPaymentExtensions.PaymentMethodSetupProvider"/> (nullable by construction) and
/// answer "this site cannot save payment methods" explicitly. They never fall back to charging, and
/// never silently waive a setup the offer requires.</para>
///
/// <para>🚨 <b>Who verifies what.</b> The PROVIDER owns signature verification: a setup or renewal
/// fact reaches the portal only through <see cref="IPaymentProvider.ReadDelivery"/> — which reports
/// <see cref="PaymentDelivery.Authentic"/> together with <see cref="PaymentDelivery.Setup"/> or
/// <see cref="PaymentDelivery.DeferredSubscription"/> — or through a server-side read here
/// (<see cref="RetrieveSetup"/>, <see cref="RetrieveDeferredSubscription"/>). The CALLER owns
/// everything after that: matching the fact to its own request
/// (<see cref="PaymentMethodSetupFact.Satisfies"/>, <see cref="DeferredSubscriptionFact.Satisfies"/>),
/// replay (one outcome per <c>EventId</c>), and the domain consequence. A browser returning to a
/// success URL is navigation, never evidence.</para>
///
/// <para>🚨 <b>Reactive end to end, and every returned observable is COLD.</b> Nothing here returns
/// a <c>Task</c>; the request is made on <c>Subscribe</c>. An implementation bridges its HTTP leaf
/// through the mesh's bounded <c>IIoPool</c>.</para>
/// </summary>
public interface IPaymentMethodSetupProvider
{
    /// <summary>What this provider is called, for operator-facing prose. A product name, not a translated one.</summary>
    string DisplayName { get; }

    /// <summary>
    /// The reason a payment method cannot be saved on this portal right now — <c>null</c> when one
    /// can. Resolved in the VIEWER's language by the implementation, because it is shown to a buyer.
    /// </summary>
    string? SetupUnavailable { get; }

    /// <summary>
    /// What the provider's account supports for one intended use and currency: which method kinds
    /// are configured, whether each is reusable for that use, and which handoffs exist. Cold; emits
    /// once.
    ///
    /// <para>🚨 Purpose-specific on purpose. A wallet that can pay is not thereby a stored method
    /// for a customer-present purchase; a kind the provider does not list reads as
    /// <see cref="PaymentMethodReuse.Unverified"/> and is never assumed reusable. A caller must not
    /// select a renewal merely to make a wallet reusable.</para>
    /// </summary>
    /// <param name="intendedUse">One of <see cref="PaymentMethodIntendedUse"/>, or a module's own word.</param>
    /// <param name="currency">ISO-4217, any case.</param>
    /// <returns>A cold observable emitting the capabilities.</returns>
    IObservable<PaymentMethodSetupCapabilities> DescribeSetup(string intendedUse, string currency);

    /// <summary>
    /// Creates a setup session — never a charge, an invoice or a subscription. Cold; errors carry a
    /// viewer-language message.
    ///
    /// <para>The provider requests off-session permission from its processor if and only if
    /// <see cref="PaymentMethodSetupRequest.AuthorizesOffSession"/> is true, stamps
    /// <see cref="PaymentMethodSetupRequest.StampedMetadata"/>, and uses
    /// <see cref="PaymentMethodSetupRequest.IdempotencyKey"/> so a retried request reaches the same
    /// setup. A request with <see cref="PaymentMethodSetupRequest.Problems"/> is refused before any
    /// call is made.</para>
    /// </summary>
    /// <param name="request">The setup to open.</param>
    /// <returns>A cold observable emitting the session.</returns>
    IObservable<PaymentMethodSetupSession> CreateSetup(PaymentMethodSetupRequest request);

    /// <summary>
    /// Reads one setup's current state from the processor — the reconciliation path for a return
    /// that arrived before its delivery, a missed delivery, or a restart. Cold; emits once.
    /// </summary>
    /// <param name="setupId">The provider's setup reference.</param>
    /// <returns>A cold observable emitting the verified fact.</returns>
    IObservable<PaymentMethodSetupFact> RetrieveSetup(string setupId);

    /// <summary>
    /// Cancels a setup the buyer did not finish, so no method is saved by a late completion. Cold;
    /// emits the fact after the cancel. The default reads the setup and changes nothing — a
    /// provider whose unfinished setups simply expire need not override it, and a caller reads the
    /// returned status rather than assuming <see cref="PaymentSetupStatus.Cancelled"/>.
    /// </summary>
    /// <param name="setupId">The provider's setup reference.</param>
    /// <returns>A cold observable emitting the fact as it stands after the call.</returns>
    IObservable<PaymentMethodSetupFact> CancelSetup(string setupId) => RetrieveSetup(setupId);

    /// <summary>
    /// Whether this provider can hand an open, client-rendered setup's one-time secret to its buyer's
    /// page (<see cref="RevealClientSecret"/>). Defaults to <c>false</c>: an embedded form is then
    /// not offered, and a hosted setup is the only handoff.
    /// </summary>
    bool RevealsClientSecret => false;

    /// <summary>
    /// The one-time client secret of an open, client-rendered setup — for the embedded form
    /// (<c>PaymentSetupFormControl</c>) running in the BUYER'S own session. Cold; emits once.
    ///
    /// <para>🚨 <b>The provider answers only the setup's own buyer.</b> It reads the setup server-side
    /// and refuses unless its stamped buyer (<see cref="PaymentMetadata.Buyer"/>) is
    /// <paramref name="buyer"/>, and unless the setup can still be completed. The caller passes the
    /// signed-in identity of the session the form renders in — never a value from the page. The
    /// answer (<see cref="PaymentSetupClientSecret"/>) is never written to a node, an activity, a log
    /// line, a URL, a cache or browser storage; it goes to that one browser and into the provider's
    /// own script.</para>
    ///
    /// <para>The default refuses with a <see cref="NotSupportedException"/> — a contract violation by
    /// a caller that did not ask <see cref="RevealsClientSecret"/> first, and therefore not
    /// localized.</para>
    /// </summary>
    /// <param name="setupId">The provider's setup reference.</param>
    /// <param name="buyer">The signed-in identity of the session the form renders in.</param>
    /// <returns>A cold observable emitting the secret once.</returns>
    IObservable<PaymentSetupClientSecret> RevealClientSecret(string setupId, string buyer) =>
        Observable.Throw<PaymentSetupClientSecret>(new NotSupportedException(
            $"Contract violation: {DisplayName} does not hand out setup client secrets "
            + $"({nameof(RevealsClientSecret)} is false) — check it before calling {nameof(RevealClientSecret)}."));

    /// <summary>
    /// Whether this provider can remove a saved method (<see cref="RemovePaymentMethod"/>). Defaults
    /// to <c>false</c>; a caller offers "Remove" only when this is true.
    /// </summary>
    bool RemovesPaymentMethods => false;

    /// <summary>
    /// Detaches a saved method from its customer. Cold; emits once on success.
    ///
    /// <para>Removing a method cancels nothing else: a renewal that billed it is cancelled or given
    /// another method by its own explicit call, and access already granted stays granted. The
    /// default refuses with a <see cref="NotSupportedException"/> — a contract violation by a caller
    /// that did not ask <see cref="RemovesPaymentMethods"/> first, and therefore not localized.</para>
    /// </summary>
    /// <param name="customerId">The processor customer.</param>
    /// <param name="paymentMethodId">The saved method.</param>
    /// <returns>A cold observable emitting once when the method is detached.</returns>
    IObservable<Unit> RemovePaymentMethod(string customerId, string paymentMethodId) =>
        Observable.Throw<Unit>(new NotSupportedException(
            $"Contract violation: {DisplayName} does not remove payment methods "
            + $"({nameof(RemovesPaymentMethods)} is false) — check it before calling {nameof(RemovePaymentMethod)}."));

    /// <summary>
    /// Whether this provider can schedule a deferred subscription
    /// (<see cref="CreateDeferredSubscription"/>). Defaults to <c>false</c>: a site whose provider
    /// saves methods but cannot schedule renewals offers the baseline with the renewal visibly
    /// unavailable.
    /// </summary>
    bool SupportsDeferredSubscription => false;

    /// <summary>
    /// Schedules ONE renewal with nothing due today and the first charge at the exact accepted
    /// instant. Cold; emits once and NEVER faults: a failed precondition, or a call that failed
    /// before any write was sent, is <see cref="DeferredSubscriptionStatus.Refused"/>; a write that
    /// was sent and got no answer is <see cref="DeferredSubscriptionStatus.Indeterminate"/> — the
    /// caller then reconciles with <see cref="RetrieveDeferredSubscription"/> and never creates
    /// again on a guess.
    ///
    /// <para>The provider refuses a request with <see cref="DeferredSubscriptionRequest.Problems"/>,
    /// a setup that did not authorize off-session use, and any account state that would make
    /// today's amount positive or change the accepted total (a setup fee, a one-time item, a
    /// proration, an unrelated pending item, tax the quote did not include). It never omits the
    /// first-charge instant as a fallback and never charges early.</para>
    ///
    /// <para>The default answers <see cref="DeferredSubscriptionStatus.NotSupported"/>.</para>
    /// </summary>
    /// <param name="request">The renewal to schedule.</param>
    /// <returns>A cold observable emitting the fact.</returns>
    IObservable<DeferredSubscriptionFact> CreateDeferredSubscription(DeferredSubscriptionRequest request) =>
        Observable.Return(new DeferredSubscriptionFact { Status = DeferredSubscriptionStatus.NotSupported });

    /// <summary>
    /// Reads one deferred subscription from the processor — by its handle when the caller recorded
    /// one, else by the claim it was stamped with. The reconciliation path after an
    /// <see cref="DeferredSubscriptionStatus.Indeterminate"/> answer, a missed delivery or a
    /// restart. Cold; emits once and never faults. A subscription that does not exist is
    /// <see cref="DeferredSubscriptionStatus.Refused"/> with no <see cref="DeferredSubscriptionFact.SubscriptionId"/>.
    ///
    /// <para>The default answers <see cref="DeferredSubscriptionStatus.NotSupported"/>.</para>
    /// </summary>
    /// <param name="subscriptionId">The processor's subscription handle, when recorded.</param>
    /// <param name="claimReference">The claim the subscription was stamped with, when the handle is not known.</param>
    /// <returns>A cold observable emitting the fact.</returns>
    IObservable<DeferredSubscriptionFact> RetrieveDeferredSubscription(string? subscriptionId, string? claimReference) =>
        Observable.Return(new DeferredSubscriptionFact { Status = DeferredSubscriptionStatus.NotSupported });

    /// <summary>
    /// Turns OFF the future billing of a deferred subscription and reports what the processor then
    /// holds — the compensation for a claim that could not be completed, and the buyer's "turn off
    /// renewal". Cold; emits once and never faults; a cancel that was sent and got no answer is
    /// <see cref="DeferredSubscriptionStatus.Indeterminate"/>.
    ///
    /// <para>🚨 Only <see cref="DeferredSubscriptionStatus.Cancelled"/> means billing is off. A
    /// caller keeps the compensation open, and visible to an operator, until it reads that word. It
    /// also cancels any <see cref="DeferredSubscriptionFact.PendingSetupId"/>; a processor does not
    /// necessarily cancel an unfinished setup with its subscription. Cancelling the renewal never
    /// revokes the benefit the buyer already holds.</para>
    ///
    /// <para>The default answers <see cref="DeferredSubscriptionStatus.NotSupported"/>.</para>
    /// </summary>
    /// <param name="subscriptionId">The processor's subscription handle.</param>
    /// <returns>A cold observable emitting the fact as it stands after the call.</returns>
    IObservable<DeferredSubscriptionFact> CancelDeferredSubscription(string subscriptionId) =>
        Observable.Return(new DeferredSubscriptionFact { Status = DeferredSubscriptionStatus.NotSupported });
}
