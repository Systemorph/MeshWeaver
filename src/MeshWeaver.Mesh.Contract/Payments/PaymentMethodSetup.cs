using System.Collections.Immutable;
using System.Text.Json.Serialization;

namespace MeshWeaver.Payments;

/// <summary>
/// What a saved payment method is being saved FOR. An OPEN vocabulary of string constants (policy
/// <c>open-vocabulary-string-constants</c>).
///
/// <para>🚨 <b>The two are different authorizations, and neither implies the other.</b> Saving a
/// method so the buyer can pay faster later authorizes no charge at all. Agreeing to a recurring
/// renewal authorizes that renewal's disclosed charges and nothing else. A provider asks its
/// processor for off-session permission only when
/// <see cref="PaymentMethodSetupRequest.AuthorizesOffSession"/> is true — which needs BOTH the
/// renewal use and a separate renewal consent.</para>
/// </summary>
public static class PaymentMethodIntendedUse
{
    /// <summary>
    /// The method is saved for purchases the buyer makes while PRESENT, each one confirmed by the
    /// buyer with its own total. Nothing may be charged to it in the buyer's absence.
    /// </summary>
    public const string CustomerPresentPurchaseOnly = "CustomerPresentPurchaseOnly";

    /// <summary>
    /// The method is ALSO authorized for the scheduled charges of one renewal the buyer explicitly
    /// accepted, in the buyer's absence. Requires a separate renewal consent.
    /// </summary>
    public const string AuthorizedRenewal = "AuthorizedRenewal";
}

/// <summary>The kind of consent a <see cref="PaymentConsentReference"/> records. An OPEN vocabulary.</summary>
public static class PaymentConsentKind
{
    /// <summary>Consent to SAVE a payment method for purchases the buyer confirms.</summary>
    public const string MethodStorage = "MethodStorage";

    /// <summary>Consent to a RECURRING renewal at disclosed terms. Never implied by <see cref="MethodStorage"/>.</summary>
    public const string RecurringRenewal = "RecurringRenewal";
}

/// <summary>How the buyer is handed to the provider to enter a method. An OPEN vocabulary.</summary>
public static class PaymentSetupHandoff
{
    /// <summary>The provider's own hosted page; <see cref="PaymentMethodSetupSession.HostedUrl"/> says where.</summary>
    public const string Hosted = "Hosted";

    /// <summary>
    /// The provider's controls rendered inside the portal page, mounted with
    /// <see cref="PaymentMethodSetupSession.ClientSecret"/>.
    /// </summary>
    public const string ClientRendered = "ClientRendered";
}

/// <summary>Where a payment-method setup stands. An OPEN vocabulary; an unknown word is never success.</summary>
public static class PaymentSetupStatus
{
    /// <summary>Created; the buyer has not finished.</summary>
    public const string Pending = "Pending";

    /// <summary>The buyer must complete a step (a bank challenge) before the method is saved.</summary>
    public const string RequiresAction = "RequiresAction";

    /// <summary>The processor confirmed the method is saved. The ONLY word that satisfies a setup.</summary>
    public const string Succeeded = "Succeeded";

    /// <summary>The processor refused the method. Nothing was saved and nothing was charged.</summary>
    public const string Failed = "Failed";

    /// <summary>The buyer or the portal cancelled the setup.</summary>
    public const string Cancelled = "Cancelled";

    /// <summary>The setup session ran out before the buyer finished.</summary>
    public const string Expired = "Expired";
}

/// <summary>A kind of payment method a buyer can choose. An OPEN vocabulary.</summary>
public static class PaymentMethodKind
{
    /// <summary>A card entered in the provider's own card fields.</summary>
    public const string Card = "Card";

    /// <summary>Apple Pay.</summary>
    public const string ApplePay = "ApplePay";

    /// <summary>Google Pay.</summary>
    public const string GooglePay = "GooglePay";
}

