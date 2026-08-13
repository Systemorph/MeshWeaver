using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Hosting.Monolith.Test;

/// <summary>
/// The readiness gate's state machine — "fail before prod, not in prod".
///
/// <para>Two rules carry the whole design, and both are here because getting either wrong turns a
/// safety feature into an outage:</para>
/// <list type="number">
///   <item><b>Only a REGRESSION gates.</b> A type that was already broken before this image failing
///     again is pre-existing damage; gating on it would let one abandoned NodeType freeze every
///     future deploy.</item>
///   <item><b>NotStarted is HEALTHY.</b> Gating on a condition nobody is measuring would black-hole
///     a pod on a config mistake. The gate withholds readiness only for what it actively measures.</item>
/// </list>
/// </summary>
public class NodeTypeBakeGateStateTest
{
    private static PreWarmOutcome Failed(string path, bool wasHealthy) =>
        new(path, PreWarmStatus.CompileError, "boom") { WasHealthyBeforeBake = wasHealthy };

    [Fact]
    public void FreshState_IsNotStarted() =>
        new NodeTypeBakeGateState().Phase.Should().Be(BakePhase.NotStarted);

    /// <summary>
    /// 🚨 Registered is NOT armed, and the state must say which it is.
    ///
    /// <para>The state is registered unconditionally so diagnostics are always collected, while the
    /// readiness check that ENFORCES it is opt-in. For months those were independent, so a recorded
    /// regression logged "REFUSING READINESS — the rollout will stall with the previous image still
    /// serving" on a pod that nothing gated, which then went Ready and took traffic. The claim was
    /// read as proof of protection during a production outage. Defaulting to <c>false</c> is what
    /// makes the honest branch the fallback: a host must opt IN to claiming enforcement.</para>
    /// </summary>
    [Fact]
    public void GatesReadiness_DefaultsToFalse_SoTheLogCannotClaimAnUnarmedStall() =>
        new NodeTypeBakeGateState().GatesReadiness.Should().BeFalse();

    [Fact]
    public void AddNodeTypeBakeGate_DefaultRegistration_IsNotArmed() =>
        new ServiceCollection().AddNodeTypeBakeGate()
            .BuildServiceProvider().GetRequiredService<NodeTypeBakeGateState>()
            .GatesReadiness.Should().BeFalse();

    /// <summary>
    /// A host that registers the readiness check declares it here, and that single declaration is
    /// what the pre-warmer reports from — never a second re-parse of the config key, which could
    /// disagree with the wiring it claims to describe.
    /// </summary>
    [Fact]
    public void AddNodeTypeBakeGate_WhenHostGates_IsArmed() =>
        new ServiceCollection().AddNodeTypeBakeGate(gatesReadiness: true)
            .BuildServiceProvider().GetRequiredService<NodeTypeBakeGateState>()
            .GatesReadiness.Should().BeTrue();

    /// <summary>Arming changes only what may be CLAIMED — never which outcomes gate.</summary>
    [Fact]
    public void Arming_DoesNotChangeWhichOutcomesGate()
    {
        var armed = new NodeTypeBakeGateState { GatesReadiness = true };
        var unarmed = new NodeTypeBakeGateState();
        armed.MarkOutcome(Failed("A", wasHealthy: true));
        unarmed.MarkOutcome(Failed("A", wasHealthy: true));

        armed.Phase.Should().Be(BakePhase.Regressed);
        unarmed.Phase.Should().Be(BakePhase.Regressed);
    }

    [Fact]
    public void MarkRunning_MovesToRunning()
    {
        var state = new NodeTypeBakeGateState();
        state.MarkRunning("enumerating");

        state.Phase.Should().Be(BakePhase.Running);
        state.Detail.Should().Be("enumerating");
    }

    [Fact]
    public void CleanRun_CompletesHealthy()
    {
        var state = new NodeTypeBakeGateState();
        state.MarkRunning("go");
        state.MarkOutcome(new PreWarmOutcome("A", PreWarmStatus.Compiled));
        state.MarkOutcome(new PreWarmOutcome("B", PreWarmStatus.AlreadyBaked));
        state.MarkComplete("done");

        state.Phase.Should().Be(BakePhase.Complete);
        state.Regressions.Should().BeEmpty();
    }

    /// <summary>A type that was working and now is not — the one thing that may stall a rollout.</summary>
    [Fact]
    public void PreviouslyHealthyTypeThatFails_Regresses()
    {
        var state = new NodeTypeBakeGateState();
        state.MarkRunning("go");
        state.MarkOutcome(Failed("Store/Plugin", wasHealthy: true));

        state.Phase.Should().Be(BakePhase.Regressed);
        state.Regressions.Keys.Should().Contain("Store/Plugin");
    }

