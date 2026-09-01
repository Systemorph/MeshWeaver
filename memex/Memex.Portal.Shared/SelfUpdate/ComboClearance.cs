using System.Collections.Immutable;
using MeshWeaver.PluginCatalog;

namespace Memex.Portal.Shared.SelfUpdate;

/// <summary>
/// What the combo gate says about ONE candidate for THIS instance — the roll-side reading of a
/// <see cref="ComboVerification"/>.
///
/// <para>🚨 <b>The three verdicts are never conflated, and neither are the two ways there can be no
/// answer.</b> <see cref="ComboVerdictKind.Green"/> is the ONLY thing that produces
/// <see cref="Cleared"/> — an absent gate, an absent verdict, a faulted producer and a
/// <see cref="ComboVerdictKind.NotVerifiable"/> verdict all land on a state that grants nothing.
/// That is the anti-trapdoor property of this type: no configuration, no missing service and no
/// exception can manufacture a clearance.</para>
/// </summary>
public enum ComboClearanceKind
{
    /// <summary>
    /// 🚨 <b>Nobody has asked.</b> No verdict exists for this candidate on this instance — the
    /// combo gate is not registered on this host, or no producer has ever run
    /// (<c>mw-combo-verify</c>, per <c>Doc/Architecture/CandidateReleaseProtocol</c>).
    ///
    /// <para>Neither a pass nor a refusal. It grants NO clearance, so it can never read as Green;
    /// it refuses nothing, because refusing on it would freeze every instance in the fleet on the
    /// day this gate shipped — the exact "fail-closed rule drawn one state too wide" that
    /// <c>ReleaseGateApplicabilityTest</c> exists to prevent. The roll then rests on the other
    /// gates, and the fact is recorded and logged so it is visible rather than inferred.</para>
    /// </summary>
    NotVerified,

    /// <summary>
    /// 🚨 <b>The gate ran and could not answer</b> (<see cref="ComboVerdictKind.NotVerifiable"/>):
    /// a module could not be materialised at its recorded ref, the gate could not run, or it
    /// produced no structured evidence.
    ///
    /// <para>Also neither a pass nor a refusal, and deliberately DISTINCT from
    /// <see cref="NotVerified"/> in its reason — "we tried and could not find out" and "nothing has
    /// tried" are different incidents with different fixes, and an operator has to be able to tell
    /// them apart from the recorded text alone.</para>
    /// </summary>
    Unverifiable,

    /// <summary>
    /// The gate ran and at least one module of this instance's combo FAILS against the candidate
    /// (<see cref="ComboVerdictKind.Red"/>). This is the refusal: the roll is HELD and every
    /// failing module is named.
    /// </summary>
    Refused,

    /// <summary>
    /// Every module of the combo compiled, rendered and tested green against the candidate
    /// (<see cref="ComboVerdictKind.Green"/>). The ONLY state that grants clearance — and it still
    /// carries the verdict's caveats, because a Green over a moving pin is not an unqualified pass.
    /// </summary>
    Cleared,
}

