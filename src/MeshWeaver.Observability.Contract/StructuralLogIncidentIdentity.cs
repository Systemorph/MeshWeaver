using System.Security.Cryptography;
using System.Text;

namespace MeshWeaver.Observability;

/// <summary>
/// The default identity: <b>the fault, not the reporter</b> — located by frame or site, and
/// discriminated by the fault's own text.
///
/// <para>Three parts, and the first two are decided by the burst itself:</para>
/// <list type="number">
/// <item><b>WHERE.</b> The burst names an application frame ⇒ the locator IS that frame, and the log
/// category and event id are DISCARDED — they say who caught and printed the fault, not where it is.
/// No frame ⇒ there is no location to key on, so the log SITE (category + event id) is the locator.
/// This is the #1002 rule, unchanged.</item>
/// <item><b>WHAT.</b> The exception type, by simple name.</item>
/// <item><b>WHICH.</b> The normalized <i>detail</i>: the exception's OWN message when there is one,
/// else the logged message. Masked first — guids, timestamps, paths, quoted literals, hex blobs,
/// labelled identifiers, numbers, and any token the message itself uses as a path segment
/// (<see cref="LogLineParser.Normalize"/>).</item>
/// </list>
///
/// <para>🚨 <b>Why the detail had to come back (#1787).</b> Without it, "same frame + same exception
/// type" was the whole key, and on 2026-08-17 that mapped THIRTEEN NodeTypes parked at
/// <c>CompileError</c> onto ONE fingerprint: every one of them throws <c>CompilationException</c>
/// from <c>EmitPipeline.EmitCompilationToDirectory</c>, so thirteen independently-actionable
/// compiler errors produced one ticket — and #1786 had to be filed by hand. Thousands of red lines
/// per window were collapsing to one to three fingerprints; the contract is one triaged issue per
/// distinct error.</para>
///
/// <para>🚨 <b>Why the detail is the EXCEPTION's message, not the reporter's.</b> That is the whole
/// lesson of #1170/#1171: ONE <c>ObjectDisposedException</c> raised at
/// <c>SynchronizationStream&lt;T&gt;.OnCompleted()</c> during one hub teardown, logged once by
/// <c>MessageHub</c> ("Error during shutdown of hub …") and once by <c>HostedHubsCollection</c>
/// ("Hub … disposal faulted"). The prose differs per reporter; the exception message
/// ("Cannot access a disposed object.") does not. Keying on the fault's own words folds the
/// reporters and still splits genuinely different faults. Only a burst with NO exception falls back
/// to the logged message — there it is the only text there is.</para>
///
/// <para>Four earlier rounds fanned out because they hashed the reporter's prose with the subject
/// still in it, in whatever position the message happened to put it. Masking now reads the subject
/// out of the message rather than guessing at its position — see
/// <c>LogLineParser.MaskSubjects</c> — and <see cref="ComputeSiteFold"/> bounds whatever that still
/// misses.</para>
///
/// <para>Override it by implementing <see cref="ILogIncidentIdentity"/> in a Code node; this stays
/// the fallback when none is compiled.</para>
/// </summary>
public sealed class StructuralLogIncidentIdentity : ILogIncidentIdentity
{
    /// <inheritdoc />
    public string Identity(LogBurst burst)
    {
        ArgumentNullException.ThrowIfNull(burst);
        return Compute(burst);
    }

    /// <summary>The identity, as a static so callers can key without allocating an instance.</summary>
    /// <param name="burst">The parsed burst.</param>
    /// <returns>A 16-hex-character token.</returns>
    public static string Compute(LogBurst burst)
    {
        ArgumentNullException.ThrowIfNull(burst);
        return Hash(Payload(burst, Detail(burst)));
    }

    /// <summary>
    /// The identity of a whole log SITE, with the detail deliberately dropped — the fold the
    /// aggregator applies when one site produced more distinct details in a single window than the
    /// configured budget allows.
    ///
    /// <para>This is the deliberate floor under the "too fine" direction. Masking cannot anticipate
    /// every message shape, and the cost of guessing wrong is asymmetric: an under-split incident is
    /// one ticket a human can split, an over-split one is fifty tickets nobody reads — and fifty is
    /// what production actually produced on 2026-08-09. So a site that fans out past the budget
    /// stops being N tickets and becomes ONE, whose body says how many shapes it covered.</para>
    /// </summary>
    /// <param name="burst">Any burst from the site being folded.</param>
    /// <returns>A 16-hex-character token, distinct from every per-detail identity.</returns>
    public static string ComputeSiteFold(LogBurst burst)
    {
        ArgumentNullException.ThrowIfNull(burst);
        // The "fold" tag keeps this out of the per-detail namespace: a site fold and a genuine burst
        // with an empty detail must never share a fingerprint.
        return Hash("fold\n" + Payload(burst, ""));
    }

    private static string Payload(LogBurst burst, string detail)
    {
        var fault = SimpleTypeName(burst.ExceptionType);

        // The branch tag ("frame"/"site") is part of the payload so the two rules can never collide
        // on a hash — a category that happens to read like a frame stays its own incident.
        return burst.TopFrame is { Length: > 0 } frame
            ? $"frame\n{frame}\n{fault}\n{detail}"
            : $"site\n{burst.Category}\n{burst.EventId}\n{fault}\n{detail}";
    }

    /// <summary>
    /// The discriminating text. <see cref="LogBurst.NormalizedDetail"/> when the parser supplied one;
    /// otherwise the normalized message, so a hand-built burst (or an older caller) still splits on
    /// what it has rather than silently collapsing a whole site onto one ticket.
    /// </summary>
    private static string Detail(LogBurst burst) =>
        burst.NormalizedDetail is { Length: > 0 } detail ? detail : burst.NormalizedMessage;

    private static string Hash(string payload) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(payload)).AsSpan(0, 8));

    /// <summary>
    /// The exception type without its namespace. One fault reaches the watcher in two spellings —
    /// <c>ex.ToString()</c> prints <c>System.ObjectDisposedException</c> while a message that
    /// interpolates <c>ex.GetType().Name</c> prints <c>ObjectDisposedException</c> — and keying on
    /// the qualified form would fork the same defect on nothing but the caller's formatting choice.
    /// The namespace is not part of what went wrong, and the frame (or the site) already localises.
    /// </summary>
    private static string SimpleTypeName(string? exceptionType) =>
        exceptionType is { Length: > 0 } type
            ? type[(type.LastIndexOf('.') + 1)..]
            : "";
}
