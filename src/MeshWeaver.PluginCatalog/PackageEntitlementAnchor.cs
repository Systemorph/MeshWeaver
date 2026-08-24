using System.Collections.Immutable;

namespace MeshWeaver.PluginCatalog;

/// <summary>
/// WHERE the <c>(source, package)</c> binding an entitlement decision was made against came from.
///
/// <para>🚨 This exists so that a CACHE can say it is a cache. Before #1782 gap 2 the only binding
/// available was the local install record's <see cref="PackageManifest.Source"/>, and it was read
/// as if it were the authority — so a package with no record had no binding, and "I cannot tell
/// which source this is from" came out as "you are not entitled to it". The provenance is now part
/// of every answer, because an unlabelled cache silently becomes the anchor again by accident.</para>
/// </summary>
public enum EntitlementAnchorKind
{
    /// <summary>The binding came from the REGISTRY — the configured package sources this instance
    /// serves, i.e. the same catalog <c>/api/plugins</c> lists. This is the anchor.</summary>
    Registry,

    /// <summary>The binding came from a LOCAL observation — an install record's stamped source, or
    /// a published module's declared package path. Trustworthy, but a cache: it says where the
    /// package came from when it was last observed here, not where the registry carries it now.</summary>
    Cache,

    /// <summary>There was no binding at all: the registry does not carry the package and nothing
    /// local ever observed it.</summary>
    None,
}

/// <summary>
/// The THREE answers an entitlement question can have.
///
/// <para>🚨 <b>Two of them are not "no".</b> A check whose inability to answer is indistinguishable
/// from a negative answer is the failure mode this codebase found eight instances of on 2026-08-21
/// (<c>if: vars.X != ''</c> skipping a gate green; a health check Healthy while the bake was
/// NotStarted; <c>bundle is null</c> returning a bare <c>0</c>). Applied to entitlement it is the
/// most expensive version of that bug — a purchase that reads as no purchase.</para>
/// </summary>
public enum EntitlementOutcome
{
    /// <summary>The caller may have the package.</summary>
    Granted,

    /// <summary>The caller may NOT have it, and that is a real observation: a binding was found and
    /// the caller's grant does not cover it (or the registry was asked in full and carries no such
    /// package, with nothing local ever having observed one).</summary>
    Denied,

    /// <summary>🚨 <b>The third state.</b> The anchor could not be consulted and nothing local ever
    /// observed this package, so entitlement is UNKNOWN. On the wire it is still a refusal — the
    /// bytes are not served — but it is a STATED one: recorded, counted and surfaced as degraded,
    /// never asserted as "not entitled".</summary>
    Indeterminate,
}

/// <summary>
/// One entitlement question, answered — with the provenance of the binding it was answered from.
/// </summary>
/// <param name="PackageId">The package that was asked about.</param>
/// <param name="Outcome">The answer.</param>
/// <param name="Anchor">Where the binding came from.</param>
/// <param name="Source">The registry source the package was bound to, when there was one.</param>
/// <param name="AnchorAvailable">Whether the authoritative anchor answered IN FULL — every
/// configured source listed. False means an ABSENCE from the anchor proves nothing, which is
/// exactly why an absence must not deny.</param>
/// <param name="Reason">One sentence, for a log line or a ledger entry.</param>
public sealed record EntitlementDecision(
    string PackageId,
    EntitlementOutcome Outcome,
    EntitlementAnchorKind Anchor,
    string? Source,
    bool AnchorAvailable,
    string Reason)
{
    /// <summary>Whether the bytes are served. ONLY <see cref="EntitlementOutcome.Granted"/> serves —
    /// the third state withholds them like a denial does, and differs in what it CLAIMS, not in
    /// what it hands over.</summary>
    public bool Serves => Outcome == EntitlementOutcome.Granted;

    /// <summary>Whether this answer came from the anchor rather than from the cache.</summary>
    public bool IsAuthoritative => Anchor == EntitlementAnchorKind.Registry;

    /// <summary>
    /// Whether this answer was reached in a DEGRADED way — the anchor could not be consulted in
    /// full, so the decision rests on a cached observation, or on nothing at all. Not a failure:
    /// a degraded grant is the correct, deliberately non-blocking answer for a caller whose
    /// entitlement was previously observed. It is reported so the degradation is legible rather
    /// than inferred from a quiet day.
    /// </summary>
    public bool IsDegraded => !AnchorAvailable || Outcome == EntitlementOutcome.Indeterminate;

    /// <summary>One line, for a log or a health payload.</summary>
    public string Describe() =>
        $"{PackageId}: {Outcome} (anchor={Anchor}"
        + (Source is { Length: > 0 } source ? $", source='{source}'" : string.Empty)
        + (AnchorAvailable ? string.Empty : ", registry NOT reachable in full")
        + $") — {Reason}";
}

