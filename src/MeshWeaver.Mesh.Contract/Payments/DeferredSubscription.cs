using System.Collections.Immutable;
using System.Text.Json.Serialization;
using MeshWeaver.Data;

namespace MeshWeaver.Payments;

/// <summary>
/// Where a deferred subscription stands. An OPEN vocabulary of string constants (policy
/// <c>open-vocabulary-string-constants</c>): a provider may answer a word of its own, and a caller
/// that does not know a word reports it verbatim and never treats it as
/// <see cref="Scheduled"/>.
/// </summary>
public static class DeferredSubscriptionStatus
{
    /// <summary>The provider does not schedule deferred subscriptions (the contract default).</summary>
    public const string NotSupported = "NotSupported";

    /// <summary>
    /// A precondition failed, or a call failed BEFORE any write was sent, and nothing was created;
    /// <see cref="DeferredSubscriptionFact.Refusal"/> names it.
    /// </summary>
    public const string Refused = "Refused";

    /// <summary>
    /// A write was sent and got no answer — a timeout, a dropped connection, a 5xx — so the provider
    /// MAY have created or cancelled the subscription. Nothing is known either way. The caller
    /// reconciles by READING (<see cref="IPaymentMethodSetupProvider.RetrieveDeferredSubscription"/>),
    /// never by creating again.
    /// </summary>
    public const string Indeterminate = "Indeterminate";

    /// <summary>
    /// The subscription exists and bills nothing until
    /// <see cref="DeferredSubscriptionFact.FirstChargeAt"/>. Not a paid period.
    /// </summary>
    public const string Scheduled = "Scheduled";

    /// <summary>The subscription needs the buyer to complete a step before it is scheduled.</summary>
    public const string RequiresAction = "RequiresAction";

    /// <summary>The first paid renewal has happened; the subscription now bills on its cadence.</summary>
    public const string Active = "Active";

    /// <summary>A charge failed and the processor is retrying. The subscription is not ended.</summary>
    public const string PastDue = "PastDue";

    /// <summary>The subscription will not bill again. Future billing is off, verified by the provider.</summary>
    public const string Cancelled = "Cancelled";
}

/// <summary>
/// Everything a provider must be TOLD to schedule ONE renewal that charges nothing today and starts
/// billing at an exact, accepted instant.
///
/// <para>🚨 <b>Zero is due today by construction.</b> There is no field here through which a caller
/// could ask for a charge now, a setup fee, a one-time item or a proration. A provider that cannot
/// create the subscription with nothing due refuses; it never charges and refunds.</para>
///
/// <para>🚨 <b>The terms are immutable and exact.</b> <see cref="FirstChargeAt"/>,
/// <see cref="RecurringTotal"/>, <see cref="Currency"/> and <see cref="Cadence"/> are the values the
/// buyer accepted, bound by <see cref="TermsHash"/>. A retry sends the same values; a provider never
/// moves the date, omits it as a fallback, or substitutes its catalogue's current price.</para>
/// </summary>
public sealed record DeferredSubscriptionRequest
{
    /// <summary>The amount due at creation. Always zero; stated so a reader does not have to infer it.</summary>
    public const decimal DueToday = 0m;

    /// <summary>The claim that owns this subscription. Only that claim may create or own it.</summary>
    public required string ClaimReference { get; init; }

    /// <summary>The authenticated viewer who accepted the renewal.</summary>
    public required string Buyer { get; init; }

    /// <summary>The verified setup that authorizes off-session charges for this renewal.</summary>
    public required string SetupId { get; init; }

    /// <summary>The processor customer to bill.</summary>
    public required string CustomerId { get; init; }

    /// <summary>The saved method to bill.</summary>
    public required string PaymentMethodId { get; init; }

    /// <summary>The plan the renewal continues.</summary>
    public required string PlanTier { get; init; }

    /// <summary>The caller's cadence word (<c>monthly</c>, <c>annual</c>); the provider maps it onto its own interval.</summary>
    public required string Cadence { get; init; }

    /// <summary>ISO-4217, any case.</summary>
    public required string Currency { get; init; }

    /// <summary>The accepted recurring total per period, TAX-INCLUSIVE, in the currency's major unit.</summary>
    public required decimal RecurringTotal { get; init; }

    /// <summary>
    /// The exact instant of the first charge — the end of the benefit the buyer already holds.
    /// Nothing is billed before it.
    /// </summary>
    public required DateTimeOffset FirstChargeAt { get; init; }

