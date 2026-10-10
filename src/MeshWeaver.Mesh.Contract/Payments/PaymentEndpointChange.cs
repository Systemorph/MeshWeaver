using System.Collections.Immutable;
using System.Text.Json.Serialization;
using MeshWeaver.Data;

namespace MeshWeaver.Payments;

/// <summary>
/// What a governed run of <see cref="IPaymentProvider.EnsureDeliveryEndpoint"/> or
/// <see cref="IPaymentProvider.RemoveDeliveryEndpoint"/> came to. An OPEN vocabulary of string
/// constants (policy <c>open-vocabulary-string-constants</c>): a provider may answer a word of its
/// own, and a caller that does not know a word reports it verbatim rather than mapping it to one of
/// these.
/// </summary>
public static class PaymentEndpointOutcome
{
    /// <summary>The provider does not manage its own delivery endpoints (the contract default).</summary>
    public const string NotSupported = "NotSupported";

    /// <summary>
    /// A precondition failed, or a call failed BEFORE any write was sent, and nothing was written;
    /// <see cref="PaymentEndpointChange.Refusal"/> names it. Never the answer to a write call whose
    /// fate is unknown — that is <see cref="Indeterminate"/>.
    /// </summary>
    public const string Refused = "Refused";

    /// <summary>
    /// A write call (create, update, delete) was sent and did not come back with an answer — a
    /// timeout, a dropped connection, a 5xx — so the provider MAY have applied it. Nothing is known
    /// either way, and the caller must not report it as "nothing written". Reconcile by running
    /// again: a dry run reads the account's listing, which is the truth, and a retried create is
    /// made safe by <see cref="PaymentEndpointRequest.IdempotencyKey"/>.
    /// <see cref="PaymentEndpointChange.Refusal"/> names the call that failed.
    /// </summary>
    public const string Indeterminate = "Indeterminate";

    /// <summary>Dry run: no endpoint exists for this URL, and an executed run would create one.</summary>
    public const string WouldCreate = "WouldCreate";

    /// <summary>An endpoint was created for this URL. Only this outcome discloses a signing secret.</summary>
    public const string Created = "Created";

    /// <summary>Dry run: the endpoint for this URL lacks required events, and an executed run would add them.</summary>
    public const string WouldAddEvents = "WouldAddEvents";

    /// <summary>The missing events were added to the existing endpoint; its signing secret is unchanged.</summary>
    public const string EventsAdded = "EventsAdded";

    /// <summary>The endpoint for this URL already carries every required event; nothing was written.</summary>
    public const string AlreadyComplete = "AlreadyComplete";

    /// <summary>
    /// An endpoint this instance created exists, but its signing secret can no longer be recovered
    /// (the provider's idempotent replay window has passed). The way out is
    /// <see cref="IPaymentProvider.RemoveDeliveryEndpoint"/> followed by a fresh ensure.
    /// </summary>
    public const string SecretNotRecovered = "SecretNotRecovered";

    /// <summary>Dry run: these endpoints, each created by this instance, would be removed.</summary>
    public const string WouldRemove = "WouldRemove";

    /// <summary>The endpoints this instance created were removed.</summary>
    public const string Removed = "Removed";

    /// <summary>The account lists no endpoint this instance created; nothing was removed.</summary>
    public const string NothingToRemove = "NothingToRemove";
}

/// <summary>
/// A governed request to register, extend or remove THIS instance's own delivery endpoint at the
/// payment provider. Composed by the caller (the Store's maintenance task) from configuration —
/// never from a value typed on a node.
/// </summary>
public sealed record PaymentEndpointRequest
{
    /// <summary>This instance's hook URL — the only URL the provider may create or edit an endpoint for.</summary>
    public required string Url { get; init; }

