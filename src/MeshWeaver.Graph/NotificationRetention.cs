using System;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Mesh;
using Microsoft.Extensions.Configuration;

namespace MeshWeaver.Graph;

/// <summary>
/// How long a notification is kept, and how much one retention run may remove. The whole policy —
/// the decision <see cref="IsExpired"/> makes and the two bounds it makes it under — with no mesh,
/// no hub and no clock of its own, so the rule can be tested without any of them.
///
/// <para>🚨 <b>Why notifications get a retention pass at all.</b> On memex-cloud
/// <c>nodeType:Notification</c> was measured at <b>4 476 rows</b> on 2026-09-03 — every notification
/// ever raised, versioned, kept, and nothing has ever removed one
/// (Systemorph/MeshWeaver#3250). A notification is the most perishable row on the platform: it says
/// something happened, it is read within minutes or not at all, nothing links to it, and its
/// SUBJECT — <see cref="Notification.TargetNodePath"/> — outlives it and carries the durable
/// record. Deleting an expired one loses a pointer, never the thing pointed at.</para>
///
/// <para>🚨 <b>The bell's legacy tail depends on this existing.</b>
/// <see href="/Doc/Architecture/AddressedNotifications">Addressed Notifications</see> §6 ruled that
/// pre-addressing rows are neither migrated nor deleted — the anchored bell simply stops reading
/// the partitions they sit in and they "age out". That ruling is explicitly conditional on a
/// retention pass eventually existing; without one, "age out" means "never".</para>
/// </summary>
public sealed record NotificationRetention
{
    /// <summary>Config key turning the pass off entirely. Default <c>true</c> — see the type remarks.</summary>
    public const string EnabledConfigKey = "Notifications:Retention:Enabled";

    /// <summary>Config key overriding <see cref="MaxAge"/> (a <see cref="TimeSpan"/> string, e.g. <c>90.00:00:00</c>).</summary>
    public const string MaxAgeConfigKey = "Notifications:Retention:MaxAge";

    /// <summary>Config key overriding <see cref="MaxDeletionsPerRun"/>.</summary>
    public const string MaxDeletionsPerRunConfigKey = "Notifications:Retention:MaxDeletionsPerRun";

    /// <summary>
    /// The shipped policy: 90 days, 200 removals per run, armed.
    ///
    /// <para>🚨 <b>Armed by default, unlike <c>AssemblyCacheRetention</c>, and the asymmetry is the
    /// point.</b> That one ships report-only because a wrong answer deletes bytes a running portal
    /// is executing. Here a wrong answer deletes a three-month-old pointer to a node that still
    /// exists — while a pass that ships disarmed reproduces exactly the defect #3250 is about,
    /// since nobody arms a knob they have never heard of. A deployment that wants it off says so
    /// with <see cref="EnabledConfigKey"/>.</para>
    /// </summary>
    public static readonly NotificationRetention Default = new();

    /// <summary>
    /// The floor <see cref="FromConfiguration"/> clamps <see cref="MaxAge"/> to. A misconfigured
    /// window must not be able to empty a bell: <c>Notifications__Retention__MaxAge: "0.00:00:00"</c>
    /// is a typo, not a request to delete every notification on the platform, and it is read as one
    /// by a chart consumer who never sees this code.
    ///
    /// <para>🚨 The clamp lives at the CONFIGURATION edge and nowhere else — <see cref="IsExpired"/>
    /// applies <see cref="MaxAge"/> exactly as given. Configuration is untyped data a typo reaches;
    /// a directly-constructed policy is code a compiler and a reviewer reach, and a predicate that
    /// silently substitutes a different window than the one its own field states is a worse thing
    /// to own than the typo it would guard.</para>
    /// </summary>
    public static readonly TimeSpan MinimumMaxAge = TimeSpan.FromDays(7);

