namespace MeshWeaver.Hosting.SelfUpdate;

/// <summary>
/// The last self-update check THIS process reported: when, what woke it, its outcome, its one-line
/// verdict, and whether that verdict is a FAILURE (a check that faulted, a release that could
/// neither be applied nor handed over, an install stranded on a withdrawn tag). Read by the
/// <c>self_update</c> entry on <c>/health</c>.
///
/// <para>🚨 <b>Why this exists (policy <c>control-first-never-silent</c>).</b> Control-first makes the
/// whole fleet wait for the control instance, and control's self-updater wrote its verdict to ONE
/// place: <c>Admin/UpdatePolicy.lastCheckVerdict</c> on control itself, behind authentication, a node
/// nobody watches. Measured 2026-10-07/08: that field read <c>check FAILED:
/// CredentialUnavailableException … The requested identity has not been assigned to this
/// resource</c> on every check, control sat on one build for 10+ hours, memex and memex-cloud
/// with it, and control's <c>/health</c> said nothing about it. A failure that an instance publishes
/// only to itself is the silence this census removes: <c>/health</c> is public and unauthenticated,
/// so the CD arming and the <c>control-always-latest</c> alarm read it from outside.</para>
///
/// <para>An instance owned by the mesh (registered as a singleton by <c>AddSelfUpdate</c>), never
/// static: its lifetime is the mesh's. The reading is one immutable record swapped atomically, so a
/// probe never sees half a check.</para>
/// </summary>
public sealed class SelfUpdateCheckCensus
{
    /// <summary>The <c>/health</c> entry's name.</summary>
    public const string HealthCheckName = "self_update";

    /// <summary>The longest verdict text the <c>/health</c> line carries. A verdict can be a whole
    /// credential-chain dump (measured: ~1.4 KB for <c>CredentialUnavailableException</c>); the first
    /// line names the failure and the full text stays on <c>Admin/UpdatePolicy</c>.</summary>
    public const int MaxLineLength = 400;

    private SelfUpdateCheckReading? last;

    /// <summary>The last reported check, or null when this process has reported none.</summary>
    public SelfUpdateCheckReading? Last => Volatile.Read(ref last);

    /// <summary>Records one reported check. Called by the self-updater's single reporting site.</summary>
    /// <param name="reading">The check as it was reported.</param>
    public void Record(SelfUpdateCheckReading reading) => Volatile.Write(ref last, reading);

    /// <summary>
    /// The <c>/health</c> description for <paramref name="reading"/>: one line, never empty. No
    /// reading is said as an absence of measurement, never as a clean one. Pure.
    /// </summary>
    /// <param name="reading">The last check, or null.</param>
    /// <param name="now">The clock the age is read against.</param>
    /// <returns>The one-line description.</returns>
    public static string Describe(SelfUpdateCheckReading? reading, DateTimeOffset now)
    {
        if (reading is null)
            return "NO self-update check reported by this process yet — this is an absence of measurement, "
                   + "NOT a clean check (read Admin/UpdatePolicy.lastCheckVerdict)";
        var age = now - reading.At;
        var ageText = age < TimeSpan.Zero ? "just now" : $"{(int)age.TotalMinutes} min ago";
        return $"{OneLine(reading.Verdict)} [outcome {reading.Outcome}, trigger {reading.Trigger}, {ageText}]";
    }

    /// <summary>The first line of <paramref name="verdict"/>, whitespace-collapsed and capped at
    /// <see cref="MaxLineLength"/> — a <c>/health</c> body is parsed line by line. Pure.</summary>
    /// <param name="verdict">The verdict as recorded.</param>
    /// <returns>The single line.</returns>
    public static string OneLine(string? verdict)
    {
        var first = (verdict ?? "").Split('\n', 2)[0].Replace('\r', ' ').Trim();
        first = string.Join(' ', first.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        return first.Length <= MaxLineLength ? first : first[..(MaxLineLength - 1)] + "…";
    }
}

/// <summary>One reported self-update check.</summary>
/// <param name="At">When it was reported.</param>
/// <param name="Trigger">What woke it (<c>Startup</c>, <c>BuildCompletion</c>, <c>SafetyNet</c>, …).</param>
/// <param name="Outcome">Its outcome's name.</param>
/// <param name="Verdict">Its one-sentence verdict, as written to <c>Admin/UpdatePolicy</c>.</param>
/// <param name="Failed">Whether the verdict is a failure — the self-updater's own classification.</param>
public sealed record SelfUpdateCheckReading(
    DateTimeOffset At, string Trigger, string Outcome, string Verdict, bool Failed);
