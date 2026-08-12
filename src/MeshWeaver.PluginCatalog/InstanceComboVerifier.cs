using System.Collections.Immutable;
using System.Reactive.Linq;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.PluginCatalog;

/// <summary>
/// Step 3 of the Candidate Release Protocol's instance gate: verify a CANDIDATE image against the
/// combo THIS instance runs, and fold the evidence into one <see cref="ComboVerification"/> verdict.
///
/// <para>Composes the two prior steps and the existing gate: the combo (as
/// <see cref="InstanceComboReader"/> states it) is materialised by
/// <see cref="InstanceComboAssembler"/> into the repo-root layout <c>mw-plugin-test</c> takes, the
/// tester is executed INSIDE the candidate image over that root (the docker orchestration is a
/// seam — a delegate — so the folding is testable without docker), and the outcome lands as a
/// verdict that names its exact input: every module with its resolved ref and content hash.</para>
///
/// <para><b>Failure semantics — the three verdicts.</b> <see cref="ComboVerdictKind.Green"/> means
/// every module materialised at its recorded ref AND compiled, rendered, and tested green against
/// the candidate. <see cref="ComboVerdictKind.Red"/> means the gate ran and at least one module
/// failed — breadth-complete, every failing module named. <see cref="ComboVerdictKind.NotVerifiable"/>
/// means the question could not be answered: a module could not be materialised (a moving ref, a
/// fetch failure, an incomplete combo), the gate could not run, or it produced no structured
/// evidence. 🚨 The three are never conflated — "we could not find out" must not read as either
/// "broken" or "all clear", which is the false-confidence failure this protocol exists to
/// prevent.</para>
///
/// <para>Reactive end to end; cold — <c>Verify</c> runs on Subscribe. See
/// <c>Doc/Architecture/CandidateReleaseProtocol</c>.</para>
/// </summary>
public sealed class InstanceComboVerifier
{
    private readonly InstanceComboAssembler assembler;
    private readonly Func<string, string, IObservable<CandidateGateRun>> runGate;
    private readonly ILogger? logger;

    /// <summary>
    /// Creates a verifier over an assembler and a gate seam.
    /// </summary>
    /// <param name="assembler">Materialises the combo into the work root (its fetch seam carries
    /// the repo access).</param>
    /// <param name="runGate">Executes <c>mw-plugin-test &lt;root&gt; --report …</c> INSIDE the
    /// candidate image: (imageRef, workRoot) → one <see cref="CandidateGateRun"/>. Production
    /// wires the docker orchestration in <c>mw-combo-verify</c>; tests hand in a fake. Expected
    /// failures (pull denied, timeout) are reported via <see cref="CandidateGateRun.Error"/>; an
    /// OnError is folded the same way rather than killing the run.</param>
    /// <param name="logger">Diagnostics.</param>
    public InstanceComboVerifier(
        InstanceComboAssembler assembler,
        Func<string, string, IObservable<CandidateGateRun>> runGate,
        ILogger? logger = null)
    {
        this.assembler = assembler;
        this.runGate = runGate;
        this.logger = logger;
    }

    /// <summary>
    /// Verifies <paramref name="imageRef"/> against <paramref name="combo"/>. Cold — subscribe to
    /// run. Emits one <see cref="ComboVerificationRun"/> (assembly report + gate run + verdict),
    /// then completes; per-module problems are entries, never faults, so the caller always learns
    /// everything that went wrong.
    ///
    /// <para>The gate is executed ONLY when every module materialised with a discoverable root —
    /// a gate over a partial set would read as though it verified the instance, the same refusal
    /// the assembler itself makes for incomplete combos.</para>
    /// </summary>
    /// <param name="combo">The instance's combo, as the reader stated it.</param>
    /// <param name="imageRef">The candidate image reference (<c>registry/repo:tag</c>).</param>
    /// <param name="workRoot">The directory the combo is materialised into and the gate runs
    /// over.</param>
    /// <param name="candidateTag">The candidate TAG the verdict is keyed by; derived from
    /// <paramref name="imageRef"/> when omitted.</param>
    public IObservable<ComboVerificationRun> Verify(
        InstanceCombo combo, string imageRef, string workRoot, string? candidateTag = null)
    {
        var tag = string.IsNullOrWhiteSpace(candidateTag) ? TagOf(imageRef) : candidateTag!;
        return assembler.Assemble(combo, workRoot)
            .SelectMany(assembly =>
            {
                var undiscoverable = assembly.Success
                    ? assembly.Modules.Where(m => !m.DeclaresRoot).Select(m => m.ModuleId).ToImmutableList()
                    : ImmutableList<string>.Empty;
                if (!assembly.Success || undiscoverable.Count > 0)
                {
                    logger?.LogWarning(
                        "[ComboVerify] not running the gate for {Tag}: assembly {State}, "
                        + "{Undiscoverable} module(s) without a discoverable root.",
                        tag, assembly.Success ? "green" : "not green", undiscoverable.Count);
                    return Observable.Return(new ComboVerificationRun
                    {
                        Assembly = assembly,
                        Verdict = Fold(tag, imageRef, assembly, gate: null, undiscoverable),
                    });
                }
                return runGate(imageRef, workRoot)
                    // An unexpected OnError from the orchestration is evidence of nothing about the
                    // candidate — fold it as an orchestration error (NotVerifiable), never a crash.
                    .Catch((Exception ex) => Observable.Return(new CandidateGateRun
                    {
                        Error = $"the gate orchestration faulted: {ex.Message}",
                    }))
                    .Select(gate => new ComboVerificationRun
                    {
                        Assembly = assembly,
                        Gate = gate,
                        Verdict = Fold(tag, imageRef, assembly, gate, undiscoverable),
                    });
            });
    }