    /// <summary>
    /// 🚨 The deploy-freeze guard. A NodeType already sitting at Error before this image is broken in
    /// production right now; the new image did not break it. If this gated, every subsequent rollout
    /// would be blocked by it — and nobody would find out until they urgently needed to deploy.
    /// </summary>
    [Fact]
    public void AlreadyBrokenTypeThatFailsAgain_DoesNotGate()
    {
        var state = new NodeTypeBakeGateState();
        state.MarkRunning("go");
        state.MarkOutcome(Failed("Abandoned/Type", wasHealthy: false));
        state.MarkComplete("done");

        state.Phase.Should().Be(BakePhase.Complete);
        state.Regressions.Should().BeEmpty();
    }

    /// <summary>Completion is not absolution: finishing the sweep must not clear a recorded regression.</summary>
    [Fact]
    public void MarkComplete_DoesNotClearARegression()
    {
        var state = new NodeTypeBakeGateState();
        state.MarkRunning("go");
        state.MarkOutcome(Failed("Store/Plugin", wasHealthy: true));
        state.MarkComplete("baked in 00:12:00");

        state.Phase.Should().Be(BakePhase.Regressed);
        state.Detail.Should().Contain("Store/Plugin");
    }

    // =============================================================================================
    // RETRACTION (issue #1214). "Completion is not absolution" is about the SWEEP finishing and
    // stays true above. What DOES absolve one type is that type building on THIS image afterwards
    // — a later, better measurement of the same thing, not an amnesty.
    //
    // The incident: memex-cloud 2026-08-11, a bake compiled `Store/*` against a half-applied plugin
    // update (the reverted `Localizer` had landed, its callers had not), recorded four regressions
    // with CS0117 diagnostics, and refused readiness. The content converged 2 minutes later and the
    // platform recompiled every one of those types green — but the gate never re-read them, so the
    // pod stayed out of rotation and the rollout hung until a human restarted it.
    // =============================================================================================

    /// <summary>
    /// The headline: a regression retracted after the sweep completed lands on the COMPLETE verdict
    /// <see cref="NodeTypeBakeGateState.MarkComplete"/> would have produced. Anything else (staying
    /// Regressed, or falling back to Running) leaves the pod out of rotation forever, which is the
    /// bug — the rollout hung on content that had already repaired itself.
    /// </summary>
    [Fact]
    public void RetractingTheLastRegressionAfterTheSweep_ReturnsTheGateToComplete()
    {
        var state = new NodeTypeBakeGateState { GatesReadiness = true };
        state.MarkRunning("go");
        state.MarkOutcome(Failed("Store/Catalog", wasHealthy: true));
        state.MarkComplete("baked in 00:19:00");
        state.Phase.Should().Be(BakePhase.Regressed);

        state.RetractRegression("Store/Catalog", "rebuilt on this image")
            .Should().BeTrue("the gate was holding a regression for exactly this type");

        state.Phase.Should().Be(BakePhase.Complete,
            "a type that builds on this image is not evidence that this image broke it — the pod "
            + "must be allowed to take traffic without a human noticing");
        state.Regressions.Should().BeEmpty();
        state.Retracted.Keys.Should().Contain("Store/Catalog");
        state.Detail.Should().Contain("baked in 00:19:00").And.Contain("retracted",
            "a regression that healed still HAPPENED — hiding it would make a real, recurring "
            + "content race invisible");
    }

    /// <summary>
    /// A retraction while the sweep is still running goes back to RUNNING, never to Complete: the
    /// sweep has not finished measuring, and reporting Complete early would let a pod go Ready
    /// before the types after this one were even attempted.
    /// </summary>
    [Fact]
    public void RetractingTheLastRegressionMidSweep_ReturnsToRunning_NotComplete()
    {
        var state = new NodeTypeBakeGateState();
        state.MarkRunning("go");
        state.MarkOutcome(Failed("Store/Catalog", wasHealthy: true));

        state.RetractRegression("Store/Catalog", "rebuilt on this image").Should().BeTrue();

        state.Phase.Should().Be(BakePhase.Running,
            "the sweep is still measuring — only MarkComplete may declare the bake done");
    }

    /// <summary>One retraction among several regressions keeps the gate red — it is not a reset.</summary>
    [Fact]
    public void RetractingOneOfSeveralRegressions_StillGates()
    {
        var state = new NodeTypeBakeGateState();
        state.MarkRunning("go");
        state.MarkOutcome(Failed("Store/Catalog", wasHealthy: true));
        state.MarkOutcome(Failed("Store/Order", wasHealthy: true));
        state.MarkComplete("done");

        state.RetractRegression("Store/Catalog", "rebuilt").Should().BeTrue();

        state.Phase.Should().Be(BakePhase.Regressed);
        state.Regressions.Keys.Should().Equal("Store/Order");
        // The still-red payload names BOTH: what is holding the pod out of rotation, and what
        // failed-then-rebuilt. The second half is the signal that says "you are looking at a
        // content race, not a bad image" — and it is needed most while the gate is still red.
        state.Detail.Should().Contain("Store/Order")
            .And.Contain("Store/Catalog").And.Contain("retracted");
    }

