using System;
using System.Collections.Generic;
using System.Reactive;
using System.Reactive.Linq;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Xunit;

namespace MeshWeaver.Hosting.Test;

/// <summary>
/// 🚨 <b>"not ready should NEVER run" — stated as an invariant a test can falsify.</b> Issue #3478,
/// maintainer directive 2026-09-06.
///
/// <para>The defect the directive names is a DIVERGENCE: the bake gate refused readiness and the
/// process kept participating. Two verdicts, one of them enforced. So the fix is not a second gate
/// with its own table — it is deriving membership FROM the readiness predicate, and these cases pin
/// that derivation over the whole state space rather than at the two points the incident happened
/// to visit.</para>
///
/// <para>The one deliberate asymmetry is <see cref="MeshAdmission.Provisional"/>: an armed gate that
/// has not measured yet grants readiness (<see cref="BakePhase.NotStarted"/> means "the sweep is
/// switched off" as far as a probe can tell) but must not let this process publish, because no
/// verdict exists. The incident's pod walked exactly through that window — its bake started at
/// <c>ApplicationStarted</c>, minutes after the process did.</para>
/// </summary>
public class MeshAdmissionIsNeverMorePermissiveThanReadinessTest
{
    private static readonly IReadOnlyList<BakePhase> AllPhases =
        [BakePhase.NotStarted, BakePhase.Running, BakePhase.Complete,
         BakePhase.Regressed, BakePhase.Faulted];

    /// <summary>
    /// 🚨 THE INVARIANT. Over every phase and every flag combination: a state that is ADMITTED to
    /// the mesh is a state a readiness probe would pass. An implementation that admits anywhere
    /// readiness refuses reproduces #3478 by construction, whatever the rest of it does.
    /// </summary>
    [Fact]
    public void AdmittedImpliesReady_ForEveryPhaseAndFlagCombination()
    {
        foreach (var phase in AllPhases)
            foreach (var gates in new[] { false, true })
                foreach (var allowUnproven in new[] { false, true })
                {
                    var state = AtPhase(phase, gates, allowUnproven);
                    if (state.Admission is MeshAdmission.Admitted)
                        state.ReadinessGranted.Should().BeTrue(
                            $"phase {phase} (gates={gates}, allowUnproven={allowUnproven}) admits "
                            + "this process to the mesh, so a readiness probe must pass it — "
                            + "'not ready should NEVER run'");
                }
    }

    /// <summary>
    /// An ARMED gate never admits before it has measured. This is the window the refused pod used:
    /// readiness is granted at <see cref="BakePhase.NotStarted"/>, and publishing there would let a
    /// bad image stamp before its own sweep had a chance to refuse it.
    /// </summary>
    [Fact]
    public void AnArmedGateThatHasNotMeasuredYet_IsProvisional_NotAdmitted()
    {
        var state = AtPhase(BakePhase.NotStarted, gatesReadiness: true, allowUnprovenBake: false);

        state.ReadinessGranted.Should().BeTrue("NotStarted is the documented fail-OPEN readiness state");
        state.Admission.Should().Be(MeshAdmission.Provisional,
            "an armed gate with no verdict yet must HOLD publications, never run them — and never "
            + "drop them either, because the verdict is still coming");
    }

    /// <summary>
    /// An UNARMED gate enforces nothing — the same "registered ≠ armed" rule that keeps the log from
    /// claiming a stall nobody implements. Every dev host, every test mesh and every deployment on
    /// the chart default is in this state, and none of them may be black-holed by enforcement
    /// nobody opted into.
    /// </summary>
    [Fact]
    public void AnUnarmedGate_IsUnarmed_EvenWhenItRecordedARegression()
    {
        var state = AtPhase(BakePhase.Regressed, gatesReadiness: false, allowUnprovenBake: false);

        state.Admission.Should().Be(MeshAdmission.Unarmed,
            "nothing consumes this state, so nothing is enforced from it");
    }

    /// <summary>
    /// The operator override moves BOTH verdicts together. <c>AllowUnprovenBake</c> says "serve on a
    /// bake that could not be PROVEN"; a process that may serve may publish, and one that may not,
    /// may not. A real regression outranks it in both.
    /// </summary>
    [Theory]
    [InlineData(BakePhase.Faulted, true, MeshAdmission.Admitted)]
    [InlineData(BakePhase.Faulted, false, MeshAdmission.Refused)]
    [InlineData(BakePhase.Regressed, true, MeshAdmission.Refused)]
    [InlineData(BakePhase.Regressed, false, MeshAdmission.Refused)]
    public void AllowUnprovenBake_MovesReadinessAndAdmissionTogether(
        BakePhase phase, bool allowUnproven, MeshAdmission expected)
    {
        var state = AtPhase(phase, gatesReadiness: true, allowUnprovenBake: allowUnproven);

        state.Admission.Should().Be(expected);
        (state.Admission is MeshAdmission.Admitted).Should().Be(state.ReadinessGranted,
            "for a terminal phase the two verdicts are the same verdict");
    }

    // ── the gate itself ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 🚨 A REFUSED PUBLICATION IS NEVER CONSTRUCTED. Not "constructed and discarded": the factory
    /// is not invoked at all, so no storage read is issued and no <c>RequireSubscribeObservable</c>
    /// is minted only to be dropped (which would log a never-subscribed warning about a write we
    /// deliberately did not make).
    /// </summary>
    [Fact]
    public void ARefusedPublication_IsNeverEvenBuilt()
    {
        var authority = new FixedAuthority(MeshAdmission.Refused);
        using var gate = new MeshPublicationGate([authority]);
        var built = 0;

        gate.Publish("a stamp", () => { built++; return Observable.Return(Unit.Default); })
            .Subscribe();

        built.Should().Be(0, "a refused process must not touch the mesh, not even to read it");
        gate.RefusedCount.Should().Be(1, "the refusal is counted, never silent");
    }

