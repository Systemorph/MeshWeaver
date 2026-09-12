using System.Collections.Immutable;
using MeshWeaver.Plugin.Packaging;

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

    /// <summary>🚨 The bytes the OCI registry served for the artifact's digest did not hash to it
    /// (<see cref="OciDigestMismatchException"/>) — refused, nothing landed, no HTTP fallback. A
    /// registry serving the wrong bytes under a sealed digest is an integrity failure, never a
    /// transient one.</summary>
    ArtifactRefused,

    /// <summary>
    /// 🚨 The bundle arrived, was accepted for this lane, and DECLARES that it has no NodeType
    /// assemblies to adopt — a module-only or content-only package. <b>Not a miss</b>: nothing was
    /// meant to be served here and nothing is compiled instead.
    ///
    /// <para>Appended rather than folded into <see cref="NoAssemblies"/> because the two are the
    /// same observation with opposite meanings, and collapsing them is the exact conflation this
    /// enum exists to prevent: "this package has no NodeTypes" and "this package's NodeTypes failed
    /// to arrive" are DIFFERENT sentences, and reporting the first as the second makes a healthy
    /// portal read Degraded for ever. Measured 2026-09-12 on memex.systemorph.com: 13 of 25
    /// attempts — <c>AI</c>, <c>Anthropic</c>, <c>Maps</c>, <c>Chat</c>, <c>Mcp</c>, … , every one
    /// of them a module package whose module landed correctly through
    /// <see cref="ModuleLandingService"/> — were counted as misses on that wording (#3768).</para>
    ///
    /// <para>🚨 It is decided from a POSITIVE DECLARATION in the manifest (a module, or content),
    /// never from the absence of assemblies. A bundle that declares nothing at all is genuinely
    /// empty and stays <see cref="NoAssemblies"/> — loud — because "the producer shipped an empty
    /// archive" is a real defect that an inference from emptiness would silence.</para>
    /// </summary>
    NothingToAdopt,
}

/// <summary>
/// How to read a bundle that carried no NodeType assemblies (#3768).
/// </summary>
public static class BundleOffering
{
    /// <summary>
    /// Classifies a bundle whose assembly list is EMPTY — the one observation that means two
    /// opposite things.
    ///
    /// <para>🚨 The answer comes from what the manifest DECLARES, never from what it lacks. A
    /// package that says it ships a module, or content, has no NodeTypes to offer and is complete
    /// as delivered (<see cref="BundleAdoptionKind.NothingToAdopt"/>). A package that declares
    /// nothing — no module, no content, and no unresolved types either — is an empty archive, which
    /// is a producer defect, and it stays <see cref="BundleAdoptionKind.NoAssemblies"/> so it stays
    /// loud. Inferring the benign reading from emptiness would silence exactly that case.</para>
    ///
    /// <para>Unresolved producer misses dominate: if the bake could not resolve types it was asked
    /// for, the bundle is short whatever else it declares, and that is a miss.</para>
    /// </summary>
    /// <param name="manifest">The bundle's manifest, or null from an unreadable bundle.</param>
    public static BundleAdoptionKind ClassifyEmpty(BundleReader.Manifest? manifest)
    {
        if (manifest?.Misses is { Count: > 0 })
            return BundleAdoptionKind.NoAssemblies;

        var declaresModule = manifest?.Module?.AssemblyName is { Length: > 0 };
        var declaresContent = manifest?.Content is { Count: > 0 };
        return declaresModule || declaresContent
            ? BundleAdoptionKind.NothingToAdopt
            : BundleAdoptionKind.NoAssemblies;
    }

    /// <summary>What an empty-but-complete bundle offered instead of NodeTypes, for the log.</summary>
    public static string OfferingOf(BundleReader.Manifest? manifest) =>
        manifest?.Module?.AssemblyName is { Length: > 0 } name
            ? $"module '{name}'"
            : "content only";
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
    ///
    /// <para>🚨 <see cref="BundleAdoptionKind.NothingToAdopt"/> is NOT a miss, and it is the one
    /// exception that has to be stated rather than inferred: a module-only or content-only package
    /// offers no NodeType assembly, so there is nothing the lane was "meant to serve" and nothing
    /// compiles here in its place. Counting it left 13 of 25 attempts on memex.systemorph.com
    /// reading as misses on 2026-09-12 while every one of them had landed correctly.</para>
    /// </summary>
    public bool IsMiss =>
        Kind is not (BundleAdoptionKind.Adopted or BundleAdoptionKind.NothingToAdopt)
        || Adopted < Offered;

    /// <summary>One line, for a log or a health payload.</summary>
    public string Describe() => Kind switch
    {
        BundleAdoptionKind.Adopted when Adopted >= Offered =>
            $"{PluginId}: adopted {Adopted}/{Offered}",
        BundleAdoptionKind.Adopted =>
            $"{PluginId}: adopted only {Adopted}/{Offered} — the rest compile here",
        BundleAdoptionKind.NothingToAdopt =>
            $"{PluginId}: no NodeTypes to adopt"
            + (string.IsNullOrWhiteSpace(Reason) ? string.Empty : $" ({Reason})"),
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
        // 🚨 The attempts that offered nothing are NAMED in the denominator rather than silently
        // dropped from it. "25 attempts, 25 adopted, no misses" over a population where 13 carried
        // no NodeTypes invites the reader to conclude 25 packages were served; saying how many had
        // nothing to serve is what makes the remaining number readable.
        var nothing = all.Count(o => o.Kind is BundleAdoptionKind.NothingToAdopt);
        var nothingSuffix = nothing == 0
            ? string.Empty
            : $" ({nothing} carried no NodeTypes to adopt)";
        if (misses.Length == 0)
            return $"{all.Count} adoption attempt(s), {adopted} assembly/assemblies adopted, "
                + $"no misses{nothingSuffix}";

        return $"{all.Count} adoption attempt(s), {adopted} assembly/assemblies adopted{nothingSuffix}, "
            + $"{misses.Length} MISS(es) — content the registry was meant to serve is compiled "
            + "here instead: "
            + string.Join("; ", misses.Take(Math.Max(1, maxNamed)).Select(m => m.Describe()))
            + (misses.Length > maxNamed ? $", …(+{misses.Length - maxNamed})" : string.Empty);
    }
}
