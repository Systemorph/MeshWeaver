using System.Security.Cryptography;
using System.Text;

namespace MeshWeaver.Observability;

/// <summary>
/// The default identity: <b>the fault, not the reporter</b>.
///
/// <para>Two rules, and which one applies is decided by the burst itself:</para>
/// <list type="number">
/// <item><b>The burst names an application frame</b> ⇒ the incident IS
/// <c>(top application frame, exception type)</c>. The log category and event id are DISCARDED —
/// they say who caught and printed the fault, not where it is. One exception unwinding through two
/// catch sites is one defect however many of them log it.</item>
/// <item><b>It names no application frame</b> ⇒ there is no location to key on, so the log SITE
/// (category + event id) is the locator, plus the exception type when there is one. This is the
/// #1002 rule, unchanged: a bare diagnostic is identified by the site the code assigns it.</item>
/// </list>
///
/// <para>The message never participates, in either branch. That is the correction for four rounds
/// of getting this wrong: every failure came from prose being part of the key, because a message
/// embeds its subject in an arbitrary position (<c>target: Claims</c>, <c>[PluginGating] Chess:</c>)
/// and no amount of masking anticipates the next shape.</para>
///
/// <para>🚨 <b>Why the category had to go.</b> Keying on it split #1170 from #1171 — ONE
/// <c>ObjectDisposedException</c> raised at <c>SynchronizationStream&lt;T&gt;.OnCompleted()</c>
/// during one hub teardown on one pod at one instant, logged once by
/// <c>MeshWeaver.Messaging.MessageHub</c> ("Error during shutdown of hub …") and once by
/// <c>MeshWeaver.Messaging.HostedHubsCollection</c> ("Hub … disposal faulted"). Same fault, same
/// frame, two reporters, two tickets. The frame is a strictly more specific locator than the
/// category — it names the exact method — so dropping the category where a frame exists cannot
/// merge anything the category was keeping apart for a good reason.</para>
///
/// <para>The remaining trade is deliberate and unchanged: two genuinely different faults raised at
/// the same frame with the same exception type collapse into one incident. That is the SAFE
/// direction — an under-split incident is one ticket a human can split, whereas an over-split one
/// is fifty tickets nobody reads, and fifty was what production actually produced.</para>
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

        var fault = SimpleTypeName(burst.ExceptionType);

        // The branch tag ("frame"/"site") is part of the payload so the two rules can never collide
        // on a hash — a category that happens to read like a frame stays its own incident.
        var payload = burst.TopFrame is { Length: > 0 } frame
            ? $"frame\n{frame}\n{fault}"
            // 🚨 No application frame — a bare diagnostic, or a fault whose whole stack is framework.
            // The message contributes NOTHING here either: the subject sits in an arbitrary position
            // (`[PluginGating] Chess:` puts it before the colon, where a `label: value` rule cannot
            // see it), and masking only ever anticipates the shape you have already seen. Category +
            // EventId identifies the log SITE, which is what the code itself asserts.
            : $"site\n{burst.Category}\n{burst.EventId}\n{fault}";

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(payload));
        return Convert.ToHexStringLower(hash.AsSpan(0, 8));
    }

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