    /// <summary>The tag of an image reference: what follows the last <c>:</c> after the last
    /// <c>/</c>, with any <c>@digest</c> stripped; <c>latest</c> when the ref names none.</summary>
    public static string TagOf(string imageRef)
    {
        var at = imageRef.IndexOf('@');
        var withoutDigest = at >= 0 ? imageRef[..at] : imageRef;
        var colon = withoutDigest.LastIndexOf(':');
        return colon > withoutDigest.LastIndexOf('/') ? withoutDigest[(colon + 1)..] : "latest";
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  Folding — pure, and where every failure-semantics decision lives
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Folds the assembly report and the gate run into the verdict. Pure and deterministic (up to
    /// <see cref="ComboVerification.VerifiedAt"/>), so the failure semantics are pinned by unit
    /// tests with fakes.
    /// </summary>
    /// <param name="candidateTag">The tag the verdict is keyed by.</param>
    /// <param name="imageRef">The image reference the gate ran (or would have run).</param>
    /// <param name="assembly">The combo assembly's manifest.</param>
    /// <param name="gate">The gate run; null when it was never executed.</param>
    /// <param name="undiscoverable">Materialised modules without an <c>index.json</c> root — the
    /// gate cannot discover them, so it was not run.</param>
    internal static ComboVerification Fold(
        string candidateTag,
        string imageRef,
        ComboAssemblyReport assembly,
        CandidateGateRun? gate,
        ImmutableList<string> undiscoverable)
    {
        var caveats = ImmutableList.CreateBuilder<string>();
        caveats.AddRange(assembly.ComboCaveats);
        foreach (var moving in assembly.Modules.Where(m => m.Pin == MaterializationPin.Moving))
            caveats.Add(
                $"'{moving.ModuleId}' was materialised from a MOVING ref — this verdict is about "
                + "the content the run happened to fetch, and a later run can resolve differently.");
        foreach (var diverged in assembly.Modules.Where(m => m.ModuleVersionMatches == false))
            caveats.Add(
                $"'{diverged.ModuleId}' has diverged from its install record (the space synced past "
                + $"the install: recorded {diverged.RecordedModuleVersion}, fetched "
                + $"{diverged.FetchedModuleVersion}).");

        var modules = gate?.Report is { FatalError: null } report && gate.Error is null
            ? FoldModulesFromGate(assembly, report, caveats)
            : FoldModulesWithoutGateEvidence(assembly, undiscoverable);

        var verdict = DecideVerdict(assembly, gate, modules, caveats);

        return new ComboVerification
        {
            CandidateTag = candidateTag,
            ImageRef = imageRef,
            ImageDigest = gate?.ImageDigest,
            VerifiedAt = DateTimeOffset.UtcNow,
            ComboReadAt = assembly.ComboReadAt,
            Verdict = verdict,
            Modules = modules,
            Caveats = caveats.ToImmutable(),
        };
    }

    /// <summary>Every module folded against the gate's structured report — the only path that can
    /// yield <see cref="ModuleVerificationOutcome.Passed"/> or
    /// <see cref="ModuleVerificationOutcome.Failed"/>.</summary>
    private static ImmutableList<ModuleVerification> FoldModulesFromGate(
        ComboAssemblyReport assembly, GateRunReport report, ImmutableList<string>.Builder caveats)
    {
        var packages = report.Packages.ToImmutableDictionary(
            p => p.Id, p => p, StringComparer.OrdinalIgnoreCase);

        var modules = assembly.Modules
            .Select(module =>
            {
                var entry = BaseEntry(module);
                if (!packages.TryGetValue(module.ModuleId, out var package))
                    // The gate ran but never saw this module — that is "not verified", never
                    // "passed by omission".
                    return entry with
                    {
                        Outcome = ModuleVerificationOutcome.NotVerified,
                        Failures =
                        [
                            "the gate did not discover this module (its folder produced no "
                            + "package) — nothing about it was verified.",
                        ],
                    };
                var failures = FailuresOf(package);
                return entry with
                {
                    Outcome = failures.Count == 0
                        ? ModuleVerificationOutcome.Passed
                        : ModuleVerificationOutcome.Failed,
                    Failures = failures,
                };
            })
            .ToImmutableList();

        // A package the report carries that the assembly never materialised would mean the gate
        // verified content this combo does not name — call it out rather than silently widening.
        var known = assembly.Modules.Select(m => m.ModuleId)
            .ToImmutableHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var extra in report.Packages.Where(p => !known.Contains(p.Id)))
            caveats.Add(
                $"the gate reported package '{extra.Id}', which is not part of this combo's "
                + "assembly manifest — its result is ignored, but the work root was not clean.");

        return modules;
    }

    /// <summary>Every module as NOT VERIFIED — the gate ran without usable evidence, or never
    /// ran. Assembly-level errors ride on their module's entry.</summary>
    private static ImmutableList<ModuleVerification> FoldModulesWithoutGateEvidence(
        ComboAssemblyReport assembly, ImmutableList<string> undiscoverable) =>
        assembly.Modules
            .Select(module => BaseEntry(module) with
            {
                Outcome = ModuleVerificationOutcome.NotVerified,
                Failures = module.Status != ModuleAssemblyStatus.Materialized
                    ? [$"{module.Status}: {module.Error}"]
                    : undiscoverable.Contains(module.ModuleId, StringComparer.OrdinalIgnoreCase)
                        ? [
                            "materialised without an index.json root — mw-plugin-test cannot "
                            + "discover it, so the combo cannot be verified as a whole.",
                          ]
                        : [],
            })
            .ToImmutableList();

    private static ModuleVerification BaseEntry(ModuleAssembly module) => new()
    {
        ModuleId = module.ModuleId,
        ResolvedCommit = module.ResolvedCommit,
        ContentHash = module.ContentHash,
        Pin = module.Pin,
    };

    /// <summary>
    /// The verdict, and the caveats that explain a <see cref="ComboVerdictKind.NotVerifiable"/>
    /// one. Priority: a named module failure is a fact about the candidate and wins (Red) even
    /// when other modules could not be verified; otherwise any gap in the evidence makes the
    /// whole verdict NotVerifiable; only a complete, evidenced pass is Green.
    /// </summary>
    private static ComboVerdictKind DecideVerdict(
        ComboAssemblyReport assembly,
        CandidateGateRun? gate,
        ImmutableList<ModuleVerification> modules,
        ImmutableList<string>.Builder caveats)
    {
        if (gate is null)
        {
            caveats.Add(assembly.FatalError is not null
                ? $"the combo could not be assembled: {assembly.FatalError}"
                : "the gate was not run: the combo did not assemble into a fully verifiable "
                  + "root (the module entries name each reason).");
            return ComboVerdictKind.NotVerifiable;
        }
        if (gate.Error is not null)
        {
            caveats.Add($"the gate could not run: {gate.Error}"
                        + TailNote(gate.LogTail));
            return ComboVerdictKind.NotVerifiable;
        }
        if (gate.Report is null)
        {
            caveats.Add(
                $"the tester produced no structured report (exit {Describe(gate.ExitCode)}) — "
                + "the candidate's mw-plugin-test may predate --report, or it crashed. Nothing "
                + "was verified; a verdict guessed out of log text would be fiction."
                + TailNote(gate.LogTail));
            return ComboVerdictKind.NotVerifiable;
        }
        if (gate.Report.FatalError is not null)
        {
            caveats.Add(
                $"the gate aborted before per-module verdicts: {gate.Report.FatalError}"
                + TailNote(gate.LogTail));
            return ComboVerdictKind.NotVerifiable;
        }

        if (modules.Any(m => m.Outcome == ModuleVerificationOutcome.Failed))
            return ComboVerdictKind.Red;
        if (modules.Count == 0 || modules.Any(m => m.Outcome == ModuleVerificationOutcome.NotVerified))
        {
            if (modules.Count == 0)
                caveats.Add("the assembly names no modules — there is nothing this verdict is about.");
            return ComboVerdictKind.NotVerifiable;
        }
        if (gate.ExitCode != 0)
        {
            // The report shows all green but the process exited red — the two disagree, and a
            // disagreement is not evidence of a pass.
            caveats.Add(
                $"the tester exited {Describe(gate.ExitCode)} although its report names no "
                + "failure — the run is treated as unverified, not as green.");
            return ComboVerdictKind.NotVerifiable;
        }
        return ComboVerdictKind.Green;
    }

    /// <summary>The breadth-complete failure lines of one package: install, idempotence, and every
    /// failed per-NodeType check with its diagnostics.</summary>
    private static ImmutableList<string> FailuresOf(GateRunPackage package)
    {
        var failures = ImmutableList.CreateBuilder<string>();
        if (package.InstallError is not null)
            failures.Add($"install: {package.InstallError}");
        if (package.IdempotenceError is not null)
            failures.Add($"idempotence: {package.IdempotenceError}");
        foreach (var type in package.NodeTypes)
        {
            if (type.Compile == GateRunOutcome.Failed)
                failures.Add($"{type.Path}: compile failed"
                             + (type.CompilationStatus is null ? "" : $" ({type.CompilationStatus})")
                             + Detail(type.CompileDetail));
            if (type.Render == GateRunOutcome.Failed)
                failures.Add($"{type.Path}: render failed{Detail(type.RenderDetail)}");
            if (type.Tests == GateRunOutcome.Failed)
                failures.Add($"{type.Path}: tests failed{Detail(type.TestsDetail)}");
        }
        return failures.ToImmutable();
    }

    private static string Detail(string? detail) =>
        string.IsNullOrWhiteSpace(detail) ? "" : $" — {detail}";

    private static string TailNote(string logTail) =>
        string.IsNullOrWhiteSpace(logTail) ? "" : $" Log tail:\n{logTail}";

    private static string Describe(int? exitCode) =>
        exitCode?.ToString() ?? "(none)";
}

/// <summary>
/// One verification run's full evidence: the assembly manifest, the gate run (null when it was
/// never executed), and the folded verdict. The verdict is what lands on the instance; the rest is
/// what an operator reads when it is not green.
/// </summary>
public sealed record ComboVerificationRun
{
    /// <summary>The combo assembly's manifest — module → resolved ref → content hash.</summary>
    public required ComboAssemblyReport Assembly { get; init; }

