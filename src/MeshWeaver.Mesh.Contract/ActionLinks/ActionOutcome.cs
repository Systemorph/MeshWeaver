using System.Text.Json.Serialization;
using MeshWeaver.Data;

namespace MeshWeaver.ActionLinks;

/// <summary>
/// What became of an attempt to publish, prepare or resolve an <see cref="ActionInvocation"/>. An
/// OPEN vocabulary of string constants (policy <c>open-vocabulary-string-constants</c>): a handler
/// may answer a word of its own, and a caller that does not know a word reports it verbatim and
/// treats it as NOT successful (<see cref="IsSuccess"/>) — an unknown word never takes a branch
/// that means something.
/// </summary>
public static class ActionOutcomeStatus
{
    /// <summary>The invocation is supported and may proceed to its next step.</summary>
    public const string Ready = "Ready";

    /// <summary>The handler has started work whose result is not known yet; the caller shows a pending state.</summary>
    public const string Pending = "Pending";

    /// <summary>The action already ran to completion for this recipient; the outcome names where its result is.</summary>
    public const string Completed = "Completed";

    /// <summary>The envelope was written by a newer contract than this build reads.</summary>
    public const string UnsupportedContract = "UnsupportedContract";

    /// <summary>No handler is registered on this portal under the invocation's key.</summary>
    public const string UnknownHandler = "UnknownHandler";

    /// <summary>A handler is registered under the key, but not for the invocation's version.</summary>
    public const string UnsupportedVersion = "UnsupportedVersion";

    /// <summary>More than one registered handler claims the key and version, so none is chosen.</summary>
    public const string AmbiguousHandler = "AmbiguousHandler";

    /// <summary>The handler lacks a capability the invocation requires; <see cref="ActionOutcome.Detail"/> names it.</summary>
    public const string MissingCapability = "MissingCapability";

    /// <summary>No authenticated person is present. The caller sends the visitor to sign in, and reveals nothing.</summary>
    public const string Unauthenticated = "Unauthenticated";

    /// <summary>The authenticated person may not do this.</summary>
    public const string Unauthorized = "Unauthorized";

    /// <summary>The invocation is bound to a different site than the one serving the request.</summary>
    public const string OriginMismatch = "OriginMismatch";

    /// <summary>The link or its offer is past its validity window.</summary>
    public const string Expired = "Expired";

    /// <summary>The link was withdrawn by its publisher.</summary>
    public const string Revoked = "Revoked";

    /// <summary>The handler could not read its own payload.</summary>
    public const string InvalidPayload = "InvalidPayload";

    /// <summary>The handler declined for a domain reason; <see cref="ActionOutcome.Reason"/> says why.</summary>
    public const string Refused = "Refused";

    /// <summary>The handler faulted. Nothing is known to have been done.</summary>
    public const string Failed = "Failed";

    /// <summary>
    /// Whether <paramref name="status"/> is one of the platform's success words
    /// (<see cref="Ready"/>, <see cref="Pending"/>, <see cref="Completed"/>). Every other word —
    /// including one this build has never seen — is not a success. Pure.
    /// </summary>
    /// <param name="status">The status word.</param>
    /// <returns>True only for a known success word.</returns>
    public static bool IsSuccess(string? status) =>
        status is Ready or Pending or Completed;
}

/// <summary>
/// The answer to "may this invocation proceed, and where does the person go next" — an OUTCOME,
/// never a fault: an unsupported handler, a foreign version and a refused recipient are all
/// ordinary answers a surface has a sentence for.
///
/// <para>Init-only properties rather than a positional record, deliberately: a new optional field is
/// then additive for every module compiled against an earlier build.</para>
/// </summary>
public sealed record ActionOutcome
{
    /// <summary>One of <see cref="ActionOutcomeStatus"/>, or a handler's own word.</summary>
    public required string Status { get; init; }

    /// <summary>The handler key the outcome is about, when it is about one.</summary>
    public string? HandlerKey { get; init; }

    /// <summary>The handler version the invocation asked for, when the outcome is about one.</summary>
    public int? HandlerVersion { get; init; }

    /// <summary>
    /// The identifier that makes the status actionable — the missing capability word, the supported
    /// version range. An identifier for operators and logs, never a sentence for a viewer.
    /// </summary>
    public string? Detail { get; init; }

    /// <summary>
    /// A viewer-facing reason supplied by the HANDLER, with the catalog key that renders it in the
    /// viewer's language. The platform's own structural outcomes carry none: a surface renders its
    /// own localized sentence for each status word.
    /// </summary>
    public LocalizableText? Reason { get; init; }

    /// <summary>
    /// Where the person goes next, when the outcome has somewhere to send them — the handler's own
    /// review page on <see cref="ActionOutcomeStatus.Ready"/>, the result on
    /// <see cref="ActionOutcomeStatus.Completed"/>. Always a local target; the surface re-checks
    /// authorization when it is opened.
    /// </summary>
    public ActivityActionReference? Navigation { get; init; }

    /// <summary>The mesh path of the domain node holding the action's durable state or result, when there is one.</summary>
    public string? ResultPath { get; init; }

    /// <summary>Whether <see cref="Status"/> is a known success word (<see cref="ActionOutcomeStatus.IsSuccess"/>).</summary>
    [JsonIgnore]
    public bool Succeeded => ActionOutcomeStatus.IsSuccess(Status);

    /// <summary>An outcome carrying just a status word about one invocation.</summary>
    /// <param name="status">The status word.</param>
    /// <param name="invocation">The invocation the outcome is about.</param>
    /// <param name="detail">The identifier that makes the status actionable, or null.</param>
    /// <returns>The outcome.</returns>
    public static ActionOutcome Of(string status, ActionInvocation invocation, string? detail = null)
    {
        ArgumentNullException.ThrowIfNull(invocation);
        return new ActionOutcome
        {
            Status = status,
            HandlerKey = invocation.HandlerKey,
            HandlerVersion = invocation.HandlerVersion,
            Detail = detail,
        };
    }
}