/// <summary>
/// 🚨 <b>THE ENTITLEMENT ANCHOR (#1782 gap 2).</b> Decides whether a caller may pull a package,
/// from a <c>(source, package)</c> binding whose AUTHORITY is the registry — with the local install
/// record demoted to what it always was, a cache.
///
/// <para><b>The decision recorded on #1782 (maintainer, 2026-08-22):</b></para>
/// <code>
/// anchor:   the entitlement record at the registry
/// local:    install record = cache
/// absent:   "ask upstream" — never "not entitled"
/// </code>
///
/// <para>Entitlement is a fact about the ACCOUNT, not about which instance happens to be serving.
/// Anchoring it on the serving instance's install records is what denied a paying customer on a
/// fresh instance simply because nothing had installed there yet — and it is what made gap 2
/// unfixable, since a package the registry has not itself installed has no record to bind to.</para>
///
/// <para>🚨 <b>What this does NOT weaken (#1777).</b> The decision is still
/// <c>PluginGrant.Allows(source, packageId)</c> against the admin-owned grant the caller's instance
/// key resolves to — the same one match <c>/api/plugins</c> and <c>InstallByDefault</c> make. What
/// changed is only WHERE the <c>source</c> half comes from: the registry's own catalog first, the
/// install record as a fallback. No source is ever invented, no second notion of entitlement is
/// introduced, and the wire behaviour is unchanged — a non-granting outcome answers exactly the one
/// refusal <c>NoSuchBundle()</c> the caller cannot distinguish from a package that does not exist.
/// If anything it TIGHTENS: a published module's self-declared package path used to be believed
/// outright, and is now overridden by the registry's binding whenever the registry carries the
/// package.</para>
///
/// <para>Pure and total: no hub, no I/O, no clock. The registry read that FEEDS it lives in
/// <see cref="PackageOriginAnchor"/>, behind the sanctioned async boundary.</para>
/// </summary>
public static class PackageEntitlementAnchor
{
    /// <summary>
    /// Resolves one package for one caller.
    /// </summary>
    /// <param name="packageId">The package being asked for.</param>
    /// <param name="anchorSource">The source the REGISTRY binds this package to, or null when the
    /// registry does not carry it (or could not be asked).</param>
    /// <param name="cachedSource">The source a LOCAL observation binds it to — an install record's
    /// stamped source, or a published module's declared package path — or null when there is none.
    /// 🚨 Its absence is never a denial; it simply means the answer must come from the anchor.</param>
    /// <param name="anchorAvailable">Whether every configured source answered. False ⇒ an ABSENCE
    /// from <paramref name="anchorSource"/> proves nothing.</param>
    /// <param name="allows">The caller's grant, as a predicate over the resolved source. This is
    /// <c>AuthenticatedInstance.Allows(source, packageId)</c> and nothing else — the anchor decides
    /// which source is asked about, never whether the grant covers it.</param>
    public static EntitlementDecision Resolve(
        string packageId,
        string? anchorSource,
        string? cachedSource,
        bool anchorAvailable,
        Func<string, bool> allows)
    {
        ArgumentNullException.ThrowIfNull(allows);

        // 1 — THE ANCHOR ANSWERED. A binding the registry itself carries is authoritative whether or
        //     not every OTHER source could be listed: a listing is an observation, and one source
        //     being down cannot make another source's answer less true. This branch also overrides a
        //     disagreeing cache, which is the whole point of having an anchor.
        if (anchorSource is { Length: > 0 } fromRegistry)
            return new EntitlementDecision(
                packageId,
                allows(fromRegistry) ? EntitlementOutcome.Granted : EntitlementOutcome.Denied,
                EntitlementAnchorKind.Registry,
                fromRegistry,
                anchorAvailable,
                $"the registry carries '{packageId}' in source '{fromRegistry}', and the caller's "
                + $"grant {(allows(fromRegistry) ? "covers" : "does not cover")} it");

        // 2 — THE CACHE ANSWERED. Either the registry does not carry it (a package installed from a
        //     source that is no longer configured, or published straight onto this instance) or the
        //     registry could not be asked. A local observation is a real observation: it is what a
        //     previous, successful resolution wrote down. Answering from it is what "fail toward not
        //     blocking a viewer whose entitlement was previously observed" means in practice.
        if (cachedSource is { Length: > 0 } fromCache)
            return new EntitlementDecision(
                packageId,
                allows(fromCache) ? EntitlementOutcome.Granted : EntitlementOutcome.Denied,
                EntitlementAnchorKind.Cache,
                fromCache,
                anchorAvailable,
                anchorAvailable
                    ? $"the registry does not carry '{packageId}'; a local record binds it to source "
                      + $"'{fromCache}', and the caller's grant "
                      + $"{(allows(fromCache) ? "covers" : "does not cover")} it"
                    : $"the registry could not be consulted in full; the last local observation binds "
                      + $"'{packageId}' to source '{fromCache}', and the caller's grant "
                      + $"{(allows(fromCache) ? "covers" : "does not cover")} it");

        // 3 — NOBODY BINDS IT, and the registry was asked IN FULL. That is a real negative: this
        //     registry carries no such package and nothing here ever installed one. A caller that is
        //     genuinely not entitled must still see nothing, and this is the branch that says so.
        if (anchorAvailable)
            return new EntitlementDecision(
                packageId, EntitlementOutcome.Denied, EntitlementAnchorKind.None, null, true,
                $"no configured source carries '{packageId}' and no local record observes it — "
                + "there is nothing here to be entitled to");

        // 4 — 🚨 THE THIRD STATE. The anchor could not be consulted and nothing was ever observed
        //     locally, so entitlement is UNKNOWN. The bytes are withheld (there is no answer to
        //     serve from), but the outcome is not a denial and must never be reported as one: an
        //     air-gapped or cut-off instance being unable to ASK is not the customer failing to buy.
        return new EntitlementDecision(
            packageId, EntitlementOutcome.Indeterminate, EntitlementAnchorKind.None, null, false,
            $"the registry could not be consulted and nothing here has ever observed '{packageId}', "
            + "so entitlement is UNKNOWN — this is not a denial");
    }
}