/// <summary>
/// Whether a saved method of one kind can be REUSED for one intended use. An OPEN vocabulary.
///
/// <para>The distinction is why this exists at all: a wallet can be genuinely available and still
/// not reusable for a customer-present purchase, in which case the honest answer is "this wallet
/// asks you again each time", not a button that pretends to have saved it.</para>
/// </summary>
public static class PaymentMethodReuse
{
    /// <summary>A method of this kind, once saved, can be used again for this intended use.</summary>
    public const string Reusable = "Reusable";

    /// <summary>
    /// The kind can pay, but each purchase needs the buyer's fresh authorization in the wallet;
    /// saving it does not make a stored method for this use.
    /// </summary>
    public const string FreshAuthorizationEachPurchase = "FreshAuthorizationEachPurchase";

    /// <summary>The kind cannot be used for this intended use on this account.</summary>
    public const string Unsupported = "Unsupported";

    /// <summary>
    /// Not established for this account, currency and integration. Treated as NOT reusable until a
    /// test of the actual integration says otherwise; the answer for every kind a provider does not
    /// list.
    /// </summary>
    public const string Unverified = "Unverified";
}

/// <summary>
/// A pointer to ONE recorded consent: which kind, the exact text the buyer saw, and when they
/// accepted it. The consent record itself lives on the caller's own node; this is what travels to
/// the provider and comes back on its verified facts.
///
/// <para>🚨 Storage consent and renewal consent are two references, never one with two meanings. A
/// renewal consent is never inferred from a storage consent, a saved method, a customer id, an
/// earlier purchase, a query parameter or a prefilled checkbox.</para>
/// </summary>
public sealed record PaymentConsentReference
{
    /// <summary>One of <see cref="PaymentConsentKind"/>, or a module's own word.</summary>
    public required string Kind { get; init; }

    /// <summary>The version of the consent text the buyer was shown.</summary>
    public required string TextVersion { get; init; }

    /// <summary>A hash of the exact text shown, so a later reader can prove which words were accepted.</summary>
    public required string TextHash { get; init; }

    /// <summary>When the buyer accepted.</summary>
    public required DateTimeOffset AcceptedAt { get; init; }

    /// <summary>
    /// The hash of the quoted terms this consent was given against (price, dates, selected
    /// features), when it was given against a quote. A consent whose terms hash differs from the
    /// request it travels with was given for something else.
    /// </summary>
    public string? TermsHash { get; init; }

    /// <summary>
    /// Whether this reference is of <paramref name="kind"/> and names its text, its version and
    /// WHEN it was accepted. <c>required</c> does not keep a caller or a deserializer from supplying
    /// <see cref="DateTimeOffset"/>'s default, and a consent with no acceptance instant records no
    /// acceptance at all. Pure.
    /// </summary>
    /// <param name="kind">The consent kind required.</param>
    /// <returns>True when the reference can stand for a consent of that kind.</returns>
    public bool Is(string kind) =>
        string.Equals(Kind, kind, StringComparison.Ordinal)
        && !string.IsNullOrWhiteSpace(TextVersion)
        && !string.IsNullOrWhiteSpace(TextHash)
        && AcceptedAt != default;
}

/// <summary>
/// Why a setup or renewal request must not be sent. An OPEN vocabulary of problem words returned by
/// <see cref="PaymentMethodSetupRequest.Problems"/> and
/// <see cref="DeferredSubscriptionRequest.Problems"/>.
/// </summary>
public static class PaymentRequestProblem
{
    /// <summary>The request carries no storage consent of kind <see cref="PaymentConsentKind.MethodStorage"/>.</summary>
    public const string MissingStorageConsent = "MissingStorageConsent";

    /// <summary>The use is a renewal, but no consent of kind <see cref="PaymentConsentKind.RecurringRenewal"/> travels with it.</summary>
    public const string MissingRenewalConsent = "MissingRenewalConsent";

