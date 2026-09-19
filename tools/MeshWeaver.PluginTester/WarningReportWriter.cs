using System.Collections.Immutable;
using MeshWeaver.Compiler;

namespace MeshWeaver.PluginTester;

/// <summary>
/// How a bake REPORTS the warnings its NodeType compiles produced — the aggregate, the actionable
/// sites, and the two ratchets' verdicts.
///
/// <para>🚨 <b>Aggregate, never a transcript.</b> The build doctrine is "CI logs warn/error +
/// verdicts only" (Doc/Architecture/ModuleBuildArchitecture), and a warning printed once per
/// occurrence violates it twice over: a <c>Source/*.cs</c> shared by eight NodeTypes reports its
/// one missing doc comment eight times, and the runtime's per-compile cap
/// (<c>EmitPipeline.MaxReportedWarnings</c> = 50) would still let a 150-type bake write 7,500
/// lines. So the raw occurrences are folded to distinct SITES, counted by diagnostic id, and only
/// the sites a reader has to ACT on — the ones the baseline does not carry — are named in full.
/// <c>MW_LOG_LEVEL=Information</c> restores every site, exactly as it restores every other held-back
/// line (<see cref="GateVerbosity"/>).</para>
///
/// <para>Shared by both bake verbs (<c>compile</c> and <c>build</c>) so the two producers cannot
/// report the same measurement differently — the same rule that keeps their compiles identical.</para>
/// </summary>
public static class WarningReportWriter
{
    /// <summary>The prefix every line carries, so one grep lifts the whole block out of a run.</summary>
    public const string Prefix = "warnings:";

    /// <summary>
    /// Writes the inventory and both verdicts.
    ///
    /// <para>🚨 It REPORTS and returns nothing. The verdict is
    /// <c>WarningRatchet.Success</c>, folded once on the report the caller already carries
    /// (<c>TreeBake.Report.WarningsAccepted</c> / <c>CascadeBuild.Report.WarningsAccepted</c>) and
    /// read from there by the exit code. Handing back a second "is it green" from the RENDERER
    /// would give the run two ways to answer one question — and a gate whose two halves can
    /// disagree about what failed is worse than one that names the wrong cause (#1077, the same
    /// lesson <c>NodeTypeResult.Success</c> and <c>GateVerdict.Headline</c> learned when they were
    /// allowed to disagree).</para>
    /// </summary>
    /// <param name="output">Where the lines go.</param>
    /// <param name="inventory">What this bake measured.</param>
    /// <param name="baseline">The debt this repo carries, or <see cref="WarningBaseline.ObserveOnly"/>.</param>
    /// <param name="ratchets">The evaluated ratchets, in report order.</param>
    public static void Write(
        TextWriter output,
        WarningInventory inventory,
        WarningBaseline baseline,
        IReadOnlyList<WarningRatchet> ratchets)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(inventory);
        ArgumentNullException.ThrowIfNull(baseline);
        ArgumentNullException.ThrowIfNull(ratchets);