/// <summary>
/// 🚨 <b>The record that makes a degraded entitlement answer legible instead of inferred</b>
/// (#1782 gap 2).
///
/// <para>Every refusal on the bundle routes is byte-identical on the wire (#1777), which is exactly
/// right for the caller and exactly wrong for the operator: "not granted", "no such package" and
/// "I could not reach the registry to find out" all leave the same trace. The log distinguishes
/// them, but a log line is not countable after it rotates, and the outcome that matters most —
/// the third state — is the one that looks most like a quiet day.</para>
///
/// <para>So decisions are RECORDED. Process-scoped, bounded, appended under
/// <see cref="ImmutableInterlocked"/>. Same shape and the same rules as
/// <see cref="BundleAdoptionLedger"/> — a diagnostic, never a source of truth; nothing decides
/// anything from it.</para>
/// </summary>
public sealed class PackageEntitlementLedger
{
    /// <summary>How many decisions are kept — the last N answer "is the anchor working now", which
    /// is the question, while an unbounded list would answer it and also leak.</summary>
    public const int Capacity = 500;

    private ImmutableList<EntitlementDecision> decisions = ImmutableList<EntitlementDecision>.Empty;

    /// <summary>Records one decision. Thread-safe; never throws.</summary>
    public void Record(EntitlementDecision decision)
    {
        ArgumentNullException.ThrowIfNull(decision);
        ImmutableInterlocked.Update(ref decisions, current =>
        {
            var next = current.Add(decision);
            return next.Count > Capacity ? next.RemoveRange(0, next.Count - Capacity) : next;
        });
    }

    /// <summary>Every recorded decision, oldest first.</summary>
    public ImmutableList<EntitlementDecision> Decisions => Volatile.Read(ref decisions);

    /// <summary>The decisions reached without a full authoritative answer — the degraded ones.</summary>
    public ImmutableList<EntitlementDecision> Degraded =>
        Decisions.Where(d => d.IsDegraded).ToImmutableList();

    /// <summary>The decisions that could not be answered at all.</summary>
    public ImmutableList<EntitlementDecision> Indeterminate =>
        Decisions.Where(d => d.Outcome == EntitlementOutcome.Indeterminate).ToImmutableList();

    /// <summary>
    /// The one line every surface renders.
    ///
    /// <para>🚨 "Nothing was ever asked" and "everything was answered authoritatively" are DIFFERENT
    /// sentences. An instance that serves no bundles never resolves an entitlement, and reporting
    /// that as a clean sweep would make the absence of the lane look like the success of it — the
    /// same rule <see cref="BundleAdoptionLedger.Describe"/> follows.</para>
    /// </summary>
    /// <param name="maxNamed">How many degraded decisions are named before the line truncates.</param>
    public string Describe(int maxNamed = 10)
    {
        var all = Decisions;
        if (all.IsEmpty)
            return "no package entitlement has been resolved in this process";

        var degraded = all.Where(d => d.IsDegraded).ToArray();
        if (degraded.Length == 0)
            return $"{all.Count} entitlement decision(s), all answered against the registry anchor";

        var unknown = degraded.Count(d => d.Outcome == EntitlementOutcome.Indeterminate);
        return $"{all.Count} entitlement decision(s), {degraded.Length} reached WITHOUT a full "
            + $"registry answer ({unknown} could not be answered at all — those are UNKNOWN, not "
            + "denials): "
            + string.Join("; ", degraded.Take(Math.Max(1, maxNamed)).Select(d => d.Describe()))
            + (degraded.Length > maxNamed ? $", …(+{degraded.Length - maxNamed})" : string.Empty);
    }
}