    /// <summary>A renewal consent travels with a request whose use is not a renewal — the two disagree.</summary>
    public const string RenewalConsentWithoutRenewalUse = "RenewalConsentWithoutRenewalUse";

    /// <summary>A consent was given against different terms than the request states.</summary>
    public const string ConsentTermsMismatch = "ConsentTermsMismatch";

    /// <summary>The request has no idempotency key, so a retry could create a second object.</summary>
    public const string MissingIdempotencyKey = "MissingIdempotencyKey";

    /// <summary>The request names no buyer.</summary>
    public const string MissingBuyer = "MissingBuyer";

    /// <summary>The recurring total is not a positive amount.</summary>
    public const string NonPositiveRecurringTotal = "NonPositiveRecurringTotal";

    /// <summary>The first charge is not in the future.</summary>
    public const string FirstChargeNotInFuture = "FirstChargeNotInFuture";

    /// <summary>The request names no verified setup, customer or method to charge.</summary>
    public const string MissingPaymentMethod = "MissingPaymentMethod";

    /// <summary>
    /// The request carries no caller reference (a setup's <see cref="PaymentMethodSetupRequest.Reference"/>)
    /// or no owning claim (a renewal's <see cref="DeferredSubscriptionRequest.ClaimReference"/>). A blank
    /// one is stamped and then read back as absent, so the created object could never be paired with it.
    /// </summary>
    public const string MissingReference = "MissingReference";

    /// <summary>The request names no currency.</summary>
    public const string MissingCurrency = "MissingCurrency";

    /// <summary>The setup request names no intended use.</summary>
    public const string MissingIntendedUse = "MissingIntendedUse";

    /// <summary>The setup request names no return URL for the buyer.</summary>
    public const string MissingReturnUrl = "MissingReturnUrl";

    /// <summary>The renewal request names no plan tier or no cadence.</summary>
    public const string MissingPlanTerms = "MissingPlanTerms";
}

/// <summary>
/// Everything a provider must be TOLD to save a payment method WITHOUT taking a payment.
///
/// <para>🚨 <b>There is no amount, on purpose.</b> A setup is not a zero-priced checkout: overloading
/// <see cref="PaymentCheckoutRequest"/> with an amount of zero would make a completed setup look
/// like a paid order to fulfilment. This record cannot express a charge, an invoice or a
/// subscription.</para>
///
/// <para>Every value is server-owned. The buyer is the authenticated principal; nothing here is
/// read from a URL, a prefill or a posted form.</para>
/// </summary>
public sealed record PaymentMethodSetupRequest
{
    /// <summary>The caller's own handle for this setup attempt (its node path), for the provider's records.</summary>
    public required string Reference { get; init; }

    /// <summary>The authenticated viewer saving the method.</summary>
    public required string Buyer { get; init; }

    /// <summary>The claim this setup belongs to, when it belongs to one. Handed back on the verified fact.</summary>
    public string? ClaimReference { get; init; }

    /// <summary>The registered site the buyer is on, when the caller binds setups to a site.</summary>
    public string? SiteId { get; init; }

    /// <summary>The currency the buyer will later be shown prices in (ISO-4217). Wallet eligibility depends on it.</summary>
    public required string Currency { get; init; }

    /// <summary>One of <see cref="PaymentMethodIntendedUse"/>, or a module's own word.</summary>
    public required string IntendedUse { get; init; }

    /// <summary>The buyer's consent to SAVE the method. Always required.</summary>
    public required PaymentConsentReference StorageConsent { get; init; }

    /// <summary>
    /// The buyer's SEPARATE consent to a recurring renewal — set only when they explicitly chose
    /// one. Null means no renewal was chosen, and the provider then requests no off-session
    /// permission whatever <see cref="IntendedUse"/> says.
    /// </summary>
    public PaymentConsentReference? RenewalConsent { get; init; }

    /// <summary>The hash of the quoted terms the buyer accepted; handed back on the verified fact.</summary>
    public string? TermsHash { get; init; }