        WriteInventory(output, inventory);
        WriteInert(output, baseline);
        foreach (var ratchet in ratchets)
            WriteRatchet(output, inventory, baseline, ratchet);
    }

    /// <summary>
    /// Baseline entries naming a code the compile no longer reports — one line, naming the codes
    /// and the count, so an inert line gets DELETED instead of sitting unread for ever. Never a
    /// failure: see <see cref="WarningBaseline.Inert"/> for why retiring a code must not be able to
    /// red a repo that has not trimmed its file yet.
    /// </summary>
    private static void WriteInert(TextWriter output, WarningBaseline baseline)
    {
        var inert = baseline.Inert.ToList();
        if (inert.Count == 0)
            return;
        var codes = inert
            .Select(e => e.Code)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(c => c, StringComparer.Ordinal);
        output.WriteLine(
            $"{Prefix} {inert.Count} INERT baseline entr(ies) naming "
            + string.Join(", ", codes)
            + " — the in-mesh compile no longer reports these (CompileWarning.NotReported: "
            + "reference-set skew, which a project build does not produce, and doc completeness, "
            + "which core's src/ NoWarn suppresses). They tolerate "
            + "nothing and fail nothing. Delete the lines.");
    }

    /// <summary>Evaluates both ratchets, in report order: latent bugs first, doc debt second.</summary>
    /// <param name="inventory">What this bake measured.</param>
    /// <param name="baseline">The debt this repo carries.</param>
    public static IReadOnlyList<WarningRatchet> Evaluate(
        WarningInventory inventory, WarningBaseline baseline) =>
    [
        WarningRatchet.Evaluate(WarningClass.Real, inventory, baseline),
        WarningRatchet.Evaluate(WarningClass.DocComment, inventory, baseline),
    ];

    /// <summary>
    /// The totals and the per-code table — the two lines that say what the shape IS without
    /// scrolling. Printed on every run, including a clean one: "I measured, and it was clean" and
    /// "I measured nothing" are two different printed sentences.
    /// </summary>
    private static void WriteInventory(TextWriter output, WarningInventory inventory)
    {
        var types = inventory.Pairs
            .Select(p => p.Scope)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();
        output.WriteLine(
            $"{Prefix} {inventory.Occurrences} raw occurrence(s) folded to {inventory.Sites.Length} "
            + $"distinct site(s), over {types} of {inventory.CompiledTypes.Count} compiled type(s)");
        if (inventory.Sites.IsEmpty)
            return;
        // ONE line, every code, biggest first: the shape of the debt in a single read.
        output.WriteLine(
            $"{Prefix} by code — " + string.Join(" · ", inventory.ByCode()
                .Select(c => $"{c.Code} {c.Occurrences}× / {c.Sites} site(s) / {c.Types} type(s)")));
    }

    /// <summary>One ratchet's verdict, plus the lines a reader has to act on.</summary>
    private static void WriteRatchet(
        TextWriter output,
        WarningInventory inventory,
        WarningBaseline baseline,
        WarningRatchet ratchet)
    {
        var owned = baseline.For(ratchet.Class).Count();
        if (!ratchet.Enforced)
        {
            // 🚨 Observe-only is PRINTED, never inferred from silence. A run that enforced nothing
            // must not read like a run that found nothing — that is the whole "a gate that cannot
            // judge must not look like a gate that passed" rule, stated by the gate itself.
            var pending = ratchet.Known.Length + ratchet.New.Length;
            output.WriteLine(
                $"{Prefix} {ratchet.Name} ratchet — OBSERVE-ONLY: no --warning-baseline was given, "
                + $"so NOTHING is enforced. {pending} (type, code) pair(s) would have to be "
                + "baselined to arm it"
                + (pending == 0 || GateVerbosity.Verbose
                    ? "."
                    : " — re-run with MW_LOG_LEVEL=Information to have them printed as baseline "
                      + "lines to paste."));
            // The pairs, ready to paste — this mode's ONE product, and the reason it is held back
            // by default: a tree with 150 types and three codes each is 450 lines nobody asked for
            // on every unrelated run.
            if (GateVerbosity.Verbose)
                foreach (var (scope, code) in ratchet.New.Concat(ratchet.Known)
                             .OrderBy(p => p.Scope, StringComparer.OrdinalIgnoreCase)
                             .ThenBy(p => p.Code, StringComparer.Ordinal))
                    output.WriteLine($"{Prefix} baseline {scope} {code}");
            WriteSites(output, inventory, ratchet.Class, only: []);
            return;
        }

        output.WriteLine(
            $"{Prefix} {ratchet.Name} ratchet — ENFORCED against {owned} baseline entr(ies): "
            + $"{ratchet.New.Length} NEW, {ratchet.Stale.Length} stale, "
            + $"{ratchet.Known.Length} known debt, {ratchet.Unverifiable.Length} unverifiable");

        foreach (var (scope, code) in ratchet.New)
            output.WriteLine(
                $"{Prefix} {ratchet.Name} NEW {scope} {code} — not in the baseline. Fix it, or "
                + $"record the debt by adding the line '{scope} {code}'.");
        foreach (var entry in ratchet.Stale)
            output.WriteLine(
                $"{Prefix} {ratchet.Name} STALE baseline entry (its type compiled clean of {entry.Code} "
                + $"this run — remove the line): {entry}");
        foreach (var entry in ratchet.Unverifiable)
            output.WriteLine(
                $"{Prefix} {ratchet.Name} unverifiable baseline entry ({entry.Scope} did not compile "
                + $"in this run, so its silence proves nothing — kept): {entry}");

        // The actionable sites: the ones behind a NEW pair. Everything else is a number above,
        // recoverable in full with MW_LOG_LEVEL=Information.
        WriteSites(output, inventory, ratchet.Class,
            only: [.. ratchet.New.Select(p => p.Scope).Distinct(StringComparer.OrdinalIgnoreCase)]);
    }

    /// <summary>
    /// Names each distinct site ONCE — never once per NodeType that shares the source, and never
    /// once per occurrence. <paramref name="only"/> restricts to the types a verdict actually
    /// failed on, which is the default; <c>MW_LOG_LEVEL=Information</c> lifts the restriction and
    /// lists every site of the class.
    /// </summary>
    private static void WriteSites(
        TextWriter output,
        WarningInventory inventory,
        WarningClass warningClass,
        ImmutableArray<string> only)
    {
        var sites = inventory.For(warningClass)
            .Where(site => GateVerbosity.Verbose
                || site.Types.Any(t => only.Contains(t, StringComparer.OrdinalIgnoreCase)));
        foreach (var site in sites)
            output.WriteLine($"{Prefix} {site.Describe()}");
    }
}
