using System.Collections.Immutable;

namespace MeshWeaver.PluginCatalog;

/// <summary>
/// How one attempt to adopt a package's prebuilt assemblies ended.
///
/// <para>🚨 They are separate values because <b>every one of them used to be the integer 0</b>, and
/// a caller cannot tell an outage from a normal day given a 0. "The registry does not advertise
/// this package for my lane" and "the registry served it and I adopted every assembly in it" were
/// the same return value, and lazy compile absorbed the difference silently.</para>
/// </summary>
public enum BundleAdoptionKind
{
    /// <summary>Assemblies were adopted from the registry's bundle.</summary>
    Adopted,

    /// <summary>🚨 The registry's index does not list this package at all — for THIS lane. The
    /// miss that used to be completely silent: no log line, no counter, just a compile that looked
    /// like normal behaviour.</summary>
    NotAdvertised,

    /// <summary>The registry's whole index is baked for a different framework identity/architecture,
    /// so nothing it holds is adoptable here. Normal during a platform roll; an outage when it
    /// persists.</summary>
    FrameworkDeclined,

    /// <summary>The registry advertises the package but answered 404 for this lane's bytes.</summary>
    NotServed,

    /// <summary>The fetch failed — the registry is down, rate-limiting, or has revoked this
    /// install's grant.</summary>
    FetchFailed,

    /// <summary>The bundle arrived but its own manifest declines against this framework.</summary>
    BundleDeclined,

    /// <summary>The bundle arrived and carried no assemblies.</summary>
    NoAssemblies,
}

/// <summary>
/// One adoption attempt's result — what was asked of which registry, and what came back.
/// </summary>
/// <param name="PluginId">The package.</param>
/// <param name="Kind">How it ended.</param>
/// <param name="Registry">The registry asked.</param>
/// <param name="Adopted">Assemblies actually seeded.</param>
/// <param name="Offered">Assemblies the bundle carried, when one arrived.</param>
/// <param name="Reason">One sentence, for the kinds that have something to say.</param>
public sealed record BundleAdoptionOutcome(
    string PluginId,
    BundleAdoptionKind Kind,
    string Registry,
    int Adopted = 0,
    int Offered = 0,
    string? Reason = null)
{
    /// <summary>
    /// Whether this attempt left content to be COMPILED here that the distribution lane was meant
    /// to serve. Adopting fewer assemblies than were offered counts — a partial adoption is a
    /// partial miss, and rounding it to "adopted" is how a regression hides inside a success.
    /// </summary>
    public bool IsMiss => Kind != BundleAdoptionKind.Adopted || Adopted < Offered;

    /// <summary>One line, for a log or a health payload.</summary>
    public string Describe() => Kind switch
    {
        BundleAdoptionKind.Adopted when Adopted >= Offered =>
            $"{PluginId}: adopted {Adopted}/{Offered}",
        BundleAdoptionKind.Adopted =>
            $"{PluginId}: adopted only {Adopted}/{Offered} — the rest compile here",
        _ => $"{PluginId}: {Kind}"
             + (string.IsNullOrWhiteSpace(Reason) ? string.Empty : $" ({Reason})"),
    };
}

/// <summary>
/// 🚨 <b>The count that proves the distribution lane works</b> — every adoption attempt this
/// process made, and how it ended (#1782 gap 4).
///
/// <para>Adoption's only evidence today is a log line, and the measurement that justified the whole
/// lane is a pair of them (prod: 80 compiles / 64.8 s → 0 compiles, 84 adopted, 32.1 s). With
/// instance-level pre-bake giving way to lazy compile-on-access (#1746), the fetch path becomes the
/// PRIMARY way assemblies arrive — and a lazy compile absorbs a miss so completely that the lane
/// can go entirely dark while every surface looks like a healthy day. That is what happened on
/// 2026-08-20: the registry served an empty index and every consumer quietly compiled.</para>
///
/// <para>So the outcomes are RECORDED, not merely logged. A miss stays countable after the log has
/// rotated, and it is readable by an operator surface without turning anything on.</para>
///
/// <para>Process-scoped and cheap: one immutable list, appended under
/// <see cref="ImmutableInterlocked"/>, bounded so a pathological reconcile loop cannot grow it
/// without limit. It is a diagnostic, never a source of truth — nothing decides anything from
/// it.</para>
/// </summary>
public sealed class BundleAdoptionLedger
{
    /// <summary>
    /// How many outcomes are kept. Bounded because this is a diagnostic on a long-lived process:
    /// the last N attempts answer "is the lane working now", which is the question, while an
    /// unbounded list would answer it and also leak.
    /// </summary>
    public const int Capacity = 500;

    private ImmutableList<BundleAdoptionOutcome> outcomes = ImmutableList<BundleAdoptionOutcome>.Empty;

    /// <summary>Records one attempt. Thread-safe; never throws.</summary>
    public void Record(BundleAdoptionOutcome outcome)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        ImmutableInterlocked.Update(ref outcomes, current =>
        {
            var next = current.Add(outcome);
            return next.Count > Capacity ? next.RemoveRange(0, next.Count - Capacity) : next;
        });
    }

    /// <summary>Every recorded attempt, oldest first.</summary>
    public ImmutableList<BundleAdoptionOutcome> Outcomes => Volatile.Read(ref outcomes);

    /// <summary>The attempts that left something to compile here.</summary>
    public ImmutableList<BundleAdoptionOutcome> Misses =>
        Outcomes.Where(o => o.IsMiss).ToImmutableList();

    /// <summary>
    /// The one line every surface renders: how many attempts, how many assemblies adopted, and —
    /// named — what was missed.
    ///
    /// <para>🚨 "Nothing was ever attempted" and "everything was adopted" are DIFFERENT sentences.
    /// A deployment with no registry configured never attempts adoption, and reporting that as a
    /// clean sweep would make the absence of the lane look like the success of it.</para>
    /// </summary>
    /// <param name="maxNamed">How many misses are named before the line truncates.</param>
    public string Describe(int maxNamed = 10)
    {
        var all = Outcomes;
        if (all.IsEmpty)
            return "no bundle adoption has been attempted in this process";

        var misses = all.Where(o => o.IsMiss).ToArray();
        var adopted = all.Sum(o => o.Adopted);
        if (misses.Length == 0)
            return $"{all.Count} adoption attempt(s), {adopted} assembly/assemblies adopted, no misses";

        return $"{all.Count} adoption attempt(s), {adopted} assembly/assemblies adopted, "
            + $"{misses.Length} MISS(es) — content the registry was meant to serve is compiled "
            + "here instead: "
            + string.Join("; ", misses.Take(Math.Max(1, maxNamed)).Select(m => m.Describe()))
            + (misses.Length > maxNamed ? $", …(+{misses.Length - maxNamed})" : string.Empty);
    }
}