    /// <summary>
    /// A deterministic key for this attempt. A retried or duplicated request with the same key
    /// reaches the SAME setup at the processor, never a second one.
    /// </summary>
    public required string IdempotencyKey { get; init; }

    /// <summary>Where the provider returns a buyer who finished. The CALLER's policy, as for a checkout.</summary>
    public required string ReturnUrl { get; init; }

    /// <summary>Where the provider returns a buyer who walked away, for a hosted handoff.</summary>
    public string? CancelUrl { get; init; }

    /// <summary>One of <see cref="PaymentSetupHandoff"/>; the provider may answer with another it supports.</summary>
    public string Handoff { get; init; } = PaymentSetupHandoff.Hosted;

    /// <summary>The processor customer to attach the method to, when the caller already holds one for this buyer.</summary>
    public string? CustomerId { get; init; }

    /// <summary>Prefills the provider's page; omitted when blank.</summary>
    public string? CustomerEmail { get; init; }

    /// <summary>
    /// The account mode the caller expects (<c>test</c> or <c>live</c>), compared case-insensitively
    /// with the mode the credential operates in. A provider refuses a mismatch rather than save a
    /// real card under a test campaign or the reverse.
    /// </summary>
    public string? ExpectedMode { get; init; }

    /// <summary>Further facts the verified fact must hand back, verbatim. Keyed by <see cref="PaymentMetadata"/>.</summary>
    public ImmutableDictionary<string, string> Metadata { get; init; } =
        ImmutableDictionary<string, string>.Empty;

    /// <summary>
    /// Whether this request authorizes charges in the buyer's ABSENCE: the use is
    /// <see cref="PaymentMethodIntendedUse.AuthorizedRenewal"/> AND a renewal consent of the right
    /// kind travels with it. The ONE predicate a provider turns its off-session request on. Pure.
    /// </summary>
    [JsonIgnore]
    public bool AuthorizesOffSession =>
        string.Equals(IntendedUse, PaymentMethodIntendedUse.AuthorizedRenewal, StringComparison.Ordinal)
        && RenewalConsent is { } consent
        && consent.Is(PaymentConsentKind.RecurringRenewal);

    /// <summary>
    /// The reasons this request must not be sent — empty when it may. Pure.
    ///
    /// <para>The two consents are checked against the use in BOTH directions: a renewal use without
    /// a renewal consent, and a renewal consent on a request whose use is not a renewal. The second
    /// is what stops a changed checkbox from quietly upgrading a customer-present setup.</para>
    /// </summary>
    /// <returns>Words from <see cref="PaymentRequestProblem"/>.</returns>
    public ImmutableArray<string> Problems()
    {
        var problems = ImmutableArray.CreateBuilder<string>();
        if (string.IsNullOrWhiteSpace(Reference))
            problems.Add(PaymentRequestProblem.MissingReference);
        if (string.IsNullOrWhiteSpace(Buyer))
            problems.Add(PaymentRequestProblem.MissingBuyer);
        if (string.IsNullOrWhiteSpace(Currency))
            problems.Add(PaymentRequestProblem.MissingCurrency);
        if (string.IsNullOrWhiteSpace(IntendedUse))
            problems.Add(PaymentRequestProblem.MissingIntendedUse);
        if (string.IsNullOrWhiteSpace(ReturnUrl))
            problems.Add(PaymentRequestProblem.MissingReturnUrl);
        if (string.IsNullOrWhiteSpace(IdempotencyKey))
            problems.Add(PaymentRequestProblem.MissingIdempotencyKey);
        if (StorageConsent is null || !StorageConsent.Is(PaymentConsentKind.MethodStorage))
            problems.Add(PaymentRequestProblem.MissingStorageConsent);

        var renewalUse = string.Equals(IntendedUse, PaymentMethodIntendedUse.AuthorizedRenewal, StringComparison.Ordinal);
        if (renewalUse && !AuthorizesOffSession)
            problems.Add(PaymentRequestProblem.MissingRenewalConsent);
        if (!renewalUse && RenewalConsent is not null)
            problems.Add(PaymentRequestProblem.RenewalConsentWithoutRenewalUse);
        if (RenewalConsent is { TermsHash: { Length: > 0 } accepted }
            && !string.Equals(accepted, TermsHash, StringComparison.Ordinal))
            problems.Add(PaymentRequestProblem.ConsentTermsMismatch);
        return problems.ToImmutable();
    }