    /// <summary>
    /// 🚨 The retraction may NOT be used to launder a type nobody condemned, and it may not
    /// resurrect a cleared one. Retracting an unheld type is a no-op that reports itself as such —
    /// otherwise a caller could quietly move the gate to Complete on a type the sweep never failed.
    /// </summary>
    [Fact]
    public void RetractingATypeThatNeverRegressed_ChangesNothing()
    {
        var state = new NodeTypeBakeGateState();
        state.MarkRunning("go");
        state.MarkOutcome(Failed("Store/Order", wasHealthy: true));
        state.MarkComplete("done");

        state.RetractRegression("Never/Failed", "rebuilt").Should().BeFalse();

        state.Phase.Should().Be(BakePhase.Regressed);
        state.Regressions.Keys.Should().Equal("Store/Order");
        state.Retracted.Should().BeEmpty();
    }

    /// <summary>
    /// A type that breaks AGAIN after recovering gates again, and the health payload says
    /// "regressed" rather than "recovered" — the newest measurement always wins.
    /// </summary>
    [Fact]
    public void ATypeThatFailsAgainAfterRetraction_RegressesAgain()
    {
        var state = new NodeTypeBakeGateState();
        state.MarkRunning("go");
        state.MarkOutcome(Failed("Flaky/Type", wasHealthy: true));
        state.MarkComplete("done");
        state.RetractRegression("Flaky/Type", "rebuilt").Should().BeTrue();
        state.Phase.Should().Be(BakePhase.Complete);

        state.MarkOutcome(Failed("Flaky/Type", wasHealthy: true)).Should().BeTrue();

        state.Phase.Should().Be(BakePhase.Regressed);
        state.Regressions.Keys.Should().Equal("Flaky/Type");
        state.Retracted.Should().BeEmpty("a fresh verdict supersedes the earlier retraction");
    }

    /// <summary>
    /// <see cref="NodeTypeBakeGateState.MarkOutcome"/> reports whether it RECORDED a regression —
    /// that boolean is what tells the pre-warmer which types to watch for recovery. A non-gating
    /// outcome must never start a watch (and never claim one was needed).
    /// </summary>
    [Fact]
    public void MarkOutcome_ReportsOnlyGatingOutcomes()
    {
        var state = new NodeTypeBakeGateState();
        state.MarkRunning("go");

        state.MarkOutcome(new PreWarmOutcome("Ok/Type", PreWarmStatus.Compiled)).Should().BeFalse();
        state.MarkOutcome(Failed("Abandoned/Type", wasHealthy: false)).Should().BeFalse(
            "a type that was already broken is not a regression");
        state.MarkOutcome(new PreWarmOutcome("Slow/Type", PreWarmStatus.TimedOut)
            { WasHealthyBeforeBake = true }).Should().BeFalse("a timeout is not a verdict");
        state.MarkOutcome(new PreWarmOutcome("Gone/Type", PreWarmStatus.NoSources)
            { WasHealthyBeforeBake = true }).Should().BeFalse("content-broken is not an image verdict");
        state.MarkOutcome(Failed("Real/Regression", wasHealthy: true)).Should().BeTrue();
    }

    /// <summary>
    /// 🚨 A DERIVED regression is retracted WITH its blocker, and is never watched on its own.
    ///
    /// <para>A dependent skipped as <see cref="PreWarmStatus.UpstreamFailed"/> was never compiled —
    /// the sweep skips it precisely so it does not have to activate its hub. Its regression's whole
    /// evidence is "my upstream failed", so when the upstream's verdict is withdrawn this one has
    /// nothing holding it up. Watching each dependent for its own recovery instead would activate
    /// every skipped dependent of one broken upstream and hold them for the pod's lifetime, undoing
    /// the saving the skip exists for.</para>
    /// </summary>
    [Fact]
    public void RetractingABlocker_AlsoRetractsTheRegressionsDerivedFromIt()
    {
        var state = new NodeTypeBakeGateState { GatesReadiness = true };
        state.MarkRunning("go");

        state.MarkOutcome(Failed("Store/Plugin", wasHealthy: true))
            .Should().BeTrue("a directly-measured verdict is the one worth watching");
        state.MarkOutcome(new PreWarmOutcome(
                "Store/Catalog", PreWarmStatus.UpstreamFailed, "blocked by Store/Plugin")
            { WasHealthyBeforeBake = true, BlockedBy = "Store/Plugin" })
            .Should().BeFalse("a derived regression must not start a watch — that is what would "
                + "activate the whole fan-out of one broken upstream");
        // Transitive: a dependent of the dependent.
        state.MarkOutcome(new PreWarmOutcome(
                "Store/Order", PreWarmStatus.UpstreamFailed, "blocked by Store/Catalog")
            { WasHealthyBeforeBake = true, BlockedBy = "Store/Catalog" })
            .Should().BeFalse();
        state.MarkComplete("baked");

        state.Phase.Should().Be(BakePhase.Regressed,
            "all three gate — a derived regression still stalls the rollout");
        state.Regressions.Keys.OrderBy(k => k, StringComparer.Ordinal).Should().Equal(
            "Store/Catalog", "Store/Order", "Store/Plugin");

        // The blocker rebuilds. Its dependents' verdicts had no other basis.
        state.RetractRegression("Store/Plugin", "rebuilt on this image").Should().BeTrue();

        state.Phase.Should().Be(BakePhase.Complete,
            "with the only measured failure withdrawn, nothing that was derived from it may keep "
            + "the pod out of rotation");
        state.Regressions.Should().BeEmpty();
        state.Retracted.Keys.OrderBy(k => k, StringComparer.Ordinal).Should().Equal(
            "Store/Catalog", "Store/Order", "Store/Plugin");
    }

