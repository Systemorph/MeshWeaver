using System.Security.Cryptography;
using System.Text;

namespace MeshWeaver.Observability;

/// <summary>
/// The default identity: <b>structure first, prose last</b>.
///
/// <para>Keys ONLY on what the CODE determines — category, event id, exception type, top
/// application frame. The message never participates. That is the correction for four rounds of getting this wrong: every failure came from prose
/// being part of the key, because a message embeds its subject in an arbitrary position
/// (<c>target: Claims</c>, <c>[PluginGating] Chess:</c>) and no amount of masking anticipates the
/// next shape. Category+EventId is assigned at the log site and cannot drift with the subject.</para>
///
/// <para>The trade is deliberate: two genuinely different faults logged at the same site with the
/// same exception collapse into one incident. That is the SAFE direction — an under-split incident
/// is one ticket a human can split, whereas an over-split one is fifty tickets nobody reads, and
/// fifty was what production actually produced.</para>
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

        // The discriminator, in order of how stable it is. A burst WITH an exception or a frame is
        // identified by those alone — the message is the part that varies per subject, and including
        // it is exactly what produced ~50 incidents for one defect.
        var discriminator = (burst.ExceptionType, burst.TopFrame) switch
        {
            ({ Length: > 0 } ex, { Length: > 0 } frame) => $"{ex}|{frame}",
            ({ Length: > 0 } ex, _) => ex,
            (_, { Length: > 0 } frame) => frame,
            // 🚨 No exception, no frame — a bare diagnostic. The message contributes NOTHING, on
            // purpose. Prose was tried as the fallback and still fanned out: the subject sits in an
            // arbitrary position (`[PluginGating] Chess:` puts it before the colon, where a
            // `label: value` rule cannot see it), and masking only ever anticipates the shape you
            // have already seen. Category + EventId identifies the log SITE, which is what the code
            // itself asserts, so all bursts from one site are one incident.
            _ => "",
        };

        var payload = $"{burst.Category}\n{burst.EventId}\n{discriminator}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(payload));
        return Convert.ToHexStringLower(hash.AsSpan(0, 8));
    }

}