    /// <summary>
    /// <see cref="Metadata"/> plus the correlation facts every setup stamps — buyer, setup
    /// reference, intended use and, when set, claim, site and terms hash — under
    /// <see cref="PaymentMetadata"/>'s keys. ONE composition, so the side that stamps and the side
    /// that reads a verified fact months later cannot drift. Pure.
    /// </summary>
    /// <returns>The metadata a provider stamps on the setup and on the customer-facing objects it creates.</returns>
    public ImmutableDictionary<string, string> StampedMetadata()
    {
        var stamped = (Metadata ?? ImmutableDictionary<string, string>.Empty)
            .SetItem(PaymentMetadata.Buyer, Buyer)
            .SetItem(PaymentMetadata.SetupReference, Reference)
            .SetItem(PaymentMetadata.IntendedUse, IntendedUse);
        if (!string.IsNullOrWhiteSpace(ClaimReference))
            stamped = stamped.SetItem(PaymentMetadata.ClaimReference, ClaimReference);
        if (!string.IsNullOrWhiteSpace(SiteId))
            stamped = stamped.SetItem(PaymentMetadata.SiteId, SiteId);
        if (!string.IsNullOrWhiteSpace(TermsHash))
            stamped = stamped.SetItem(PaymentMetadata.TermsHash, TermsHash);
        return stamped;
    }
}

/// <summary>
/// One created setup session: the provider's reference and how the buyer is handed over.
/// </summary>
public sealed record PaymentMethodSetupSession
{
    /// <summary>The provider's reference for the setup — recorded on the caller's attempt so a verified fact can be paired.</summary>
    public required string SetupId { get; init; }

    /// <summary>One of <see cref="PaymentSetupHandoff"/>: how the buyer is actually handed over.</summary>
    public required string Handoff { get; init; }

    /// <summary>Where to send the buyer, for a <see cref="PaymentSetupHandoff.Hosted"/> handoff. Short-lived.</summary>
    public string? HostedUrl { get; init; }

    /// <summary>
    /// The material the provider's in-page controls are mounted with, for a
    /// <see cref="PaymentSetupHandoff.ClientRendered"/> handoff.
    ///
    /// <para>🚨 BUYER-ONLY and short-lived. It is handed to the intended buyer's own page over TLS
    /// and written NOWHERE — not to a node, an activity, a log line, a URL, a cache or browser
    /// storage. <see cref="JsonIgnoreAttribute"/> keeps it out of every serialized form and
    /// <see cref="ToString"/> masks it, so a session that is persisted, posted or printed carries
    /// none.</para>
    /// </summary>
    [JsonIgnore]
    public string? ClientSecret { get; init; }

    /// <summary>The provider's PUBLIC client key the in-page controls need. Public by nature; not a secret.</summary>
    public string? ClientPublicKey { get; init; }

    /// <summary>The processor customer the method will be attached to, when the provider resolved or created one.</summary>
    public string? CustomerId { get; init; }

    /// <summary>The account mode the session was created in (<c>test</c>, <c>live</c>).</summary>
    public string Mode { get; init; } = "";

    /// <summary>When the session stops being usable, when the provider says.</summary>
    public DateTimeOffset? ExpiresAt { get; init; }