    /// <summary>
    /// The cascade is keyed on the ACTUAL blocker — an unrelated regression is not swept up with
    /// it. Otherwise retracting one type would quietly release the gate on everything.
    /// </summary>
    [Fact]
    public void RetractingABlocker_LeavesUnrelatedRegressionsStanding()
    {
        var state = new NodeTypeBakeGateState { GatesReadiness = true };
        state.MarkRunning("go");
        state.MarkOutcome(Failed("Store/Plugin", wasHealthy: true));
        state.MarkOutcome(new PreWarmOutcome(
                "Store/Catalog", PreWarmStatus.UpstreamFailed, "blocked by Store/Plugin")
            { WasHealthyBeforeBake = true, BlockedBy = "Store/Plugin" });
        state.MarkOutcome(Failed("Unrelated/Type", wasHealthy: true));
        state.MarkComplete("baked");

        state.RetractRegression("Store/Plugin", "rebuilt on this image").Should().BeTrue();

        state.Phase.Should().Be(BakePhase.Regressed);
        state.Regressions.Keys.Should().Equal("Unrelated/Type");
    }

    /// <summary>
    /// The torn-snapshot EVIDENCE (issue #1214, proposal 3): a source whose <c>LastModified</c> is
    /// at or after the compile's start stamp proves the compile sampled a source set the mesh was
    /// mid-way through replacing. It is diagnostic only — it names the suspicion in the log and
    /// never downgrades the verdict, because a torn compile of genuinely broken code must still gate.
    /// </summary>
    [Fact]
    public void SourcesMovedDuringCompile_IsTrue_WhenASourceWasWrittenAfterTheCompileStarted()
    {
        var started = new DateTimeOffset(2026, 8, 11, 11, 4, 30, TimeSpan.Zero);
        DynamicTypePreWarmer.SourcesMovedDuringCompile(new NodeTypeDefinition
        {
            Sources = ["namespace:Source scope:subtree"],
            LastCompileStartedAt = started,
            CurrentSourceVersions = new Dictionary<string, long>
            {
                ["Store/Plugin/Source/Localizer"] = started.AddSeconds(29).UtcTicks
            }
        }).Should().BeTrue("the source moved WHILE the compile was running — a torn snapshot");
    }

    /// <summary>A source set that has not moved since the compile started is STABLE — no suspicion
    /// to name, and Roslyn's verdict is about the code.</summary>
    [Fact]
    public void SourcesMovedDuringCompile_IsFalse_OnAStableSourceSet()
    {
        var started = new DateTimeOffset(2026, 8, 11, 11, 4, 30, TimeSpan.Zero);
        DynamicTypePreWarmer.SourcesMovedDuringCompile(new NodeTypeDefinition
        {
            Sources = ["namespace:Source scope:subtree"],
            LastCompileStartedAt = started,
            CurrentSourceVersions = new Dictionary<string, long>
            {
                ["Store/Plugin/Source/Localizer"] = started.AddMinutes(-10).UtcTicks
            }
        }).Should().BeFalse();
    }

    /// <summary>
    /// No start stamp ⇒ no evidence. The predicate must never guess: an absent
    /// <see cref="NodeTypeDefinition.LastCompileStartedAt"/> means we cannot say when the compile
    /// ran, and "I cannot tell" is not "the snapshot was torn".
    /// </summary>
    [Fact]
    public void SourcesMovedDuringCompile_IsFalse_WithoutACompileStartStamp() =>
        DynamicTypePreWarmer.SourcesMovedDuringCompile(new NodeTypeDefinition
        {
            Sources = ["namespace:Source scope:subtree"],
            CurrentSourceVersions = new Dictionary<string, long> { ["P/Source/A"] = 42 }
        }).Should().BeFalse();

    /// <summary>A regression among successes still gates — one bad type is enough.</summary>
    [Fact]
    public void OneRegressionAmongSuccesses_StillGates()
    {
        var state = new NodeTypeBakeGateState();
        state.MarkRunning("go");
        state.MarkOutcome(new PreWarmOutcome("Good", PreWarmStatus.Compiled));
        state.MarkOutcome(Failed("Bad", wasHealthy: true));
        state.MarkOutcome(new PreWarmOutcome("AlsoGood", PreWarmStatus.AlreadyBaked));
        state.MarkComplete("done");

        state.Phase.Should().Be(BakePhase.Regressed);
        state.Regressions.Keys.Should().Equal("Bad");
    }

