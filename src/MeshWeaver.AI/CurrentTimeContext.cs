using System.Globalization;
using MeshWeaver.Messaging;

namespace MeshWeaver.AI;

/// <summary>
/// The "what day is it" block shipped into EVERY agent round's context — the anchor an agent
/// needs before it can resolve a single relative expression ("today", "tomorrow", "next Tuesday",
/// "clear my Friday"). Without it a model answers from its training priors, confidently and
/// wrong (#1651: the Executive Assistant answered Wednesday 6 August on Friday 14 August).
///
/// <para><b>Why this is PER ROUND, never in the agent's instructions.</b> Agents are built once
/// and cached (<c>ChatClientAgentFactory</c>), and a thread lives for days — a date baked into
/// the cached instruction text is stale the moment the clock rolls past midnight, which is worse
/// than no date at all because it is confidently wrong in exactly the same way. So the block is
/// composed on the round, beside the application context and attachments
/// (<c>AgentChatClient.AppendContextAndAttachmentsAsync</c>), which both the streaming and the
/// non-streaming path share.</para>
///
/// <para><b>🕰️ Two clocks, deliberately, because they answer two different questions.</b>
/// Every stored instant is UTC, and the deployment container's process zone IS UTC — so
/// <c>.ToLocalTime()</c> / <c>.LocalDateTime</c> are no-ops wearing a disguise and are never
/// used here. The viewer's wall clock comes from their named IANA zone
/// (<see cref="AccessContext.TimeZoneId"/>) through the one display seam,
/// <see cref="DisplayTimeExtensions.ToDisplayTime(DateTimeOffset, string?)"/>, so DST is applied
/// per-region rather than as a fixed offset:
/// <list type="bullet">
///   <item><description><b>The user's local date</b> answers "what day is today" for a HUMAN, and
///   is the anchor every relative expression must be resolved against. Near midnight the UTC date
///   and the user's date are DIFFERENT days — that is the whole reason the zone has to ride
///   along.</description></item>
///   <item><description><b>The UTC instant, ISO-8601 with the trailing <c>Z</c></b>, is what the
///   agent may feed back into a tool, a node field or a calendar entry. A zone-less string is
///   re-parsed in the SERVER's zone on the way back, which silently shifts it.</description></item>
/// </list>
/// An unknown or unset zone degrades to UTC and SAYS SO — never to the server's zone, which looks
/// right in CI and is wrong in Zurich.</para>
///
/// <para>The formatter is <b>pure</b>: it takes the instant and the zone id as arguments and
/// touches no hub, no circuit, and no ambient <c>CultureInfo</c> — the caller captures the zone
/// once, on the round. Text is model-facing, so it is invariant English by design (the same rule
/// that keeps tool-parameter descriptions untranslated); that also makes it deterministic to
/// test. See <c>CurrentTimeContextTest</c>.</para>
/// </summary>
public static class CurrentTimeContext
{
    /// <summary>The markdown heading the block opens with — shared with the tests so they cannot drift.</summary>
    public const string Heading = "# Current Date and Time";

    /// <summary>
    /// Renders the per-round date/time block for a viewer in <paramref name="timeZoneId"/>.
    /// </summary>
    /// <param name="utcNow">The current instant. Converted to UTC before anything is rendered, so
    /// a caller that passes a non-UTC offset still gets the correct instant on both clocks.</param>
    /// <param name="timeZoneId">The viewer's named IANA zone (e.g. <c>Europe/Zurich</c>), normally
    /// <see cref="AccessContext.TimeZoneId"/>. Null, empty, or an id this host cannot resolve →
    /// the block renders UTC and says the zone is unknown. Never a fixed offset (DST would then be
    /// wrong half the year).</param>
    /// <returns>A markdown block ending in a blank line, ready to append to the round's context.</returns>
    public static string Describe(DateTimeOffset utcNow, string? timeZoneId)
    {
        var utc = utcNow.ToUniversalTime();
        var zone = DisplayTimeExtensions.ResolveZone(timeZoneId);
        var local = DisplayTimeExtensions.ToDisplayTime(utc, timeZoneId);

        // The zone LABEL is the resolved zone's own id, never the caller's string: an id this host
        // cannot resolve falls back to UTC, and echoing the unresolvable name would claim a
        // conversion that did not happen.
        var zoneLine = zone is null
            ? $"- **Local time now:** {local.ToString("HH:mm", CultureInfo.InvariantCulture)} " +
              "(UTC — the user's time zone is not known, so this may not be their wall clock)"
            : $"- **Local time now:** {local.ToString("HH:mm", CultureInfo.InvariantCulture)} " +
              $"({zone.Id}, UTC{local.ToString("zzz", CultureInfo.InvariantCulture)})";

        return $"""
                {Heading}

                - **Today (the user's local date):** {local.DayOfWeek}, {local.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}
                {zoneLine}
                - **Now, UTC (machine-readable):** `{Iso(utc)}`

                Resolve every relative expression — "today", "tomorrow", "next Tuesday", "this Friday",
                "in two weeks" — against the user's LOCAL date above. Never answer the date from memory.
                When you WRITE a timestamp (a tool argument, a node field, a calendar entry) use ISO-8601
                with the trailing `Z` or an explicit offset; a zone-less string is re-read in the server's
                zone and silently shifts. Do NOT convert dates that are not instants — a policy inception,
                an as-of date, a due date or a birthday is the same calendar day in every zone.


                """;
    }

    /// <summary>
    /// The machine-facing form of an instant: ISO-8601, seconds precision, explicit trailing
    /// <c>Z</c>. Anything an agent may hand back to a tool goes through here — a zone-less string
    /// re-parses in the server's zone on the way back.
    /// </summary>
    public static string Iso(DateTimeOffset instant) =>
        instant.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
}