    /// <summary>
    /// A HELD publication runs exactly once, at release — after the verdict, not before it. This is
    /// "validate before running" rather than "never run": a passing bake still records everything it
    /// compiled.
    /// </summary>
    [Fact]
    public void AHeldPublication_RunsOnceOnAdmission()
    {
        var authority = new FixedAuthority(MeshAdmission.Provisional);
        using var gate = new MeshPublicationGate([authority]);
        var ran = 0;

        gate.Publish("a stamp", () => { ran++; return Observable.Return(Unit.Default); })
            .Subscribe();
        ran.Should().Be(0, "the verdict does not exist yet");

        authority.Admission = MeshAdmission.Admitted;
        gate.Reconsider();

        ran.Should().Be(1, "the held write runs when the process is admitted");
        gate.ReleasedCount.Should().Be(1);
        gate.HeldCount.Should().Be(0, "the queue is drained, not replayed");
    }

    /// <summary>
    /// A held publication whose verdict turns out to be a REFUSAL is discarded, never written — the
    /// exact shape of the incident, where the stamps that did the damage were produced before the
    /// regression was discovered.
    /// </summary>
    [Fact]
    public void AHeldPublication_IsDiscardedWhenTheVerdictRefuses()
    {
        var authority = new FixedAuthority(MeshAdmission.Provisional);
        using var gate = new MeshPublicationGate([authority]);
        var ran = 0;

        gate.Publish("a stamp", () => { ran++; return Observable.Return(Unit.Default); })
            .Subscribe();
        authority.Admission = MeshAdmission.Refused;
        gate.Reconsider();

        ran.Should().Be(0, "a stamp produced before the verdict must not be written after it");
        gate.DiscardedCount.Should().Be(1);
        gate.WithheldCount.Should().Be(1, "withheld = refused outright + discarded after holding");
    }

    /// <summary>
    /// With no authority registered the gate is a straight pass-through — byte-for-byte the
    /// behaviour every host had before it existed.
    /// </summary>
    [Fact]
    public void AnUnarmedGate_PassesEverythingThrough()
    {
        using var gate = new MeshPublicationGate();
        var ran = 0;

        gate.Publish("a stamp", () => { ran++; return Observable.Return(Unit.Default); })
            .Subscribe();

        ran.Should().Be(1, "an unarmed gate enforces nothing");
        gate.PassedCount.Should().Be(1);
    }

    /// <summary>
    /// The MOST RESTRICTIVE authority wins. One authority stating evidence against this image
    /// outranks any number stating the absence of it — which is what lets a second validator be
    /// added later without weakening the first.
    /// </summary>
    [Fact]
    public void TheMostRestrictiveAuthorityWins()
    {
        var permissive = new FixedAuthority(MeshAdmission.Admitted);
        var refusing = new FixedAuthority(MeshAdmission.Refused);
        using var gate = new MeshPublicationGate([permissive, refusing]);

        gate.Admission.Should().Be(MeshAdmission.Refused);
    }

    /// <summary>
    /// Teardown drops what is held: a process on its way out must not write its identity into the
    /// mesh as it goes.
    /// </summary>
    [Fact]
    public void DisposeDropsHeldPublications()
    {
        var authority = new FixedAuthority(MeshAdmission.Provisional);
        var gate = new MeshPublicationGate([authority]);
        var ran = 0;

        gate.Publish("a stamp", () => { ran++; return Observable.Return(Unit.Default); })
            .Subscribe();
        gate.Dispose();
        authority.Admission = MeshAdmission.Admitted;
        gate.Reconsider();

        ran.Should().Be(0, "a disposed gate writes nothing, whatever the verdict becomes");
    }

    /// <summary>
    /// Drives a real <see cref="NodeTypeBakeGateState"/> into <paramref name="phase"/> the way the
    /// sweep does — never by setting a field, so the derivation under test is the production one.
    /// </summary>
    private static NodeTypeBakeGateState AtPhase(
        BakePhase phase, bool gatesReadiness, bool allowUnprovenBake)
    {
        var state = new NodeTypeBakeGateState
        {
            GatesReadiness = gatesReadiness,
            AllowUnprovenBake = allowUnprovenBake,
        };
        switch (phase)
        {
            case BakePhase.NotStarted:
                break;
            case BakePhase.Running:
                state.MarkRunning("enumerating dynamic NodeTypes");
                break;
            case BakePhase.Complete:
                state.MarkRunning("enumerating dynamic NodeTypes");
                state.MarkComplete("baked in 00:01:00 — compiled=3 alreadyBaked=0");
                break;
            case BakePhase.Regressed:
                state.MarkRunning("enumerating dynamic NodeTypes");
                state.MarkOutcome(new PreWarmOutcome(
                    "Feedback/Feedback", PreWarmStatus.CompileError, "CS0117"));
                break;
            default:
                state.MarkRunning("enumerating dynamic NodeTypes");
                state.MarkFaulted("the warm-up stream faulted before the sweep could finish");
                break;
        }
        state.Phase.Should().Be(phase, "the case must actually reach the phase it claims to test");
        return state;
    }

    /// <summary>An authority whose verdict the test moves — a stand-in for a validator, never for
    /// <see cref="NodeTypeBakeGateState"/>, whose own derivation is exercised above.</summary>
    private sealed class FixedAuthority(MeshAdmission admission) : IMeshAdmissionAuthority
    {
        public MeshAdmission Admission { get; set; } = admission;

        public string AdmissionReason => $"test authority says {Admission}";
    }
}