    /// <summary>A print of this session with the client secret masked.</summary>
    public override string ToString() =>
        $"PaymentMethodSetupSession {{ SetupId = {SetupId}, Handoff = {Handoff}, HostedUrl = {HostedUrl}, "
        + $"CustomerId = {CustomerId}, Mode = {Mode}, ExpiresAt = {ExpiresAt:O}, "
        + $"ClientSecret = {(ClientSecret is null ? "null" : "(redacted)")} }}";
}

/// <summary>What a buyer may be SHOWN about a saved method: enough to recognise it, nothing that can pay.</summary>
public sealed record PaymentMethodSummary
{
    /// <summary>One of <see cref="PaymentMethodKind"/>, or the provider's own word.</summary>
    public required string Kind { get; init; }

    /// <summary>The card network or wallet brand, as display text.</summary>
    public string? Brand { get; init; }

    /// <summary>The last four digits, when the kind has them.</summary>
    public string? Last4 { get; init; }

    /// <summary>The expiry month (1–12), when the kind has one.</summary>
    public int? ExpiryMonth { get; init; }

    /// <summary>The four-digit expiry year, when the kind has one.</summary>
    public int? ExpiryYear { get; init; }
}

/// <summary>
/// The provider's VERIFIED account of one setup — from an authenticated delivery or a server-side
/// read of the processor, never from a browser return.
///
/// <para>🚨 <b>It is not a payment and it is not an entitlement.</b> A succeeded setup proves a
/// method was saved. It fulfils no order, grants no plan and starts no billing period; a caller
/// that needs one of those does it separately, after its own checks.</para>
/// </summary>
public sealed record PaymentMethodSetupFact
{
    /// <summary>One of <see cref="PaymentSetupStatus"/>, or the provider's own word.</summary>
    public required string Status { get; init; }

    /// <summary>The provider's reference for the setup.</summary>
    public required string SetupId { get; init; }

    /// <summary>The processor customer the method is attached to.</summary>
    public string? CustomerId { get; init; }

    /// <summary>
    /// The processor's handle for the saved method. A provider identifier, not a credential — but
    /// it belongs on the buyer's private record, never on a node other viewers can read.
    /// </summary>
    public string? PaymentMethodId { get; init; }

    /// <summary>What the buyer may be shown about the method.</summary>
    public PaymentMethodSummary? Summary { get; init; }

    /// <summary>
    /// Whether the processor recorded permission to charge this method in the buyer's ABSENCE. A
    /// fact that says so for a request that did not ask (<see cref="PaymentMethodSetupRequest.AuthorizesOffSession"/>)
    /// is a mismatch, never a bonus.
    /// </summary>
    public bool OffSessionAuthorized { get; init; }

    /// <summary>The account mode the setup lives in (<c>test</c>, <c>live</c>).</summary>
    public string Mode { get; init; } = "";

    /// <summary>The provider account the setup lives in, when the provider names it.</summary>
    public string? Account { get; init; }

    /// <summary>The provider's id for the event that reported this, when it came from a delivery — the replay key.</summary>
    public string? EventId { get; init; }

    /// <summary>The metadata the setup carries, verbatim, as stamped by <see cref="PaymentMethodSetupRequest.StampedMetadata"/>.</summary>
    public ImmutableDictionary<string, string> Metadata { get; init; } =
        ImmutableDictionary<string, string>.Empty;

    /// <summary>The viewer the setup was made for.</summary>
    [JsonIgnore]
    public string? Buyer => Meta(PaymentMetadata.Buyer);

    /// <summary>The caller's handle for the setup attempt.</summary>
    [JsonIgnore]
    public string? SetupReference => Meta(PaymentMetadata.SetupReference);

    /// <summary>The claim the setup belongs to.</summary>
    [JsonIgnore]
    public string? ClaimReference => Meta(PaymentMetadata.ClaimReference);

    /// <summary>The terms hash the buyer accepted.</summary>
    [JsonIgnore]
    public string? TermsHash => Meta(PaymentMetadata.TermsHash);

    /// <summary>The registered site the setup was bound to.</summary>
    [JsonIgnore]
    public string? SiteId => Meta(PaymentMetadata.SiteId);