    /// <summary>The gate execution, when it ran.</summary>
    public CandidateGateRun? Gate { get; init; }

    /// <summary>The folded verdict.</summary>
    public required ComboVerification Verdict { get; init; }

    /// <summary>Process exit code for the console boundary: 0 only for
    /// <see cref="ComboVerdictKind.Green"/>.</summary>
    public int ExitCode => Verdict.Verdict == ComboVerdictKind.Green ? 0 : 1;

    /// <summary>Human summary for the console / CI log.</summary>
    public void WriteSummary(TextWriter output)
    {
        output.WriteLine();
        output.WriteLine($"=== mw-combo-verify: {Verdict.CandidateTag} ===");
        if (Verdict.ImageDigest is not null)
            output.WriteLine($"image: {Verdict.ImageRef} @ {Verdict.ImageDigest}");
        foreach (var module in Verdict.Modules)
        {
            var mark = module.Outcome switch
            {
                ModuleVerificationOutcome.Passed => "ok ",
                ModuleVerificationOutcome.Failed => "RED",
                _ => "?? ",
            };
            output.WriteLine($"  {mark} {module.ModuleId} [{module.Outcome}"
                             + (module.ResolvedCommit is null ? "" : $" @ {Shorten(module.ResolvedCommit)}")
                             + "]");
            foreach (var failure in module.Failures)
                output.WriteLine($"        {failure.ReplaceLineEndings("\n").Replace("\n", "\n        ")}");
        }
        foreach (var caveat in Verdict.Caveats)
            output.WriteLine($"  caveat: {caveat}");
        output.WriteLine($"verdict: {Verdict.Verdict} — {Verdict.Modules.Count} module(s), "
                         + $"{Verdict.Modules.Count(m => m.Outcome == ModuleVerificationOutcome.Passed)} passed, "
                         + $"{Verdict.FailedModules.Count} failed, "
                         + $"{Verdict.Modules.Count(m => m.Outcome == ModuleVerificationOutcome.NotVerified)} not verified.");
    }

    private static string Shorten(string value) => value.Length > 12 ? value[..12] : value;
}
