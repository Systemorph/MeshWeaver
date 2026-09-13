// The namespace is a binary contract, not a filing decision — see MeshOperations.cs.
namespace MeshWeaver.AI;

/// <summary>
/// The three NON-JSON answers a <see cref="MeshOperations"/> verb can produce — the model-facing
/// sentinel sentences that ride the MCP tool result as plain text (plus the permission refusal
/// <c>DeleteErrorLine</c> writes) — and, for every HTTP host that mirrors those verbs, the ONE
/// mapping from sentinel to status code.
///
/// <para>The verbs return a string that is EITHER a JSON document OR one of these prose sentences
/// (<c>"Error: …"</c>, <c>"Not found: …"</c>, <c>"Unavailable: …"</c>). On the MCP surface that is
/// the whole contract: the caller is a language model and reads the sentence. A REST mirror that
/// ships the same string with <c>200 application/json</c> breaks every HTTP caller's first
/// assumption — <c>response.ok</c> meant "here is the node" and the body then failed inside
/// <c>JSON.parse</c> naming neither the path nor the reason (MeshWeaver.Plugins#1699, measured on an
/// <c>Unavailable: …</c> answer from <c>POST /api/mesh/get</c>). Two hosts mirror the verbs
/// (<c>Memex.Portal.Shared</c>'s <c>MeshApiEndpoints</c> and the Plugins sidecar's
/// <c>LocalMeshApiEndpoints</c>); they classify through this type so they cannot disagree.</para>
///
/// <para>A JSON document never starts with any of these prefixes (it starts with <c>{</c>, <c>[</c>,
/// a quote, a digit, <c>true</c>/<c>false</c>/<c>null</c>), so the prefix test is exact, not a
/// heuristic. The prefixes themselves are the constants the verbs write; they are deliberately not
/// localized (model-facing text stays English so tool-calling does not degrade).</para>
/// </summary>
public static class OperationSentinel
{
    /// <summary>A verb that ran and failed — a fault the caller can read in the sentence.</summary>
    public const string ErrorPrefix = "Error:";

    /// <summary>A DEFINITIVE absence: the read completed and there is nothing readable at the path.</summary>
    public const string NotFoundPrefix = "Not found:";

    /// <summary>A read that reached NO verdict (issue #974): the node's existence is unknown; retry.</summary>
    public const string UnavailablePrefix = "Unavailable:";

    /// <summary>
    /// A verb the current identity may not perform here (<c>DeleteErrorLine</c>: "Refused deleting
    /// {path}: … do not retry"): a permission verdict that hands the model a GUI URL to present to the
    /// user. Prefix-shaped without a colon — the writer predates the other three; the sentence stays
    /// as written because the model reads it.
    /// </summary>
    public const string RefusedPrefix = "Refused ";

    /// <summary>
    /// Classifies one verb result. <c>null</c> for a JSON document (ship it as-is); otherwise the
    /// sentinel's kind and the HTTP status an HTTP mirror answers with.
    /// </summary>
    public static SentinelVerdict? Classify(string? result)
    {
        if (result is null)
            return null;
        if (result.StartsWith(NotFoundPrefix, StringComparison.Ordinal))
            return new SentinelVerdict(SentinelKind.NotFound, 404);
        if (result.StartsWith(UnavailablePrefix, StringComparison.Ordinal))
            return new SentinelVerdict(SentinelKind.Unavailable, 503);
        if (result.StartsWith(ErrorPrefix, StringComparison.Ordinal))
            return new SentinelVerdict(SentinelKind.Error, 500);
        if (result.StartsWith(RefusedPrefix, StringComparison.Ordinal))
            return new SentinelVerdict(SentinelKind.Refused, 403);
        return null;
    }
}

/// <summary>Which sentinel a verb answered with — see <see cref="OperationSentinel"/>.</summary>
public enum SentinelKind
{
    /// <summary><c>"Error: …"</c> — the verb ran and failed.</summary>
    Error,

    /// <summary><c>"Not found: …"</c> — a definitive absence.</summary>
    NotFound,

    /// <summary><c>"Unavailable: …"</c> — no verdict was reached; retry, never create or delete.</summary>
    Unavailable,

    /// <summary><c>"Refused …"</c> — the identity may not perform the verb here; do not retry, hand the user the GUI URL.</summary>
    Refused,
}

/// <summary>
/// A classified sentinel: its <see cref="Kind"/> and the <see cref="HttpStatus"/> every HTTP mirror
/// of the verbs answers with — 404 for a definitive absence, 503 for a read that reached no verdict
/// (the sentence itself says "retry shortly"), 500 for a fault, 403 for a permission refusal. The HTTP body an HTTP mirror ships
/// is <c>{ "error": &lt;the sentence&gt;, "kind": &lt;Kind&gt; }</c>, so the reason stays readable
/// and machine-addressable at once.
/// </summary>
/// <param name="Kind">The sentinel's kind.</param>
/// <param name="HttpStatus">The status code an HTTP mirror answers with.</param>
public sealed record SentinelVerdict(SentinelKind Kind, int HttpStatus);
