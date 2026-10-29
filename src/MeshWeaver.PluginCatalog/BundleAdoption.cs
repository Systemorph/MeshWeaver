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

    /// <summary>
    /// 🚨 The attempt was NOT MADE because this process runs a closed type set
    /// (<c>Mesh:ClosedTypeSet=true</c>, <c>Doc/Architecture/ClosedTypeSet</c>): it adopts no database
    /// NodeType and compiles none in its place, so nothing was fetched. <b>Not a miss</b> — and
    /// deliberately not <see cref="NothingToAdopt"/> either, which is a statement about a bundle that
    /// WAS read and declared no NodeTypes. This one says nothing about the package's contents (it
    /// may carry many NodeTypes); it says the policy made the question moot, and the ledger renders
    /// it as such rather than folding it into "carried no NodeTypes".
    /// </summary>
    NotApplicable,
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
    /// <para>Two things dominate every declaration, and both are misses:</para>
    /// <list type="number">
    /// <item>unresolved producer <c>Misses</c> — the bake could not resolve types it was asked for,
    /// so the bundle is short whatever else it carries;</item>
    /// <item>🚨 a non-empty <c>Assemblies</c> list. The manifest DECLARED NodeType assemblies and
    /// none came out, which is a torn bundle, not a module-only one.
    /// <see cref="BundleReader.Read(System.IO.Stream, System.Collections.Generic.IReadOnlySet{string})"/>
    /// skips a declared assembly whose archive entry is absent (<c>if (dll is null) continue;</c>)
    /// — silently, and without recording a miss. A MIXED package (content + NodeTypes + a module)
    /// whose assembly entries went missing therefore arrives here with zero payloads, a positive
    /// <c>Module</c> declaration and no <c>Misses</c>, and reading that as "nothing to adopt" would
    /// hide a genuinely missing NodeType — from <c>Modules:RequirePrebuilt</c> above all, which
    /// exists to refuse exactly that. Found in review on #4079.</item>
    /// </list>
    /// </summary>
    /// <param name="manifest">The bundle's manifest, or null from an unreadable bundle.</param>
    public static BundleAdoptionKind ClassifyEmpty(BundleReader.Manifest? manifest)
        // The rule itself lives beside the manifest (BundleReader.DeclaresNothingToAdopt), so the
        // shipped-bundle discovery in MeshWeaver.Hosting reads the SAME rule (#7015 review).
        => BundleReader.DeclaresNothingToAdopt(manifest)
            ? BundleAdoptionKind.NothingToAdopt
            : BundleAdoptionKind.NoAssemblies;

    /// <summary>How many of the producer's stated miss reasons <see cref="StatedReasons"/> names
    /// before it truncates — enough to name the cause on <c>/health</c> without a package with many
    /// NodeTypes drowning every other line.</summary>
    public const int StatedReasonsShown = 3;

    /// <summary>
    /// The suffix an empty bundle's <see cref="BundleAdoptionKind.NoAssemblies"/> outcome carries:
    /// every reason the producer STATED in <c>Misses</c> — the NodeType misses as well as the
    /// module ones — or the empty string when it stated none.
    ///
    /// <para>🚨 MeshWeaver#7181: only the module misses used to be named, so a package whose
    /// NodeTypes the registry could not serve (never compiled there, bytes evicted, no artifact
    /// for the lane) rendered on <c>/health</c> as a bare "the bundle carried no assemblies" —
    /// word for word what a producer that shipped an empty archive renders. The reason the
    /// producer gave travels to the one surface the delivery lane is triaged from.</para>
    /// </summary>
    /// <param name="manifest">The bundle's manifest, or null from an unreadable bundle.</param>
    public static string StatedReasons(BundleReader.Manifest? manifest)
    {
        var stated = manifest?.Misses ?? [];
        if (stated.Count == 0)
            return string.Empty;
        return " — the registry says: " + string.Join(" | ", stated.Take(StatedReasonsShown))
               + (stated.Count > StatedReasonsShown
                   ? $" …(+{stated.Count - StatedReasonsShown})"
                   : string.Empty);
    }

    /// <summary>
    /// The producer's <c>module '&lt;name&gt;': not served — …</c> misses
    /// (<see cref="BundleReader.IsModuleNotServedMiss"/>) — empty when it stated none.
    ///
    /// <para>🚨 MeshWeaver#7401: a MIXED package (NodeTypes AND a runtime module) whose module the
    /// registry declared but could not serve arrives with its NodeType assemblies, so it never
    /// reaches the empty-bundle branch that names this reason. The miss was filtered out of the
    /// NodeType coverage check (correctly — it names no NodeType) and then dropped, so the adoption
    /// was recorded as a clean <see cref="BundleAdoptionKind.Adopted"/> and <c>/health</c> could not
    /// see that the package's module was absent. The adoption outcome now carries these, whatever
    /// else the bundle held.</para>
    /// </summary>
    /// <param name="manifest">The bundle's manifest, or null from an unreadable bundle.</param>
    public static ImmutableArray<string> ModulesNotServed(BundleReader.Manifest? manifest) =>
        manifest?.Misses is { Count: > 0 } misses
            ? misses.Where(BundleReader.IsModuleNotServedMiss).ToImmutableArray()
            : ImmutableArray<string>.Empty;

    /// <summary>
    /// The outcome of a bundle whose NodeType assemblies arrived and were seeded — the ONE place
    /// <see cref="PluginBundleClient"/> builds it, so the module-not-served misses the manifest
    /// states travel with it (#7401) instead of being dropped with the NodeType-coverage filter.
    /// </summary>
    /// <param name="pluginId">The package.</param>
    /// <param name="registry">The registry asked.</param>
    /// <param name="adopted">Assemblies actually seeded.</param>
    /// <param name="offered">Assemblies the bundle carried.</param>
    /// <param name="manifest">The bundle's manifest.</param>
    public static BundleAdoptionOutcome AdoptedOutcome(
        string pluginId, string registry, int adopted, int offered, BundleReader.Manifest? manifest)
        => new(pluginId, BundleAdoptionKind.Adopted, registry, Adopted: adopted, Offered: offered)
        {
            ModulesNotServed = ModulesNotServed(manifest),
        };

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
    /// The producer's <c>module '&lt;name&gt;': not served — …</c> misses carried by a bundle whose
    /// NodeType assemblies DID arrive (<see cref="BundleOffering.ModulesNotServed"/>, #7401). Empty
    /// otherwise. Any entry makes the attempt a miss: the package's runtime module is absent here,
    /// and nothing compiles in its place.
    ///
    /// <para>An INIT property rather than a positional parameter: adding one to the primary
    /// constructor would replace its signature for every assembly compiled against it.</para>
    /// </summary>
    public ImmutableArray<string> ModulesNotServed { get; init; } = ImmutableArray<string>.Empty;

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
        Kind is not (BundleAdoptionKind.Adopted or BundleAdoptionKind.NothingToAdopt
            or BundleAdoptionKind.NotApplicable)
        || Adopted < Offered
        || !ModulesNotServed.IsDefaultOrEmpty;

    /// <summary>One line, for a log or a health payload.</summary>
    public string Describe() => Kind switch
    {
        BundleAdoptionKind.Adopted when Adopted >= Offered =>
            $"{PluginId}: adopted {Adopted}/{Offered}" + ModuleSuffix(),
        BundleAdoptionKind.Adopted =>
            $"{PluginId}: adopted only {Adopted}/{Offered} — the rest compile here" + ModuleSuffix(),
        BundleAdoptionKind.NothingToAdopt =>
            $"{PluginId}: no NodeTypes to adopt"
            + (string.IsNullOrWhiteSpace(Reason) ? string.Empty : $" ({Reason})"),
        BundleAdoptionKind.NotApplicable =>
            $"{PluginId}: not attempted"
            + (string.IsNullOrWhiteSpace(Reason) ? string.Empty : $" ({Reason})"),
        _ => $"{PluginId}: {Kind}"
             + (string.IsNullOrWhiteSpace(Reason) ? string.Empty : $" ({Reason})"),
    };

    private string ModuleSuffix() => ModulesNotServed.IsDefaultOrEmpty
        ? string.Empty
        : " — but its runtime module is ABSENT here (nothing compiles in its place): "
          + string.Join(" | ", ModulesNotServed);
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
        // The policy-skipped attempts are named APART: they fetched nothing, so they say nothing
        // about what the packages carry — folding them into "carried no NodeTypes" would state a
        // denominator nobody measured.
        var notApplicable = all.Where(o => o.Kind is BundleAdoptionKind.NotApplicable).ToArray();
        var nothingSuffix = (nothing == 0 ? string.Empty : $" ({nothing} carried no NodeTypes to adopt)")
            + (notApplicable.Length == 0
                ? string.Empty
                : $" ({notApplicable.Length} not attempted: "
                  + $"{notApplicable[0].Reason ?? "adoption does not apply to this process"})");
        if (misses.Length == 0)
            return $"{all.Count} adoption attempt(s), {adopted} assembly/assemblies adopted, "
                + $"no misses{nothingSuffix}";

        return $"{all.Count} adoption attempt(s), {adopted} assembly/assemblies adopted{nothingSuffix}, "
            + $"{misses.Length} MISS(es) — content the registry was meant to serve did not arrive "
            + "(a NodeType compiles here instead; a module that was not served is absent): "
            + string.Join("; ", misses.Take(Math.Max(1, maxNamed)).Select(m => m.Describe()))
            + (misses.Length > maxNamed ? $", …(+{misses.Length - maxNamed})" : string.Empty);
    }
}