    /// <summary>The buyer's consent to THIS recurring renewal. Always required; kind <see cref="PaymentConsentKind.RecurringRenewal"/>.</summary>
    public required PaymentConsentReference RenewalConsent { get; init; }

    /// <summary>The hash of the quoted terms (dates, total, cadence, selected features) the consent was given against.</summary>
    public required string TermsHash { get; init; }

    /// <summary>
    /// The ONE key of the creation step, persisted by the caller BEFORE the call. A retry with the
    /// same key reaches the same subscription.
    /// </summary>
    public required string IdempotencyKey { get; init; }

    /// <summary>The account mode the caller expects (<c>test</c> or <c>live</c>); a mismatch is refused.</summary>
    public string? ExpectedMode { get; init; }

    /// <summary>Further facts the verified deliveries must hand back, verbatim. Keyed by <see cref="PaymentMetadata"/>.</summary>
    public ImmutableDictionary<string, string> Metadata { get; init; } =
        ImmutableDictionary<string, string>.Empty;

    /// <summary>
    /// The reasons this request must not be sent — empty when it may. Pure: the clock comes from
    /// the caller.
    /// </summary>
    /// <param name="now">The instant to compare <see cref="FirstChargeAt"/> against.</param>
    /// <returns>Words from <see cref="PaymentRequestProblem"/>.</returns>
    public ImmutableArray<string> Problems(DateTimeOffset now)
    {
        var problems = ImmutableArray.CreateBuilder<string>();
        if (string.IsNullOrWhiteSpace(ClaimReference))
            problems.Add(PaymentRequestProblem.MissingReference);
        if (string.IsNullOrWhiteSpace(Buyer))
            problems.Add(PaymentRequestProblem.MissingBuyer);
        if (string.IsNullOrWhiteSpace(PlanTier) || string.IsNullOrWhiteSpace(Cadence))
            problems.Add(PaymentRequestProblem.MissingPlanTerms);
        if (string.IsNullOrWhiteSpace(Currency))
            problems.Add(PaymentRequestProblem.MissingCurrency);
        if (string.IsNullOrWhiteSpace(IdempotencyKey))
            problems.Add(PaymentRequestProblem.MissingIdempotencyKey);
        if (string.IsNullOrWhiteSpace(SetupId) || string.IsNullOrWhiteSpace(CustomerId)
                                               || string.IsNullOrWhiteSpace(PaymentMethodId))
            problems.Add(PaymentRequestProblem.MissingPaymentMethod);
        if (RenewalConsent is null || !RenewalConsent.Is(PaymentConsentKind.RecurringRenewal))
            problems.Add(PaymentRequestProblem.MissingRenewalConsent);
        else if (string.IsNullOrWhiteSpace(TermsHash)
                 || !string.Equals(RenewalConsent.TermsHash, TermsHash, StringComparison.Ordinal))
            problems.Add(PaymentRequestProblem.ConsentTermsMismatch);
        if (RecurringTotal <= 0m)
            problems.Add(PaymentRequestProblem.NonPositiveRecurringTotal);
        if (FirstChargeAt <= now)
            problems.Add(PaymentRequestProblem.FirstChargeNotInFuture);
        return problems.ToImmutable();
    }

    /// <summary>
    /// <see cref="Metadata"/> plus the facts every deferred subscription stamps — buyer, claim,
    /// plan, cadence, terms hash, the setup that authorized it — under <see cref="PaymentMetadata"/>'s
    /// keys. The renewal deliveries that arrive months later are read with the same keys. Pure.
    /// </summary>
    /// <returns>The metadata a provider stamps on the subscription.</returns>
    public ImmutableDictionary<string, string> StampedMetadata() =>
        (Metadata ?? ImmutableDictionary<string, string>.Empty)
        .SetItem(PaymentMetadata.Buyer, Buyer)
        .SetItem(PaymentMetadata.ClaimReference, ClaimReference)
        .SetItem(PaymentMetadata.PlanTier, PlanTier)
        .SetItem(PaymentMetadata.Cadence, Cadence)
        .SetItem(PaymentMetadata.TermsHash, TermsHash)
        .SetItem(PaymentMetadata.SetupReference, SetupId)
        .SetItem(PaymentMetadata.IntendedUse, PaymentMethodIntendedUse.AuthorizedRenewal);
}

