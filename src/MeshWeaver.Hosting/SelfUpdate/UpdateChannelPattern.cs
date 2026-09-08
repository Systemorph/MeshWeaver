using System.Text.RegularExpressions;

namespace MeshWeaver.Hosting.SelfUpdate;

/// <summary>
/// The VERSION PATTERN an <c>Admin/UpdatePolicy</c> record uses to opt into continuous builds —
/// a glob over the registry tag, e.g. <c>3.0.1-ci*</c>.
///
/// <para>🚨 <b>Why a pattern at all (maintainer, 2026-09-08).</b> <i>"By default we will not
/// upgrade as long as no version without <c>-ci…</c> is labelled ⇒ we want a clean label
/// <c>3.0.1</c> to upgrade. If we want to get the <c>-ci…</c> we have to specify the pattern
/// <c>3.0.1-ci*</c>."</i> A pre-release tag is therefore never eligible on its own: it is eligible
/// only when a pattern on the policy record ADMITS it. The pattern is deliberately a glob and not a
/// SemVer range — an operator writes the line they want to follow the way it appears in the
/// registry, and <c>3.0.0-ci*</c> can never admit <c>3.0.1</c> or <c>3.0.1-ci.4</c>, which is the
/// whole point: following one line's continuous builds is a decision that ends when the next
/// clean release is cut, and it ends by the pattern no longer matching anything new.</para>
///
/// <para>The pattern only says WHICH tags are candidates. Their ORDER is still the
/// sealed-publication lineage (<c>PlatformReleaseOrder</c>): among <c>3.0.1-ci.7845</c> and
/// <c>3.0.1-ci.8059</c> the run number decides, so <c>ci.7845 &lt; ci.8059</c> numerically and a
/// retired <c>rc</c> label never outranks a later run.</para>
///
/// <para>Pure. <c>*</c> matches any run of characters, <c>?</c> exactly one, everything else
/// literally; the whole tag must match; case-insensitive because registries are.</para>
/// </summary>
public static class UpdateChannelPattern
{
    /// <summary>
    /// <paramref name="pattern"/> with surrounding whitespace removed, or <c>null</c> when it is
    /// null, empty or whitespace — the ONE notion of "no pattern", so a record whose field reads
    /// <c>" "</c> is not silently a different policy from one whose field is absent.
    /// </summary>
    public static string? Normalize(string? pattern)
    {
        var trimmed = pattern?.Trim();
        return string.IsNullOrEmpty(trimmed) ? null : trimmed;
    }

    /// <summary>
    /// Whether <paramref name="tag"/> is admitted by <paramref name="pattern"/>. A null/blank
    /// pattern admits NOTHING — the caller decides what "no pattern" means (for the update policy:
    /// clean releases only), never this matcher, so an absent pattern can never widen a channel.
    /// </summary>
    public static bool Matches(string? pattern, string tag)
    {
        var normalized = Normalize(pattern);
        if (normalized is null || string.IsNullOrEmpty(tag))
            return false;
        return Regex.IsMatch(tag, ToRegex(normalized), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    /// <summary>The anchored regular expression <paramref name="glob"/> denotes — exposed so a
    /// test can pin the translation rather than infer it from matches.</summary>
    public static string ToRegex(string glob) =>
        "^" + Regex.Escape(glob).Replace(@"\*", ".*").Replace(@"\?", ".") + "$";
}