    /// <summary>Whether the pass removes anything at all. Off means it is a complete no-op — no
    /// query is even issued.</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>
    /// A notification is removed once its node has not been written for this long.
    ///
    /// <para>Ninety days is chosen to be uncontroversial rather than aggressive: it is far past the
    /// minutes in which a notification is actually read, it keeps a full quarter of history for
    /// anyone who does scroll, and it still retires the entire pre-addressing tail within one
    /// quarter of a portal's ordinary logons.</para>
    /// </summary>
    public TimeSpan MaxAge { get; init; } = TimeSpan.FromDays(90);

    /// <summary>
    /// The hard cap on how many notifications ONE run may remove. This is what makes the pass
    /// bounded: a partition holding thousands of expired rows drains over successive runs, a couple
    /// of hundred at a time, instead of turning one logon into a mass delete.
    /// </summary>
    public int MaxDeletionsPerRun { get; init; } = 200;

    /// <summary>
    /// Whether <paramref name="node"/> is a notification this policy would remove at
    /// <paramref name="now"/>. The single definition of "expired" — the sweep asks nothing else.
    ///
    /// <para>🚨 <b>The clock is <see cref="MeshNode.LastModified"/>, not
    /// <see cref="Notification.CreatedAt"/>, and the reason is that ORDER and PREDICATE must be the
    /// same quantity.</b> The sweep bounds itself by asking the index for the oldest
    /// <see cref="MaxDeletionsPerRun"/> rows; if it then judged them on a different timestamp, the
    /// page it was handed would not be the page it wants and a backlog could fail to drain. Only
    /// <c>LastModified</c> can be both — it is a real column every backend orders on, while
    /// <c>CreatedAt</c> lives inside the content JSON and the in-memory adapter's sort silently
    /// falls back to <c>Name</c> for it. It also reads better: "untouched for 90 days" keeps a
    /// notification you opened yesterday, whatever the event's own date.</para>
    ///
    /// <para>🚨 <b>Fail CLOSED on anything unexpected.</b> Not a notification, no policy, no
    /// timestamp ⇒ not expired. This function is what deletes; every uncertainty in it must
    /// resolve to keeping the row.</para>
    /// </summary>
    /// <param name="node">The candidate row.</param>
    /// <param name="now">The current time — passed in so the rule is a pure function.</param>
    public bool IsExpired(MeshNode? node, DateTimeOffset now)
    {
        if (!Enabled || node is null)
            return false;
        // The query already filters on nodeType, but the DECISION is what deletes, so it re-checks
        // rather than trusting its caller.
        if (!string.Equals(node.NodeType, NotificationNodeType.NodeType, StringComparison.OrdinalIgnoreCase))
            return false;
        // An undated row is one this pass cannot age. Keeping it costs one row; removing it on a
        // default(DateTimeOffset) — which is BEFORE any cutoff — would delete every row the storage
        // layer failed to stamp.
        if (node.LastModified == default)
            return false;
        return node.LastModified <= now - MaxAge;
    }

    /// <summary>The effective window a CONFIGURED value yields: never below <see cref="MinimumMaxAge"/>.</summary>
    private static TimeSpan Clamp(TimeSpan maxAge) => maxAge < MinimumMaxAge ? MinimumMaxAge : maxAge;

    /// <summary>
    /// Reads the policy from configuration. Every key degrades to its default when absent or
    /// malformed — a typo in a knob must never widen what gets deleted, so a value that does not
    /// parse leaves the shipped 90 days in place, and one that parses too small is clamped to
    /// <see cref="MinimumMaxAge"/>.
    /// </summary>
    /// <param name="configuration">The host's configuration, or null on a host that has none.</param>
    public static NotificationRetention FromConfiguration(IConfiguration? configuration)
    {
        var retention = Default;
        if (configuration is null)
            return retention;

        if (bool.TryParse(configuration[EnabledConfigKey], out var enabled))
            retention = retention with { Enabled = enabled };
        if (TimeSpan.TryParse(configuration[MaxAgeConfigKey], out var maxAge) && maxAge > TimeSpan.Zero)
            retention = retention with { MaxAge = Clamp(maxAge) };
        if (int.TryParse(configuration[MaxDeletionsPerRunConfigKey], out var max) && max >= 1)
            retention = retention with { MaxDeletionsPerRun = max };

        return retention;
    }
}