    /// <summary>
    /// A type SKIPPED because its upstream failed is itself a failure to reach a usable build, so it
    /// counts — otherwise a broken dependency would gate while its dependents slipped through.
    /// </summary>
    [Fact]
    public void UpstreamFailedOnAPreviouslyHealthyType_AlsoGates()
    {
        var state = new NodeTypeBakeGateState();
        state.MarkRunning("go");
        state.MarkOutcome(new PreWarmOutcome("Dependent", PreWarmStatus.UpstreamFailed, "blocked by Up")
        {
            WasHealthyBeforeBake = true
        });

        state.Phase.Should().Be(BakePhase.Regressed);
        state.Regressions.Keys.Should().Contain("Dependent");
    }
    /// <summary>
    /// 🚨 A TIMEOUT IS NOT A VERDICT. The per-type budget elapsing means the sweep never got an
    /// answer — not that the type is broken. During a roll the baking pod and the serving pod are
    /// two silos and the sweep's source resolution can time out across that boundary (core #694),
    /// so a perfectly healthy type times out for reasons that have nothing to do with it.
    ///
    /// <para>Counting that as a regression stalled memex-cloud's rollout on 2026-08-02 — "7
    /// NodeType(s) regressed on this image" with not one CS#### diagnostic in the log; every one
    /// was a SubscribeRequest timeout. The old image kept serving, but self-update stopped
    /// advancing, which for an auto-updating fleet is the worse failure.</para>
    /// </summary>
    [Fact]
    public void TimedOutOnAPreviouslyHealthyType_DoesNotGate()
    {
        var state = new NodeTypeBakeGateState();
        state.MarkRunning("go");
        state.MarkOutcome(new PreWarmOutcome("Slow/Type", PreWarmStatus.TimedOut, "budget elapsed")
        {
            WasHealthyBeforeBake = true
        });
        state.MarkComplete("done");

        state.Phase.Should().Be(BakePhase.Complete);
        state.Regressions.Should().BeEmpty();
        state.Unevaluated.Keys.Should().Contain("Slow/Type");
    }

    /// <summary>
    /// Non-blocking must not mean invisible: a swallowed timeout is exactly how a real regression
    /// would hide behind that leniency, so the health payload names what it could not evaluate.
    /// </summary>
    [Fact]
    public void UnevaluatedTypes_AreNamedInTheHealthDetail()
    {
        var state = new NodeTypeBakeGateState();
        state.MarkRunning("go");
        state.MarkOutcome(new PreWarmOutcome("Slow/Type", PreWarmStatus.TimedOut) { WasHealthyBeforeBake = true });
        state.MarkComplete("all good");

        state.Detail.Should().Contain("Slow/Type").And.Contain("not evaluated");
    }

    /// <summary>
    /// 🚨 The depth-1 hole. Refusing to gate on a DIRECT timeout is worth nothing if the same
    /// unevaluated upstream gates through its dependents instead: a timed-out shared source turned
    /// every previously-healthy dependent into <see cref="PreWarmStatus.UpstreamFailed"/>, which
    /// gates — so the 2026-08-02 memex-cloud stall came straight back one hop downstream.
    ///
    /// <para>The warmer now distinguishes the two cascades, and a dependent of something that was
    /// never evaluated is itself never evaluated. "I don't know" propagates as "I don't know".</para>
    /// </summary>
    [Fact]
    public void UpstreamUnevaluatedOnAPreviouslyHealthyType_DoesNotGate()
    {
        var state = new NodeTypeBakeGateState();
        state.MarkRunning("go");
        state.MarkOutcome(new PreWarmOutcome("Dependent", PreWarmStatus.UpstreamUnevaluated, "blocked by Slow/Up")
        {
            WasHealthyBeforeBake = true
        });
        state.MarkComplete("done");

        state.Phase.Should().Be(BakePhase.Complete);
        state.Regressions.Should().BeEmpty();
        state.Unevaluated.Keys.Should().Contain("Dependent");
        state.Detail.Should().Contain("Dependent").And.Contain("not evaluated");
    }

    /// <summary>
    /// The leniency is scoped to timeouts ONLY. Roslyn rejecting the type is a verdict, and it must
    /// still stall the rollout — otherwise the gate stops being a gate.
    /// </summary>
    [Fact]
    public void CompileErrorStillGates_EvenAlongsideATimeout()
    {
        var state = new NodeTypeBakeGateState();
        state.MarkRunning("go");
        state.MarkOutcome(new PreWarmOutcome("Slow/Type", PreWarmStatus.TimedOut) { WasHealthyBeforeBake = true });
        state.MarkOutcome(Failed("Broken/Type", wasHealthy: true));
        state.MarkComplete("done");

        state.Phase.Should().Be(BakePhase.Regressed);
        state.Regressions.Keys.Should().Equal("Broken/Type");
    }


    /// <summary>Outcomes default to "was healthy" so a caller that never sets the flag fails safe.</summary>
    [Fact]
    public void OutcomeWasHealthyBeforeBake_DefaultsToTrue() =>
        new PreWarmOutcome("A", PreWarmStatus.Compiled).WasHealthyBeforeBake.Should().BeTrue();