/// <summary>
/// The combo gate's answer for one candidate tag: the state, the sentence an operator reads on
/// <c>Admin/UpdatePolicy</c>, and the verdict it was folded from (null when there was none).
///
/// <para>Pure — no hub, no logger, no Rx — so the four states and their wording are pinned by unit
/// tests rather than re-derived from an integration run. The reasons are GATE DIAGNOSTICS and are
/// deliberately not localized, the same call <c>InstanceComboReader.ProvenanceCaveat</c> makes:
/// module ids, tags, digests and failure text are machine text, and the surfaces that render them
/// localize the labels around them.</para>
/// </summary>
/// <param name="Kind">The state.</param>
/// <param name="CandidateTag">The candidate this answer is about.</param>
/// <param name="Reason">One sentence, naming what was found and (for a refusal) every module that
/// blocks it.</param>
/// <param name="Verdict">The recorded verdict this was folded from; null for
/// <see cref="ComboClearanceKind.NotVerified"/>.</param>
public sealed record ComboClearance(
    ComboClearanceKind Kind,
    string CandidateTag,
    string Reason,
    ComboVerification? Verdict)
{
    /// <summary>The roll is REFUSED — a Red verdict, and the only state that blocks.</summary>
    public bool Refuses => Kind == ComboClearanceKind.Refused;

    /// <summary>The candidate is CLEARED — a Green verdict, and the only state that grants.</summary>
    public bool IsCleared => Kind == ComboClearanceKind.Cleared;

    /// <summary>
    /// Folds a recorded verdict (or its absence) into the roll-side answer.
    /// </summary>
    /// <param name="candidateTag">The candidate.</param>
    /// <param name="verdict">The verdict recorded for it, or null when none exists.</param>
    /// <param name="absence">Why there is no verdict — names whether the gate is registered at
    /// all. Only read when <paramref name="verdict"/> is null.</param>
    public static ComboClearance For(
        string candidateTag, ComboVerification? verdict, string? absence = null)
    {
        if (verdict is null)
            return new(
                ComboClearanceKind.NotVerified,
                candidateTag,
                $"no combo verification has been recorded for '{candidateTag}' on this instance"
                + (string.IsNullOrWhiteSpace(absence) ? "" : $" ({absence})")
                + " — nothing has checked whether that image can serve the modules this instance "
                + "runs. Produce one with mw-combo-verify (Doc/Architecture/CandidateReleaseProtocol) "
                + "and it will be honoured on the next check.",
                null);

        var ran = $"verified {verdict.VerifiedAt:u}"
                  + (verdict.VerifiedPlatform is null ? "" : $" for {verdict.VerifiedPlatform}")
                  + (verdict.ImageDigest is null ? "" : $" at {verdict.ImageDigest}");

        return verdict.Verdict switch
        {
            ComboVerdictKind.Green => new(
                ComboClearanceKind.Cleared,
                candidateTag,
                $"combo verification is GREEN for '{candidateTag}': all {verdict.Modules.Count} "
                + $"module(s) compiled, rendered and tested against the candidate ({ran})."
                + Caveats(verdict),
                verdict),

            ComboVerdictKind.Red => new(
                ComboClearanceKind.Refused,
                candidateTag,
                $"combo verification is RED for '{candidateTag}': "
                + $"{verdict.FailedModules.Count} module(s) this instance runs fail against that "
                + $"image — {FailingModules(verdict)} ({ran})."
                + Caveats(verdict),
                verdict),

            // 🚨 The default arm is NotVerifiable, and it must stay the default: a ComboVerdictKind
            // member added later must land here, never on Cleared. An `is not Red` test would have
            // cleared it instead.
            _ => new(
                ComboClearanceKind.Unverifiable,
                candidateTag,
                $"combo verification for '{candidateTag}' could NOT answer ({ran}) — "
                + $"{NotVerifiedModules(verdict)}."
                + Caveats(verdict),
                verdict),
        };
    }

    /// <summary>Every failing module with its first failure line — breadth-complete, never
    /// truncated: one broken module must not hide another.</summary>
    private static string FailingModules(ComboVerification verdict) =>
        verdict.FailedModules.Count == 0
            ? "(the verdict names no module — read Admin/UpdatePolicy for the full record)"
            : string.Join("; ", verdict.FailedModules.Select(m =>
                $"'{m.ModuleId}': {First(m.Failures)}"));

    /// <summary>The modules a NotVerifiable verdict could not evaluate, with the reason each
    /// carries. Modules that merely rode along carry none and are counted rather than listed.</summary>
    private static string NotVerifiedModules(ComboVerification verdict)
    {
        var named = verdict.Modules
            .Where(m => m.Outcome == ModuleVerificationOutcome.NotVerified && m.Failures.Count > 0)
            .Select(m => $"'{m.ModuleId}': {First(m.Failures)}")
            .ToImmutableList();
        return named.Count > 0
            ? string.Join("; ", named)
            : $"{verdict.Modules.Count} module(s) were left unverified";
    }

    /// <summary>
    /// 🚨 The caveats, on EVERY verdict including a Green. <see cref="ComboVerification.Caveats"/>
    /// documents them as mandatory-to-surface: a Green resolved over a moving pin, or one whose
    /// input diverged from the install record, is not an unqualified pass and must never read as
    /// one.
    /// </summary>
    private static string Caveats(ComboVerification verdict) =>
        verdict.Caveats.Count == 0
            ? ""
            : " Caveats: " + string.Join(" | ", verdict.Caveats);

    private static string First(ImmutableList<string> failures) =>
        failures.Count == 0 ? "(no detail recorded)" : failures[0];
}
