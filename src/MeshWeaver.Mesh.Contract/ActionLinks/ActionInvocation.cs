using System.Collections.Immutable;
using System.Text.Json;

namespace MeshWeaver.ActionLinks;

/// <summary>
/// A domain-neutral, versioned DESCRIPTION of something a person can later be offered to do —
/// "claim this coupon", "open this page" — that one module produces and another publishes behind a
/// shareable link.
///
/// <para><b>Why the platform carries it.</b> The module that knows the domain (a store's coupon
/// handler) and the module that publishes links and resumes sign-in (a reusable action-link
/// service) must not depend on each other in both directions. Each depends on this record instead:
/// the domain module emits it, the publisher stores it, and the registered
/// <see cref="IActionHandler"/> for <see cref="HandlerKey"/> interprets it. It lives in the
/// platform for the reason <c>IPaymentProvider</c> does — content compiled inside the mesh has to
/// bind something present in every portal image.</para>
///
/// <para>🚨 <b>It is DATA, never code.</b> An invocation names a handler by key and version and
/// carries that handler's payload as opaque JSON. It holds no delegate, no script, no method name,
/// no hub address and no identity to run as. A person who follows a link can therefore cause
/// exactly what a handler registered on THIS portal is written to do, and nothing a publisher
/// typed.</para>
///
/// <para>🚨 <b>It is not a grant.</b> Holding an invocation, or the link that resolves to one,
/// entitles nobody to anything. The handler decides from the authenticated
/// <see cref="ActionRecipientContext"/>, afresh, every time.</para>
///
/// <para>Init-only properties rather than a positional record, deliberately: a new optional field is
/// then additive for every module compiled against an earlier build. Equality is by reference for
/// <see cref="Payload"/> and <see cref="RequiredCapabilities"/>, so compare invocations field by
/// field.</para>
/// </summary>
public sealed record ActionInvocation
{
    /// <summary>The contract version this build writes and the highest it reads.</summary>
    public const int CurrentContractVersion = 1;

    /// <summary>
    /// The version of THIS envelope. A reader refuses a higher one
    /// (<see cref="ActionOutcomeStatus.UnsupportedContract"/>) instead of interpreting fields whose
    /// meaning may have changed.
    /// </summary>
    public int ContractVersion { get; init; } = CurrentContractVersion;

    /// <summary>
    /// The registered handler this invocation is for (<c>store.coupon-journey</c>). An identifier,
    /// compared ordinally; never a type name, a path or anything a link visitor supplies.
    /// </summary>
    public required string HandlerKey { get; init; }

    /// <summary>
    /// The version of the handler's own payload schema. A handler declares the range it reads
    /// (<see cref="ActionHandlerCapability.MinVersion"/>…<see cref="ActionHandlerCapability.MaxVersion"/>);
    /// anything outside it is <see cref="ActionOutcomeStatus.UnsupportedVersion"/>.
    /// </summary>
    public required int HandlerVersion { get; init; }

    /// <summary>
    /// The handler's own typed payload, as JSON the publisher stores verbatim and never reads.
    /// Null for a handler that needs none.
    ///
    /// <para>🚨 Server-authored. It is written by the producing module at publication and read back
    /// by the handler; it never carries a value a link visitor could edit, a secret, or personal
    /// data about the sender.</para>
    /// </summary>
    public JsonElement? Payload { get; init; }

    /// <summary>
    /// Capabilities the destination must provide for this invocation to be publishable at all —
    /// words from <see cref="ActionCapabilities"/> or a module's own. Publication fails with
    /// <see cref="ActionOutcomeStatus.MissingCapability"/>, naming the word, when the resolved
    /// handler does not list every one: an advertised action must not depend on a module that
    /// happens to be loaded.
    /// </summary>
    public ImmutableArray<string> RequiredCapabilities { get; init; } = [];
}

/// <summary>
/// Capability words an <see cref="ActionInvocation"/> can require and an
/// <see cref="ActionHandlerCapability"/> can offer. An OPEN vocabulary of string constants (policy
/// <c>open-vocabulary-string-constants</c>): the platform names the ones it knows a first consumer
/// for, and any module declares its own without a change here.
/// </summary>
public static class ActionCapabilities
{
    /// <summary>The destination can save a payment method without taking a payment.</summary>
    public const string PaymentMethodSetup = "PaymentMethodSetup";

    /// <summary>The destination can schedule a consented renewal that is not charged today.</summary>
    public const string DeferredSubscription = "DeferredSubscription";
}