    /// <summary>
    /// 🚨 A CONTENT verdict is not an IMAGE verdict. A type whose declared source queries match
    /// nothing any more — its sources deleted out from under it — fails on EVERY image, so gating
    /// on it freezes the fleet exactly like the abandoned-Error case the WasHealthyBeforeBake rule
    /// exists for. Lived through on 2026-08-10: four KmuBasics/* types (their course re-installed
    /// under a new id, the type nodes left behind) stalled memex-cloud's self-update across two
    /// successive images, while the stuck-but-baking pod wedged the cluster's routing.
    /// </summary>
    [Fact]
    public void NoSourcesOnAPreviouslyHealthyType_DoesNotGate()
    {
        var state = new NodeTypeBakeGateState();
        state.MarkRunning("go");
        state.MarkOutcome(new PreWarmOutcome(
            "KmuBasics/Buchungsjournal", PreWarmStatus.NoSources, "0 source nodes matched")
        {
            WasHealthyBeforeBake = true
        });
        state.MarkComplete("done");

        state.Phase.Should().Be(BakePhase.Complete);
        state.Regressions.Should().BeEmpty();
        state.ContentBroken.Keys.Should().Contain("KmuBasics/Buchungsjournal");
    }

    /// <summary>
    /// The depth-1 rule, content edition: leniency on the direct NoSources verdict is worth
    /// nothing if the identical condition gates through the dependents as UpstreamFailed.
    /// </summary>
    [Fact]
    public void UpstreamContentBrokenOnAPreviouslyHealthyType_DoesNotGate()
    {
        var state = new NodeTypeBakeGateState();
        state.MarkRunning("go");
        state.MarkOutcome(new PreWarmOutcome(
            "Dependent", PreWarmStatus.UpstreamContentBroken, "blocked by KmuBasics/Buchungsjournal")
        {
            WasHealthyBeforeBake = true
        });
        state.MarkComplete("done");

        state.Phase.Should().Be(BakePhase.Complete);
        state.Regressions.Should().BeEmpty();
        state.ContentBroken.Keys.Should().Contain("Dependent");
    }

    /// <summary>Non-blocking must not mean invisible — the health payload names what content broke.</summary>
    [Fact]
    public void ContentBrokenTypes_AreNamedInTheHealthDetail()
    {
        var state = new NodeTypeBakeGateState();
        state.MarkRunning("go");
        state.MarkOutcome(new PreWarmOutcome("Gone/Type", PreWarmStatus.NoSources)
        {
            WasHealthyBeforeBake = true
        });
        state.MarkComplete("all good");

        state.Detail.Should().Contain("Gone/Type").And.Contain("content-broken");
    }

    /// <summary>A content-broken type must not dilute a REAL regression happening beside it.</summary>
    [Fact]
    public void CompileErrorStillGates_EvenAlongsideAContentBrokenType()
    {
        var state = new NodeTypeBakeGateState();
        state.MarkRunning("go");
        state.MarkOutcome(new PreWarmOutcome("Gone/Type", PreWarmStatus.NoSources)
        {
            WasHealthyBeforeBake = true
        });
        state.MarkOutcome(Failed("Broken/Type", wasHealthy: true));
        state.MarkComplete("done");

        state.Phase.Should().Be(BakePhase.Regressed);
        state.Regressions.Keys.Should().Equal("Broken/Type");
    }

    // =============================================================================================
    // ClassifyCompileFailure — "no sources matched" is NOT "sources matched and did not compile".
    //
    // The first is a CONTENT fact and must not gate readiness; the second is a verdict about this
    // image and must. The ONLY evidence is CurrentSourceVersions: explicitly empty ⇒ NoSources,
    // anything else ⇒ CompileError.
    //
    // 🚨 The classifier used to ALSO require d.Sources is { Count: > 0 }. That is issue #1391: an
    // empty Sources does not mean "configuration-only", it means "uses the DEFAULT {path}/Source
    // query" — how nearly every NodeType in a real mesh is authored — so NoSources was unreachable
    // for almost the entire population and a DELETED type gated readiness forever. This block is
    // the boundary; none of it may be relaxed into "compile errors stop gating".
    // =============================================================================================

    /// <summary>
    /// Declared source queries + an EXPLICITLY empty live snapshot = the sources are gone =
    /// <see cref="PreWarmStatus.NoSources"/>.
    /// </summary>
    [Fact]
    public void ClassifyCompileFailure_DeclaredSourcesWithEmptySnapshot_IsNoSources() =>
        DynamicTypePreWarmer.ClassifyCompileFailure(new NodeTypeDefinition
        {
            Sources = ["namespace:Source scope:subtree"],
            CurrentSourceVersions = new Dictionary<string, long>()
        }).Should().Be(PreWarmStatus.NoSources);

