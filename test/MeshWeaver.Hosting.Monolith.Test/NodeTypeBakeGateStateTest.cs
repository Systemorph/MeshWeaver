using MeshWeaver.Hosting;
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

    /// <summary>Outcomes default to "was healthy" so a caller that never sets the flag fails safe.</summary>
    [Fact]
    public void OutcomeWasHealthyBeforeBake_DefaultsToTrue() =>
        new PreWarmOutcome("A", PreWarmStatus.Compiled).WasHealthyBeforeBake.Should().BeTrue();
}
