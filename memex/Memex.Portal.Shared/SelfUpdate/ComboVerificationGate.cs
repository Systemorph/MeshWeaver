using System.Reactive;
using System.Reactive.Linq;
using MeshWeaver.Mesh.Threading;
using MeshWeaver.Messaging;
using MeshWeaver.PluginCatalog;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Memex.Portal.Shared.SelfUpdate;

/// <summary>
/// 🚨 <b>The combo gate (#2274): may THIS instance be rolled to that image, given the modules it
/// actually runs?</b>
///
/// <para>The release-availability gate (#1754) asks whether an ARTIFACT exists for the target. This
/// asks the harder question the artifact cannot answer: whether the candidate's assemblies can
/// still serve the module content this instance has landed. A framework-identity change invalidates
/// the whole assembly cache by design, and an optional parameter added to a record's primary
/// constructor REPLACES the signature — so "newer" and "baked" can both be true while the roll
/// trades a working portal for a <c>MissingMethodException</c> at boot. That is not hypothetical:
/// memex.systemorph.com sat trapped between two failing states for exactly this reason, and the
/// verdict that would have said so was produced by nothing and read by nothing.</para>
///
/// <para><b>Produce where it is possible, CONSULT everywhere.</b> Producing a verdict needs docker,
/// a materialisation root and repo credentials a portal pod does not have (see
/// <see cref="IComboGateRunner"/>), so the producer is off-cluster by design. This gate therefore
/// runs <see cref="InstanceComboVerifier"/> when a runner IS registered — recording the verdict
/// through <see cref="UpdatePolicyNodeType.RecordVerification"/>, where the Updates settings tab
/// already reads it — and otherwise consults the verdict an operator or CD landed on
/// <c>Admin/UpdatePolicy</c>.</para>
///
/// <para>🚨 <b>Only a Green clears.</b> Every other path — no runner, no verdict, a producer that
/// faulted, a <see cref="ComboVerdictKind.NotVerifiable"/> verdict — resolves to a
/// <see cref="ComboClearance"/> that grants nothing. There is deliberately no configuration key, no
/// missing-service branch and no <c>catch</c> that can manufacture a clearance; the fold lives in
/// <see cref="ComboClearance.For"/> and is pinned by unit tests for all four states.</para>
///
/// <para>Registered unconditionally by <see cref="SelfUpdateConfiguration.AddSelfUpdate"/>, for the
/// same reason <see cref="ReleaseAvailabilityService"/> is: the same verdict has to be readable by
/// every path that rolls a version, and a gate wired for only one of them is not a gate.</para>
///
/// <para>Mesh-scoped singleton (its lifetime is the mesh's — <c>Doc/Architecture/NoStaticState</c>),
/// reactive end to end, no <c>async</c>/<c>await</c>. Not sealed and its members are virtual: they
/// are the documented injection seams, exactly as
/// <see cref="ReleaseAvailabilityService.IsUpdatable"/> is.</para>
///
/// <para>See <c>Doc/Architecture/ComboGateWiring</c>.</para>
/// </summary>
/// <param name="hub">The hub whose instance this gates.</param>
/// <param name="logger">Diagnostics.</param>
public class ComboVerificationGate(
    IMessageHub hub,
    ILogger<ComboVerificationGate>? logger = null)
{
    /// <summary>
    /// How long ONE production run may take before it is abandoned and the recorded verdict is
    /// consulted instead. Generous by design — assembling a combo means one shallow pack per module
    /// repository and the gate run compiles, renders and TESTS every NodeType inside the candidate
    /// image — so this bounds a WEDGE, not a slow answer.
    ///
    /// <para>Abandoning is safe precisely because it is not a decision: the fallback is the recorded
    /// verdict, so a producer that hangs cannot turn a recorded Red into a pass.</para>
    /// </summary>
    protected virtual TimeSpan ProduceBudget => TimeSpan.FromMinutes(30);

    /// <summary>
    /// The producer seam, resolved from the mesh's services — null on every host that cannot run a
    /// gate, which is every portal pod. Virtual so a test can present one without registering
    /// docker.
    /// </summary>
    protected virtual IComboGateRunner? ResolveRunner() =>
        hub.ServiceProvider.GetService<IComboGateRunner>();

    /// <summary>
    /// 🚨 <b>The pure consult: the clearance that follows from what is already RECORDED.</b> Used
    /// to walk the candidate list, where asking the producer once per candidate would cost a full
    /// docker run per tag. Cheap, synchronous, and total — it always answers.
    /// </summary>
    /// <param name="policy">The policy content the poller has already read; the verdicts live on
    /// it, so this costs no additional mesh touch (the wedges-to-zero invariant is untouched).</param>
    /// <param name="candidateTag">The candidate.</param>
    public virtual ComboClearance Recorded(UpdatePolicyContent policy, string candidateTag) =>
        ComboClearance.For(candidateTag, policy.VerificationFor(candidateTag), AbsenceReason());

    /// <summary>
    /// The clearance for the candidate this instance is about to roll to: PRODUCE a verdict when
    /// this host can, else consult the recorded one. Cold — subscribe to run — and total: it
    /// answers on every path, so a caller never needs a <c>Catch</c> that would turn an incident
    /// into a pass.
    /// </summary>
    /// <param name="policy">The policy content already read by the caller.</param>
    /// <param name="candidateTag">The candidate tag.</param>
    /// <param name="imageRef">The full image reference the gate would run
    /// (<c>registry/repo:tag</c>).</param>
    public virtual IObservable<ComboClearance> Clearance(
        UpdatePolicyContent policy, string candidateTag, string imageRef) =>
        Produce(candidateTag, imageRef)
            .Select(produced => ComboClearance.For(
                candidateTag,
                // 🚨 The freshly produced verdict wins, but its ABSENCE falls back to the record —
                // never to a clearance. A producer that could not run leaves a recorded Red
                // refusing, which is the whole point of consulting the record at all.
                produced ?? policy.VerificationFor(candidateTag),
                AbsenceReason()));

    /// <summary>
    /// Runs <see cref="InstanceComboVerifier"/> for this instance's combo against the candidate and
    /// RECORDS the verdict on <c>Admin/UpdatePolicy</c>. Emits the verdict, or <c>null</c> when this
    /// host produces none (no runner registered) or the production could not complete.
    ///
    /// <para>🚨 The record is BOOKKEEPING, never a gate (#1020, the shape
    /// <c>SelfUpdateHostedService.RecordAvailable</c> already uses): a failed write is surfaced as a
    /// warning naming the tag and the node, and the verdict is still returned — the DECISION comes
    /// from the verdict itself, never from whether the mesh accepted the write. <c>Concat</c>, not
    /// <c>Merge</c>, so the record still lands FIRST when it succeeds.</para>
    ///
    /// <para>Virtual: the documented seam for a test that must pin what the poller DOES with a
    /// produced verdict without staging docker and a module repository.</para>
    /// </summary>
    protected virtual IObservable<ComboVerification?> Produce(string candidateTag, string imageRef)
    {
        var runner = ResolveRunner();
        if (runner is null)
            // Not a skip: nothing has been decided here. The caller folds the ABSENCE of a verdict
            // into NotVerified, which grants no clearance.
            return Observable.Return<ComboVerification?>(null);

        var combo = ReadCombo();
        if (combo is null)
        {
            logger?.LogWarning(
                "[ComboGate] a gate runner is registered but {Reader} is not — this host cannot "
                + "state its own combo, so no verdict can be produced for {Tag}. The recorded "
                + "verdict (if any) is consulted instead.",
                nameof(InstanceComboReader), candidateTag);
            return Observable.Return<ComboVerification?>(null);
        }

        var filePool = hub.ServiceProvider.GetService<IoPoolRegistry>()?.Get(IoPoolNames.FileSystem)
                       ?? IoPool.Unbounded;
        var assembler = new InstanceComboAssembler(
            runner.Fetch, filePool, runner.Options, logger);
        var verifier = new InstanceComboVerifier(assembler, runner.Run, logger);

        return Observable.Defer(() =>
            {
                logger?.LogInformation(
                    "[ComboGate] verifying candidate {Image} against this instance's combo.",
                    imageRef);
                return combo;
            })
            .SelectMany(combo => verifier.Verify(combo, imageRef, runner.WorkRoot, candidateTag))
            .SelectMany(run => Record(run.Verdict)
                .IgnoreElements()
                .Select(_ => (ComboVerification?)null)
                .Concat(Observable.Return<ComboVerification?>(run.Verdict)))
            .Take(1)
            .Timeout(ProduceBudget)
            .Catch((Exception exception) =>
            {
                // 🚨 Surfaced, never swallowed — and it does not decide anything: the caller falls
                // back to the RECORDED verdict, so a producer that faults cannot clear a candidate
                // and cannot un-refuse one.
                logger?.LogWarning(exception,
                    "[ComboGate] could not produce a combo verdict for {Tag}; the verdict recorded "
                    + "on {Node} (if any) decides instead.",
                    candidateTag, UpdatePolicyNodeType.NodePath);
                return Observable.Return<ComboVerification?>(null);
            });
    }

    /// <summary>
    /// What this instance runs, as <see cref="InstanceComboReader"/> states it — the INPUT the
    /// verifier materialises and gates. Null when the reader is not registered on this host, in
    /// which case nothing can be produced (and nothing is thereby cleared).
    ///
    /// <para>Virtual: the seam that lets a test present a combo without provisioning module
    /// partitions, so the assembler, the verifier and the verdict record all still run FOR REAL —
    /// which is the only way a test can pin that they are called at all.</para>
    /// </summary>
    protected virtual IObservable<InstanceCombo>? ReadCombo() =>
        hub.ServiceProvider.GetService<InstanceComboReader>()?.Read();

    /// <summary>The verdict write, isolated so a failed record can never abort the run that
    /// produced it.</summary>
    private IObservable<Unit> Record(ComboVerification verdict) =>
        UpdatePolicyNodeType.RecordVerification(hub, verdict)
            .Catch((Exception exception) =>
            {
                logger?.LogWarning(exception,
                    "[ComboGate] could not record the {Verdict} verdict for {Tag} on {Node}; the "
                    + "verdict itself still decides this check.",
                    verdict.Verdict, verdict.CandidateTag, UpdatePolicyNodeType.NodePath);
                return Observable.Return(Unit.Default);
            });

    /// <summary>Why this host has no verdict of its own to offer — carried into the
    /// <see cref="ComboClearanceKind.NotVerified"/> reason so "nobody ran the gate" reads
    /// differently from "the gate ran and could not answer".</summary>
    private string AbsenceReason() =>
        ResolveRunner() is null
            ? "no combo-gate runner is registered on this host, so it produces no verdicts of its "
              + "own — they are landed here by mw-combo-verify"
            : "the gate runner on this host produced none";

    /// <summary>
    /// 🚨 The clearance for a host on which the gate itself is NOT registered — the answer the
    /// poller uses when it can resolve no gate at all.
    ///
    /// <para>It is <see cref="ComboClearanceKind.NotVerified"/>, never a clearance: an unregistered
    /// gate has not answered the question, it has failed to ask it. It does not REFUSE either,
    /// because refusing on the absence of a producer nothing has wired yet would freeze every
    /// instance in the fleet — the fail-closed rule drawn one state too wide that
    /// <c>ReleaseGateApplicabilityTest</c> already records the cost of.</para>
    /// </summary>
    public static ComboClearance NotRegistered(string candidateTag) =>
        ComboClearance.For(
            candidateTag, null,
            $"no {nameof(ComboVerificationGate)} is registered on this host (it is wired by "
            + $"{nameof(SelfUpdateConfiguration.AddSelfUpdate)})");
}