    /// <summary>
    /// 🚨 A NULL snapshot is "the watcher never seeded", NOT "the sources are gone" — the sweep
    /// does not actually know, so the failure keeps gating. A real regression must not hide
    /// behind an unseeded snapshot.
    /// </summary>
    [Fact]
    public void ClassifyCompileFailure_NullSnapshot_StaysCompileError() =>
        DynamicTypePreWarmer.ClassifyCompileFailure(new NodeTypeDefinition
        {
            Sources = ["namespace:Source scope:subtree"]
        }).Should().Be(PreWarmStatus.CompileError);

    /// <summary>Sources still matching = the failure is about the CODE on this image. It gates.</summary>
    [Fact]
    public void ClassifyCompileFailure_MatchedSources_StaysCompileError() =>
        DynamicTypePreWarmer.ClassifyCompileFailure(new NodeTypeDefinition
        {
            Sources = ["namespace:Source scope:subtree"],
            CurrentSourceVersions = new Dictionary<string, long> { ["P/Source/A"] = 42 }
        }).Should().Be(PreWarmStatus.CompileError);

    /// <summary>
    /// 🚨 #1391, and the reason the extra <c>Sources is { Count: &gt; 0 }</c> conjunct had to go.
    /// This type declares no source queries, so it uses the DEFAULT <c>{path}/Source</c> subtree
    /// query — which is how nearly every NodeType in a real mesh is authored, NOT a marker for
    /// "configuration-only". Its <c>Source/</c> subtree was deleted, so the live snapshot is
    /// explicitly empty and the configuration lambda no longer compiles.
    ///
    /// <para>This previously asserted <c>CompileError</c> on the premise that an empty
    /// <c>Sources</c> means the type "compiles from its Configuration alone". That premise is
    /// wrong about the domain, and the cost of it was concrete: <c>Edu/Course</c> — a node deleted
    /// in the 2026-08-12 Edu rework — held <c>memex</c>'s portal readiness on 100% of pod boots,
    /// reported as an image regression, with a diagnostic pointing at code that no longer
    /// existed.</para>
    /// </summary>
    [Fact]
    public void ClassifyCompileFailure_DefaultQueriesWithEmptySnapshot_IsNoSources() =>
        DynamicTypePreWarmer.ClassifyCompileFailure(new NodeTypeDefinition
        {
            CurrentSourceVersions = new Dictionary<string, long>()
        }).Should().Be(PreWarmStatus.NoSources);

    /// <summary>
    /// 🚨 THE REGRESSION GUARD. The fix above must never widen into "compile errors stop gating".
    /// A type on the DEFAULT queries whose sources are still present and simply do not compile is
    /// a verdict about this image, and it gates — exactly as it did before #1391. This is the
    /// default-query twin of <c>ClassifyCompileFailure_MatchedSources_StaysCompileError</c>: the
    /// two differ only in whether the queries were declared, which is now correctly irrelevant.
    /// </summary>
    [Fact]
    public void ClassifyCompileFailure_DefaultQueriesWithSourcesStillPresent_StaysCompileError() =>
        DynamicTypePreWarmer.ClassifyCompileFailure(new NodeTypeDefinition
        {
            CurrentSourceVersions = new Dictionary<string, long> { ["P/Source/A"] = 42 }
        }).Should().Be(PreWarmStatus.CompileError);

    /// <summary>
    /// The same "not seeded is not gone" rule, on the default queries: a NULL snapshot means the
    /// watcher never ran, so the sweep does not know, so the failure keeps gating.
    /// </summary>
    [Fact]
    public void ClassifyCompileFailure_DefaultQueriesWithNullSnapshot_StaysCompileError() =>
        DynamicTypePreWarmer.ClassifyCompileFailure(new NodeTypeDefinition())
            .Should().Be(PreWarmStatus.CompileError);

    /// <summary>
    /// End-to-end through the gate, not just the classifier: the deleted default-query type must
    /// leave the pod READY. This is the property #1391 is actually about — the classification only
    /// matters because of what the gate does with it.
    /// </summary>
    [Fact]
    public void DeletedDefaultQueryType_DoesNotGateReadiness()
    {
        var state = new NodeTypeBakeGateState();
        state.MarkRunning("go");
        state.MarkOutcome(new PreWarmOutcome(
            "Edu/Course",
            DynamicTypePreWarmer.ClassifyCompileFailure(new NodeTypeDefinition
            {
                CurrentSourceVersions = new Dictionary<string, long>()
            }),
            "0 source nodes matched")
        { WasHealthyBeforeBake = true });
        state.MarkComplete("done");

        state.Regressions.Should().BeEmpty(
            "a node that no longer exists is content, not an image regression");
        state.Phase.Should().Be(BakePhase.Complete);
    }