    /// <summary>
    /// The identity of this instance, written into the endpoint's metadata on create. It is what
    /// lets a later run find its own endpoint, and what <see cref="IPaymentProvider.RemoveDeliveryEndpoint"/>
    /// restricts itself to.
    /// </summary>
    public required string Owner { get; init; }

    /// <summary>The path of the request node, written into the endpoint's metadata on create.</summary>
    public required string RequestPath { get; init; }

    /// <summary>The events the endpoint must carry — the provider's own required set.</summary>
    public ImmutableArray<string> Events { get; init; } = [];

    /// <summary>False (the default) plans and writes nothing; true performs the change.</summary>
    public bool Execute { get; init; }

    /// <summary>
    /// The account mode the requester confirmed (<c>test</c> or <c>live</c>), compared
    /// case-insensitively with the mode the credential operates in. An executed run with a missing
    /// or different confirmation is refused.
    /// </summary>
    public string? ConfirmedMode { get; init; }

    /// <summary>
    /// The idempotency key of the create call, recorded on the request node BEFORE the call is made,
    /// so a run that dies after the call and is retried does not create a second endpoint.
    /// </summary>
    public string? IdempotencyKey { get; init; }

    /// <summary>The endpoint id an earlier run of this request recorded, or null.</summary>
    public string? RecordedEndpointId { get; init; }
}

/// <summary>The answer of a governed endpoint run.</summary>
public sealed record PaymentEndpointChange
{
    /// <summary>One of <see cref="PaymentEndpointOutcome"/>, or a provider's own word.</summary>
    public required string Outcome { get; init; }

    /// <summary>The account mode, as display text ("TEST", "LIVE").</summary>
    public string Mode { get; init; } = "";

    /// <summary>The endpoint the run created, edited or found, or null.</summary>
    public string? EndpointId { get; init; }

    /// <summary>The endpoints a removal touched (or would touch).</summary>
    public ImmutableArray<string> RemovedEndpointIds { get; init; } = [];

    /// <summary>The endpoint's events after the run (or as planned).</summary>
    public ImmutableArray<string> Events { get; init; } = [];

    /// <summary>The events the run added (or would add).</summary>
    public ImmutableArray<string> AddedEvents { get; init; } = [];

    /// <summary>
    /// Why nothing was written, for <see cref="PaymentEndpointOutcome.Refused"/> and
    /// <see cref="PaymentEndpointOutcome.NotSupported"/> — or which write call's fate is unknown, for
    /// <see cref="PaymentEndpointOutcome.Indeterminate"/> — its English plus, where the platform
    /// authored it, the catalog key that renders it in the viewer's language.
    /// </summary>
    public LocalizableText? Refusal { get; init; }

    /// <summary>
    /// The signing secret of a newly created endpoint — set on <see cref="PaymentEndpointOutcome.Created"/>
    /// only.
    ///
    /// <para>🚨 A CREDENTIAL: whoever holds it can forge a completed checkout. The caller shows it
    /// once to the person who ran the task and writes it NOWHERE — not to a node, an activity, a log
    /// line or an exception message. <see cref="ToString"/> is overridden so a record print cannot
    /// leak it, and <see cref="JsonIgnoreAttribute"/> keeps it out of every serialized form — a
    /// change that is persisted, posted or logged as JSON carries no secret.</para>
    /// </summary>
    [JsonIgnore]
    public string? DisclosedSecret { get; init; }

    /// <summary>A print of this change with the secret masked.</summary>
    public override string ToString() =>
        $"PaymentEndpointChange {{ Outcome = {Outcome}, Mode = {Mode}, EndpointId = {EndpointId}, "
        + $"Events = [{string.Join(", ", Events)}], AddedEvents = [{string.Join(", ", AddedEvents)}], "
        + $"RemovedEndpointIds = [{string.Join(", ", RemovedEndpointIds)}], Refusal = {Refusal?.English}, "
        + $"DisclosedSecret = {(DisclosedSecret is null ? "null" : "(redacted)")} }}";
}