    /// <summary>The intended use the setup was requested for.</summary>
    [JsonIgnore]
    public string? IntendedUse => Meta(PaymentMetadata.IntendedUse);

    /// <summary>Whether the processor confirmed the method is saved. Only <see cref="PaymentSetupStatus.Succeeded"/> is.</summary>
    [JsonIgnore]
    public bool IsSucceeded => string.Equals(Status, PaymentSetupStatus.Succeeded, StringComparison.Ordinal);

    /// <summary>
    /// Whether this fact names a caller's setup attempt or claim — i.e. work the portal would act
    /// on. The predicate <see cref="PaymentDelivery.NamesWork"/> uses for a setup delivery.
    /// </summary>
    [JsonIgnore]
    public bool NamesWork => SetupReference is not null || ClaimReference is not null;

    /// <summary>
    /// Everything about this fact that does NOT match the request it is offered for and the session
    /// that request created — empty when it matches. Pure.
    ///
    /// <para>Compared: the setup id, the buyer, the caller's reference, the claim, the site, the
    /// terms hash, the intended use, the customer (when the session named one), the account mode (when the
    /// request stated one), and off-session permission, which must equal exactly what the request
    /// authorized.</para>
    /// </summary>
    /// <param name="request">The request the caller sent.</param>
    /// <param name="session">The session the provider answered that request with.</param>
    /// <returns>The names of the mismatching facts.</returns>
    public ImmutableArray<string> Mismatches(PaymentMethodSetupRequest request, PaymentMethodSetupSession session)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(session);
        var mismatches = ImmutableArray.CreateBuilder<string>();
        if (!string.Equals(SetupId, session.SetupId, StringComparison.Ordinal))
            mismatches.Add(nameof(SetupId));
        if (!string.Equals(Buyer, request.Buyer, StringComparison.Ordinal))
            mismatches.Add(nameof(Buyer));
        if (!string.Equals(SetupReference, request.Reference, StringComparison.Ordinal))
            mismatches.Add(nameof(SetupReference));
        if (!string.Equals(ClaimReference, Blank(request.ClaimReference), StringComparison.Ordinal))
            mismatches.Add(nameof(ClaimReference));
        if (!string.Equals(SiteId, Blank(request.SiteId), StringComparison.Ordinal))
            mismatches.Add(nameof(SiteId));
        if (!string.Equals(TermsHash, Blank(request.TermsHash), StringComparison.Ordinal))
            mismatches.Add(nameof(TermsHash));
        if (!string.Equals(IntendedUse, request.IntendedUse, StringComparison.Ordinal))
            mismatches.Add(nameof(IntendedUse));
        if (!string.IsNullOrWhiteSpace(session.CustomerId)
            && !string.Equals(CustomerId, session.CustomerId, StringComparison.Ordinal))
            mismatches.Add(nameof(CustomerId));
        if (!string.IsNullOrWhiteSpace(request.ExpectedMode)
            && !string.Equals(Mode, request.ExpectedMode, StringComparison.OrdinalIgnoreCase))
            mismatches.Add(nameof(Mode));
        if (OffSessionAuthorized != request.AuthorizesOffSession)
            mismatches.Add(nameof(OffSessionAuthorized));
        return mismatches.ToImmutable();
    }

    /// <summary>
    /// Whether this fact SATISFIES the request: the setup succeeded, it names the saved method, and
    /// nothing mismatches. The one predicate a caller may proceed on. A success that names no method
    /// proves nothing was saved that could be reused, removed or billed. Pure.
    /// </summary>
    /// <param name="request">The request the caller sent.</param>
    /// <param name="session">The session the provider answered that request with.</param>
    /// <returns>True only for a succeeded setup that is this request's own.</returns>
    public bool Satisfies(PaymentMethodSetupRequest request, PaymentMethodSetupSession session) =>
        IsSucceeded && !string.IsNullOrWhiteSpace(PaymentMethodId) && Mismatches(request, session).IsEmpty;

    /// <summary>One metadata value, or null when absent or blank. Pure.</summary>
    /// <param name="key">The metadata key.</param>
    /// <returns>The value, or null.</returns>
    public string? Meta(string key) =>
        Metadata is not null && Metadata.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : null;

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
}