    /// <summary>
    /// …and the converse, end-to-end: a default-query type whose sources are still there and break
    /// on THIS image must still refuse readiness. Same shape as the test above, one field apart.
    /// </summary>
    [Fact]
    public void BrokenDefaultQueryTypeWithLiveSources_StillGatesReadiness()
    {
        var state = new NodeTypeBakeGateState();
        state.MarkRunning("go");
        state.MarkOutcome(new PreWarmOutcome(
            "Edu/Exercise",
            DynamicTypePreWarmer.ClassifyCompileFailure(new NodeTypeDefinition
            {
                CurrentSourceVersions = new Dictionary<string, long>
                {
                    ["Edu/Exercise/Source/ExerciseContent"] = 42
                }
            }),
            "CS0246")
        { WasHealthyBeforeBake = true });
        state.MarkComplete("done");

        state.Regressions.Keys.Should().Equal("Edu/Exercise");
        state.Phase.Should().Be(BakePhase.Regressed);
    }

    // =============================================================================================
    // A SWEEP THAT ERRORED IS NOT A SWEEP THAT PASSED (BakePhase.Faulted).
    //
    // The error handler used to call MarkComplete, so a pod whose enumeration threw reported
    // Complete → Healthy and took traffic having verified nothing. The end-to-end proof — real
    // sweep, real gate, real health check — is NodeTypeBakeGateFaultTest; these pin the state
    // machine's own edges, which that test cannot reach individually.
    // =============================================================================================

    /// <summary>The core distinction: an errored sweep is neither Complete nor NotStarted.</summary>
    [Fact]
    public void MarkFaulted_IsNotComplete()
    {
        var state = new NodeTypeBakeGateState();
        state.MarkRunning("go");
        state.MarkFaulted("enumeration threw");

        state.Phase.Should().Be(BakePhase.Faulted,
            "the sweep verified nothing — Complete is a claim about a sweep that RAN");
        state.Detail.Should().Contain("NOT PROVEN");
        state.Regressions.Should().BeEmpty(
            "a fault is not a verdict about any particular type");
    }

    /// <summary>
    /// A standing regression outranks a later fault. Both refuse readiness, but "these named types
    /// regressed" is the more actionable payload, and evidence the sweep DID gather must not be
    /// erased by the way it ended.
    /// </summary>
    [Fact]
    public void MarkFaulted_DoesNotEraseARegression()
    {
        var state = new NodeTypeBakeGateState();
        state.MarkRunning("go");
        state.MarkOutcome(Failed("Store/Plugin", wasHealthy: true));
        state.MarkFaulted("the stream faulted at type 200 of 240");

        state.Phase.Should().Be(BakePhase.Regressed);
        state.Regressions.Keys.Should().Contain("Store/Plugin");
    }

    /// <summary>
    /// 🚨 Retracting the last regression after a FAULTED sweep returns to Faulted — never Complete.
    ///
    /// <para>Retraction is a better measurement of ONE type; it says nothing about the other 239 the
    /// errored sweep never reached. Landing on Complete here would let a pod launder an unproven
    /// bake into a passed one by way of a single type recovering — the original defect, rebuilt out
    /// of the recovery machinery.</para>
    /// </summary>
    [Fact]
    public void RetractingTheLastRegressionAfterAFaultedSweep_ReturnsToFaulted_NotComplete()
    {
        var state = new NodeTypeBakeGateState();
        state.MarkRunning("go");
        state.MarkOutcome(Failed("Store/Plugin", wasHealthy: true));
        state.MarkFaulted("the stream faulted");

        state.RetractRegression("Store/Plugin", "rebuilt on this image").Should().BeTrue();

        state.Phase.Should().Be(BakePhase.Faulted,
            "one type recovering does not turn an errored sweep into a successful one");
        state.Detail.Should().Contain("NOT PROVEN");
    }

    /// <summary>
    /// The escape hatch is a VERDICT relaxation, not a state rewrite — the recorded phase is
    /// identical with and without it. A flag that rewrote the state would recreate the original
    /// defect one level up, where nothing could see it.
    /// </summary>
    [Fact]
    public void AllowUnprovenBake_DoesNotChangeWhatIsRecorded()
    {
        var permissive = new NodeTypeBakeGateState { AllowUnprovenBake = true };
        permissive.MarkRunning("go");
        permissive.MarkFaulted("enumeration threw");

        permissive.Phase.Should().Be(BakePhase.Faulted);
        permissive.Detail.Should().Contain("NOT PROVEN");
    }

    /// <summary>
    /// …and it can never launder a real regression: only the unproven-bake verdict is waivable.
    /// </summary>
    [Fact]
    public void AllowUnprovenBake_StillGatesARealRegression()
    {
        var permissive = new NodeTypeBakeGateState { AllowUnprovenBake = true };
        permissive.MarkRunning("go");
        permissive.MarkOutcome(Failed("Store/Plugin", wasHealthy: true));
        permissive.MarkFaulted("and then the stream faulted too");

        permissive.Phase.Should().Be(BakePhase.Regressed,
            "the override waives 'I could not find out', never 'this type broke'");
    }

    /// <summary>Defaults stay strict — an unproven bake refuses readiness unless asked otherwise.</summary>
    [Fact]
    public void AllowUnprovenBake_DefaultsToFalse() =>
        new NodeTypeBakeGateState().AllowUnprovenBake.Should().BeFalse();
}
