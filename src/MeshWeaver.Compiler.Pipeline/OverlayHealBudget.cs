using System.Collections.Concurrent;

namespace MeshWeaver.Graph.Configuration;

/// <summary>
/// How often ONE instance may recycle itself off a compilation overlay for ONE NodeType — the
/// bound that lets <c>NodeTypeEnrichmentHelpers.ArmOverlaySelfHeal</c> keep RE-EVALUATING a
/// fault card without turning a degraded page into a recycle storm.
///
/// <para><b>Why the watcher cannot bound itself.</b> The self-heal's whole action is to dispose the
/// instance hub so the next access re-enriches. That destroys the watcher too: the replacement hub
/// arms a FRESH one, whose ladder starts again at its first rung. So every piece of "we already
/// tried this" state a watcher could hold dies exactly when it becomes relevant. If the
/// re-enrichment faults again — a type that reports a usable build but whose activation still
/// cannot be prepared, which is precisely the 2026-08-17 memex shape (a deterministic cross-hub
/// <c>Conflict</c> on the recompile flip, issue #1814) — the pair would loop at the first rung
/// forever. This registry is the memory that survives the recycle.</para>
///
/// <para><b>The rule.</b> The FIRST self-heal for a pair is never delayed — the overwhelmingly
/// common case is a deploy window that has cleared, and making it wait would be a regression. Each
/// FURTHER self-heal inside <see cref="ForgetWindow"/> must wait out a widening spacing
/// (<see cref="Spacing"/>), so a non-converging pair costs at most one activation per step instead
/// of one per grace window. An entry that has not healed for <see cref="ForgetWindow"/> is
/// forgotten, so a genuinely transient fault that healed once and stayed healthy starts fresh.</para>
///
/// <para><b>Mesh-scoped singleton, instance maps only — NO static state</b> (registered in
/// <c>AddGraph</c>, exactly like <see cref="NodeTypeCompileParkRegistry"/>). Absent from the service
/// tree — a unit-test hub, a host without <c>AddGraph</c> — the watcher simply applies no spacing,
/// which is the pre-registry behaviour rather than a failure.</para>
///
/// <para>This is a SPACING budget, never a cap: it delays a recycle, it never cancels one. A cap
/// would re-create the very defect it is bounding — a fault card that outlives its cause because
/// something decided to stop looking.</para>
/// </summary>
public sealed class OverlayHealBudget
{
    /// <summary>
    /// Minimum spacing before the next self-heal of a pair, indexed by how many self-heals it has
    /// already performed inside the window. Past the last entry the final value repeats — the
    /// steady state for a pair that never converges.
    /// </summary>
    private static readonly TimeSpan[] Spacing =
    [
        TimeSpan.FromSeconds(45),
        TimeSpan.FromSeconds(90),
        TimeSpan.FromMinutes(3),
        TimeSpan.FromMinutes(6),
        TimeSpan.FromMinutes(10),
    ];

    /// <summary>
    /// A pair that has not self-healed for this long is forgotten, so its next fault starts from an
    /// un-delayed heal again. Long enough to cover a full deploy + warm-up window (the interval over
    /// which repeats are evidence of non-convergence rather than of two unrelated incidents).
    /// </summary>
    internal static readonly TimeSpan ForgetWindow = TimeSpan.FromMinutes(30);

    private readonly ConcurrentDictionary<string, Entry> entries =
        new(StringComparer.OrdinalIgnoreCase);

    private sealed record Entry(int Heals, DateTimeOffset LastHeal);

    private static string Key(string instancePath, string nodeType) => $"{instancePath} {nodeType}";

    /// <summary>
    /// The instant from which this pair may recycle itself again.
    /// <see cref="DateTimeOffset.MinValue"/> (i.e. "no wait") for a pair with no recent self-heal.
    /// </summary>
    public DateTimeOffset EarliestHeal(string instancePath, string nodeType, DateTimeOffset now)
    {
        if (!entries.TryGetValue(Key(instancePath, nodeType), out var entry))
            return DateTimeOffset.MinValue;
        if (now - entry.LastHeal >= ForgetWindow)
            return DateTimeOffset.MinValue;
        return entry.LastHeal + SpacingAfter(entry.Heals);
    }

    /// <summary>
    /// How many self-heals this pair has already performed inside <see cref="ForgetWindow"/> — the
    /// number a "still not converging" diagnostic reports.
    /// </summary>
    public int HealsSoFar(string instancePath, string nodeType, DateTimeOffset now)
        => entries.TryGetValue(Key(instancePath, nodeType), out var entry)
            && now - entry.LastHeal < ForgetWindow
            ? entry.Heals
            : 0;

    /// <summary>
    /// Record a self-heal that is about to be posted; returns the pair's new heal count. A record
    /// older than <see cref="ForgetWindow"/> is replaced rather than incremented.
    /// </summary>
    public int RecordHeal(string instancePath, string nodeType, DateTimeOffset now)
        => entries.AddOrUpdate(
            Key(instancePath, nodeType),
            _ => new Entry(1, now),
            (_, existing) => now - existing.LastHeal >= ForgetWindow
                ? new Entry(1, now)
                : new Entry(existing.Heals + 1, now)).Heals;

    private static TimeSpan SpacingAfter(int heals)
        => Spacing[Math.Clamp(heals - 1, 0, Spacing.Length - 1)];
}