/// <summary>What one kind of payment method can do for one intended use on this account.</summary>
public sealed record PaymentMethodCapability
{
    /// <summary>One of <see cref="PaymentMethodKind"/>, or the provider's own word.</summary>
    public required string Kind { get; init; }

    /// <summary>One of <see cref="PaymentMethodReuse"/>, or the provider's own word.</summary>
    public required string Reuse { get; init; }

    /// <summary>An operator-facing identifier explaining the answer (a setting that is off, a rule of the scheme). Not viewer text.</summary>
    public string? Detail { get; init; }
}

/// <summary>
/// What the provider's ACCOUNT supports for one intended use and currency — which method kinds are
/// configured, whether each is reusable for that use, and which handoffs exist.
///
/// <para>This is the server's half of the answer. Whether a wallet is available on the buyer's
/// DEVICE is established by the provider's own in-page controls, never guessed from an operating
/// system; a kind listed here as reusable may still not be offered on a given phone.</para>
/// </summary>
public sealed record PaymentMethodSetupCapabilities
{
    /// <summary>The intended use the answer is for.</summary>
    public required string IntendedUse { get; init; }

    /// <summary>The currency the answer is for.</summary>
    public required string Currency { get; init; }

    /// <summary>What each configured method kind can do for this use.</summary>
    public ImmutableArray<PaymentMethodCapability> Methods { get; init; } = [];

    /// <summary>The handoffs the provider can open (<see cref="PaymentSetupHandoff"/>).</summary>
    public ImmutableArray<string> Handoffs { get; init; } = [];

    /// <summary>The account mode the answer was read in (<c>test</c>, <c>live</c>).</summary>
    public string Mode { get; init; } = "";

    /// <summary>
    /// What <paramref name="kind"/> can do for this use: the provider's word for it, or
    /// <see cref="PaymentMethodReuse.Unverified"/> for a kind the provider did not list. An unlisted
    /// kind is never assumed reusable. Pure.
    /// </summary>
    /// <param name="kind">The method kind.</param>
    /// <returns>The reuse word.</returns>
    public string ReuseOf(string kind)
    {
        var methods = Methods.IsDefault ? [] : Methods;
        return methods.FirstOrDefault(m => string.Equals(m.Kind, kind, StringComparison.Ordinal))?.Reuse
               ?? PaymentMethodReuse.Unverified;
    }
}

/// <summary>
/// An open setup's one-time client secret, as <see cref="IPaymentMethodSetupProvider.RevealClientSecret"/>
/// hands it to the buyer's embedded form. 🚨 The secret is <see cref="JsonIgnoreAttribute">never
/// serialized</see> and masked in <see cref="ToString"/>, like
/// <see cref="PaymentMethodSetupSession.ClientSecret"/>: it reaches the buyer's browser through the
/// renderer's own call into the provider's script, and nowhere else.
/// </summary>
public sealed record PaymentSetupClientSecret
{
    /// <summary>The setup the secret completes.</summary>
    public required string SetupId { get; init; }

    /// <summary>The one-time secret. Never serialized (and therefore not <c>required</c>, which System.Text.Json refuses on an ignored member).</summary>
    [JsonIgnore]
    public string Secret { get; init; } = "";

    /// <summary>The provider's publishable key the in-page script loads with. Public by nature.</summary>
    public string? ClientPublicKey { get; init; }

    /// <inheritdoc />
    public override string ToString() =>
        $"PaymentSetupClientSecret {{ SetupId = {SetupId}, ClientPublicKey = {ClientPublicKey}, Secret = (redacted) }}";
}