/// <summary>
/// The provider's VERIFIED account of one deferred subscription — from the creation answer, an
/// authenticated delivery, or a server-side read.
///
/// <para>🚨 <b>A scheduled subscription is not a paid period.</b> Creating one normally produces an
/// initial invoice of zero. That invoice pays for nothing: it never fulfils an order and never
/// extends a plan. Paid-period accounting starts with the first VERIFIED paid renewal, which
/// arrives as an ordinary <see cref="PaymentSubscriptionEvent"/> whose
/// <see cref="PaymentSubscriptionEvent.IsPaidRenewal"/> is true.</para>
///
/// <para>An OUTCOME, never a fault: a refusal and an unknown fate are statuses
/// (<see cref="DeferredSubscriptionStatus.Refused"/>, <see cref="DeferredSubscriptionStatus.Indeterminate"/>).</para>
/// </summary>
public sealed record DeferredSubscriptionFact
{
    /// <summary>One of <see cref="DeferredSubscriptionStatus"/>, or the provider's own word.</summary>
    public required string Status { get; init; }

    /// <summary>The processor's subscription handle, when one exists.</summary>
    public string? SubscriptionId { get; init; }

    /// <summary>The processor customer it bills.</summary>
    public string? CustomerId { get; init; }

    /// <summary>The saved method it bills.</summary>
    public string? PaymentMethodId { get; init; }

    /// <summary>The plan, as the processor holds it.</summary>
    public string? PlanTier { get; init; }

    /// <summary>The cadence, in the caller's word.</summary>
    public string? Cadence { get; init; }

    /// <summary>The currency, as the processor holds it.</summary>
    public string? Currency { get; init; }

    /// <summary>The recurring total per period, tax-inclusive, as the processor holds it.</summary>
    public decimal? RecurringTotal { get; init; }

    /// <summary>The instant the processor will first charge.</summary>
    public DateTimeOffset? FirstChargeAt { get; init; }

    /// <summary>The processor's handle for the initial invoice, when it created one.</summary>
    public string? InitialInvoiceId { get; init; }

    /// <summary>What the initial invoice asks for. Zero for a correct deferred subscription; null when the processor has not said.</summary>
    public decimal? InitialInvoiceAmountDue { get; init; }

    /// <summary>What has been CAPTURED for this subscription so far. Zero until the first paid renewal; null when the processor has not said.</summary>
    public decimal? AmountCharged { get; init; }

    /// <summary>A setup the processor still wants the buyer to complete for this subscription, when there is one.</summary>
    public string? PendingSetupId { get; init; }

    /// <summary>The account mode the subscription lives in (<c>test</c>, <c>live</c>).</summary>
    public string Mode { get; init; } = "";

    /// <summary>The provider account it lives in, when the provider names it.</summary>
    public string? Account { get; init; }

    /// <summary>The provider's id for the event that reported this, when it came from a delivery — the replay key.</summary>
    public string? EventId { get; init; }

    /// <summary>
    /// Why nothing was created (<see cref="DeferredSubscriptionStatus.Refused"/>,
    /// <see cref="DeferredSubscriptionStatus.NotSupported"/>) or which call's fate is unknown
    /// (<see cref="DeferredSubscriptionStatus.Indeterminate"/>).
    /// </summary>
    public LocalizableText? Refusal { get; init; }

    /// <summary>The subscription's metadata, verbatim, as stamped by <see cref="DeferredSubscriptionRequest.StampedMetadata"/>.</summary>
    public ImmutableDictionary<string, string> Metadata { get; init; } =
        ImmutableDictionary<string, string>.Empty;

    /// <summary>The viewer whose renewal it is.</summary>
    [JsonIgnore]
    public string? Buyer => Meta(PaymentMetadata.Buyer);

    /// <summary>The claim that owns it.</summary>
    [JsonIgnore]
    public string? ClaimReference => Meta(PaymentMetadata.ClaimReference);

    /// <summary>The terms hash the buyer accepted.</summary>
    [JsonIgnore]
    public string? TermsHash => Meta(PaymentMetadata.TermsHash);

    /// <summary>The verified setup that authorized off-session billing for it, as stamped.</summary>
    [JsonIgnore]
    public string? SetupReference => Meta(PaymentMetadata.SetupReference);

