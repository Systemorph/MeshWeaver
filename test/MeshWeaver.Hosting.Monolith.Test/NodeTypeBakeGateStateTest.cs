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

    /// <summary>
    /// The classifier that feeds the buckets above: declared source queries + an EXPLICITLY empty
    /// live snapshot = the sources are gone = <see cref="PreWarmStatus.NoSources"/>.
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
    /// A type that declares NO source queries compiles from its Configuration alone — an empty
    /// snapshot is its normal state, so a failure is a verdict about this image and gates.
    /// </summary>
    [Fact]
    public void ClassifyCompileFailure_NoDeclaredSources_StaysCompileError() =>
        DynamicTypePreWarmer.ClassifyCompileFailure(new NodeTypeDefinition
        {
            CurrentSourceVersions = new Dictionary<string, long>()
        }).Should().Be(PreWarmStatus.CompileError);
}