    /// <summary>Whether the subscription exists and is waiting for its first charge.</summary>
    [JsonIgnore]
    public bool IsScheduled => string.Equals(Status, DeferredSubscriptionStatus.Scheduled, StringComparison.Ordinal);

    /// <summary>
    /// Whether zero-due is PROVEN from the processor's own numbers: the initial invoice asks for
    /// exactly zero and nothing has been captured. A number the processor has not reported is not a
    /// zero — an unknown amount proves nothing.
    /// </summary>
    [JsonIgnore]
    public bool IsZeroDueToday => InitialInvoiceAmountDue == 0m && AmountCharged == 0m;

    /// <summary>
    /// Whether this fact names a claim — i.e. work the portal would act on. The predicate
    /// <see cref="PaymentDelivery.NamesWork"/> uses for a deferred-subscription delivery.
    /// </summary>
    [JsonIgnore]
    public bool NamesWork => ClaimReference is not null;

    /// <summary>
    /// Everything about this fact that does NOT match the request it is offered for — empty when it
    /// matches. Pure.
    ///
    /// <para>Compared exactly: the claim, the buyer, the terms hash, the setup that authorized it, the customer, the method, the
    /// plan, the cadence, the currency (case-insensitively), the recurring total, the first-charge
    /// instant, the account mode (when the request stated one) — and zero-due, which must be
    /// PROVEN (<see cref="IsZeroDueToday"/>), not merely unreported.</para>
    /// </summary>
    /// <param name="request">The request the caller sent.</param>
    /// <returns>The names of the mismatching facts.</returns>
    public ImmutableArray<string> Mismatches(DeferredSubscriptionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var mismatches = ImmutableArray.CreateBuilder<string>();
        if (!string.Equals(ClaimReference, request.ClaimReference, StringComparison.Ordinal))
            mismatches.Add(nameof(ClaimReference));
        if (!string.Equals(Buyer, request.Buyer, StringComparison.Ordinal))
            mismatches.Add(nameof(Buyer));
        if (!string.Equals(TermsHash, request.TermsHash, StringComparison.Ordinal))
            mismatches.Add(nameof(TermsHash));
        if (!string.Equals(SetupReference, request.SetupId, StringComparison.Ordinal))
            mismatches.Add(nameof(SetupReference));
        if (!string.Equals(CustomerId, request.CustomerId, StringComparison.Ordinal))
            mismatches.Add(nameof(CustomerId));
        if (!string.Equals(PaymentMethodId, request.PaymentMethodId, StringComparison.Ordinal))
            mismatches.Add(nameof(PaymentMethodId));
        if (!string.Equals(PlanTier, request.PlanTier, StringComparison.Ordinal))
            mismatches.Add(nameof(PlanTier));
        if (!string.Equals(Cadence, request.Cadence, StringComparison.Ordinal))
            mismatches.Add(nameof(Cadence));
        if (!string.Equals(Currency, request.Currency, StringComparison.OrdinalIgnoreCase))
            mismatches.Add(nameof(Currency));
        if (RecurringTotal != request.RecurringTotal)
            mismatches.Add(nameof(RecurringTotal));
        if (FirstChargeAt != request.FirstChargeAt)
            mismatches.Add(nameof(FirstChargeAt));
        if (!string.IsNullOrWhiteSpace(request.ExpectedMode)
            && !string.Equals(Mode, request.ExpectedMode, StringComparison.OrdinalIgnoreCase))
            mismatches.Add(nameof(Mode));
        if (!IsZeroDueToday)
            mismatches.Add(nameof(IsZeroDueToday));
        return mismatches.ToImmutable();
    }

    /// <summary>
    /// Whether this fact SATISFIES the request: the subscription is scheduled, it names a
    /// subscription, and nothing mismatches. The one predicate a caller may report "renewal
    /// scheduled" on. Pure.
    /// </summary>
    /// <param name="request">The request the caller sent.</param>
    /// <returns>True only for a scheduled, zero-due subscription that is this request's own.</returns>
    public bool Satisfies(DeferredSubscriptionRequest request) =>
        IsScheduled && !string.IsNullOrWhiteSpace(SubscriptionId) && Mismatches(request).IsEmpty;

    /// <summary>One metadata value, or null when absent or blank. Pure.</summary>
    /// <param name="key">The metadata key.</param>
    /// <returns>The value, or null.</returns>
    public string? Meta(string key) =>
        Metadata is not null && Metadata.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : null;
}
