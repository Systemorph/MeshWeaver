using System;
using System.Collections.Generic;
using System.Reactive.Linq;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using MeshWeaver.Reactive.Assertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Hosting.Test;

/// <summary>
/// 🚨 <b>A NodeType its repository has RETIRED is not a regression of the image</b> — measured on
/// <c>memex.systemorph.com</c>, 2026-09-08, new pod on image <c>3.0.0-ci.8131</c>:
///
/// <code>
/// 20:29:14Z [StaticRepoImport] Crm: Pruned 1 NodeType(s) that still have instances … 'Crm/Mail'
/// 20:29:52Z Failed to compile assembly for node 'Crm/Mail' … No node found at 'Crm/Mail'
/// 20:31:12Z DynamicTypePreWarmer: REFUSING READINESS — 1 NodeType(s) regressed on this image: Crm/Mail
/// </code>
///
/// <para>The sweep enumerated <c>Crm/Mail</c>, a sync pruned it 38 seconds before its compile, the
/// compile then failed against a node that no longer existed, and the failure was filed as a
/// <c>CompileError</c> on a healthy baseline — a regression. The recovery watch subscribed to the
/// missing node, faulted, and logged "the regression STANDS (a watch that cannot observe a
/// recovery must never be read as one)": correct for an existing node whose read faults, fatal for
/// a node that is GONE, because nothing could ever retract it. Every rollout on every instance was
/// held on a deliberate retirement.</para>
///
/// <para>Two classifications close it, both content verdicts (they cascade as
/// <see cref="PreWarmStatus.UpstreamContentBroken"/>, and the gate files them under
/// <see cref="NodeTypeBakeGateState.Retired"/> without stalling):
/// <see cref="PreWarmStatus.Removed"/> — the definition node no longer exists, asserted by a
/// LISTING; and <see cref="PreWarmStatus.Retired"/> — the definition is held for its remaining
/// instances (<see cref="NodeTypeDefinition.PendingRetirement"/>), so its withdrawn sources are
/// not the image's business. And a standing regression whose node disappears is WITHDRAWN into the
/// same bucket (<see cref="NodeTypeBakeGateState.RetireRegression"/>), not laundered into a
/// recovery.</para>
///
/// <para>Every leniency here has a negative control beside it: an existing node keeps its verdict,
/// a real compile error still gates, a faulted listing answers "present".</para>
/// </summary>
public class ARetiredNodeTypeIsNotARegressionTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    // ——— the gate: Retired / Removed do not gate; a standing regression can be withdrawn ————

    [Theory(Timeout = 60000)]
    [InlineData(PreWarmStatus.Retired)]
    [InlineData(PreWarmStatus.Removed)]
    public void ARetiredOutcome_OnAHealthyType_DoesNotGate_AndIsNamed(PreWarmStatus status)
    {
        var gate = new NodeTypeBakeGateState { GatesReadiness = true };
        gate.MarkRunning("enumerating dynamic NodeTypes");

        var watch = gate.MarkOutcome(new PreWarmOutcome("Crm/Mail", status, "retired by Crm")
        {
            WasHealthyBeforeBake = true,
        });

        watch.Should().BeFalse("there is nothing to watch for — no recovery can bring a retired type back");
        gate.Regressions.Should().BeEmpty("a retirement is a content fact, not an image verdict");
        gate.Retired.Keys.Should().Contain("Crm/Mail");
        gate.Phase.Should().Be(BakePhase.Running, "the sweep is still measuring, undisturbed");

        gate.MarkComplete("baked in 00:01:00 — compiled=3 alreadyBaked=0");

        gate.ReadinessGranted.Should().BeTrue("the pod may serve — the retirement is not its business");
        gate.Detail.Should().Contain("Crm/Mail").And.Contain("retired",
            "non-blocking must not mean invisible: the health payload names the retired type");
    }

    [Fact(Timeout = 60000)]
    public void ACompileErrorOnAHealthyType_StillGates()
    {
        // The negative control: the classification changed, the gate did not.
        var gate = new NodeTypeBakeGateState { GatesReadiness = true };
        gate.MarkRunning("enumerating dynamic NodeTypes");

        var watch = gate.MarkOutcome(new PreWarmOutcome("Crm/Contact", PreWarmStatus.CompileError, "CS0246")
        {
            WasHealthyBeforeBake = true,
        });

        watch.Should().BeTrue();
        gate.Phase.Should().Be(BakePhase.Regressed);
        gate.Retired.Should().BeEmpty();
        gate.ReadinessGranted.Should().BeFalse();
    }

    [Fact(Timeout = 60000)]
    public void AStandingRegression_WhoseNodeWasRemoved_IsWithdrawn_NotRecovered()
    {
        var gate = new NodeTypeBakeGateState { GatesReadiness = true };
        gate.MarkRunning("enumerating dynamic NodeTypes");
        gate.MarkOutcome(new PreWarmOutcome("Crm/Mail", PreWarmStatus.CompileError, "No node found at 'Crm/Mail'")
        {
            WasHealthyBeforeBake = true,
        });
        // A dependent skipped because Crm/Mail failed — its whole evidence is the blocker's verdict.
        gate.MarkOutcome(new PreWarmOutcome("Crm/MailReport", PreWarmStatus.UpstreamFailed, "blocked by Crm/Mail")
        {
            WasHealthyBeforeBake = true,
            BlockedBy = "Crm/Mail",
        });
        gate.MarkComplete("baked in 00:02:00 — compiled=10 alreadyBaked=0");
        gate.Phase.Should().Be(BakePhase.Regressed, "the fixture must start from the stall it repairs");
        gate.ReadinessGranted.Should().BeFalse();

        var withdrawn = gate.RetireRegression("Crm/Mail", "the NodeType definition no longer exists");

        withdrawn.Should().BeTrue();
        gate.Regressions.Should().BeEmpty(
            "the derived verdict cascades exactly as for a retraction — with the blocker's verdict "
            + "gone there is no evidence left against the dependent either");
        gate.Retired.Keys.Should().Contain("Crm/Mail");
        gate.Retracted.Keys.Should().NotContain("Crm/Mail",
            "a withdrawn regression is not a RECOVERY — the health payload must not say the type rebuilt");
        gate.Retracted.Keys.Should().Contain("Crm/MailReport");
        gate.Phase.Should().Be(BakePhase.Complete);
        gate.ReadinessGranted.Should().BeTrue("with nothing left to have broken, the rollout proceeds");

        gate.RetireRegression("Crm/Mail", "again").Should().BeFalse(
            "withdrawing is idempotent: a second call finds no regression to move");
        gate.RetireRegression("Crm/Contact", "never regressed").Should().BeFalse();
    }

    // ——— the classifications, pure ————————————————————————————————————————————————

    [Fact(Timeout = 60000)]
    public void AHeldDefinition_ClassifiesAsRetired_BeforeAnythingElseIsAsked()
    {
        var held = new NodeTypeDefinition
        {
            Configuration = "config => config",
            PendingRetirement = "Retired by Crm import abc at 2026-09-08T20:29:14Z; held for 1 instance(s): PartnerRe/Esl/DueDiligenceMail.",
            // Sources still match (the shared folder is non-empty), a build once succeeded:
            // without the stamp this is the textbook gating CompileError.
            CurrentSourceVersions = new Dictionary<string, long> { ["Crm/Source/Contact.cs"] = 1 },
            LastCompileSucceededAt = DateTimeOffset.UtcNow.AddDays(-1),
        };

        DynamicTypePreWarmer.ClassifyCompileFailure(held).Should().Be(PreWarmStatus.Retired);
        DynamicTypePreWarmer.ClassifyCompileFailure(held with { PendingRetirement = null })
            .Should().Be(PreWarmStatus.CompileError,
                "the control: the same definition without the stamp keeps gating");
        DynamicTypePreWarmer.ClassifyCompileFailure(held with { PendingRetirement = "" })
            .Should().Be(PreWarmStatus.CompileError, "an empty stamp is no stamp");
    }

    [Fact(Timeout = 60000)]
    public void AnImageVerdict_AgainstAnAbsentNode_IsRemoved_AndNothingElseMoves()
    {
        var compileError = new PreWarmOutcome("Crm/Mail", PreWarmStatus.CompileError, "No node found at 'Crm/Mail'")
        {
            WasHealthyBeforeBake = true,
        };

        var removed = DynamicTypePreWarmer.ReclassifyAbsent(compileError, nodeExists: false);
        removed.Status.Should().Be(PreWarmStatus.Removed);
        removed.Detail.Should().Contain("CompileError").And.Contain("No node found",
            "the original measurement is kept inside the detail — reclassified, not erased");
        removed.WasHealthyBeforeBake.Should().BeTrue("everything but the verdict is carried unchanged");

        DynamicTypePreWarmer.ReclassifyAbsent(compileError, nodeExists: true)
            .Should().BeSameAs(compileError, "an existing node keeps its verdict — this is the control");

        var faulted = new PreWarmOutcome("Crm/Mail", PreWarmStatus.Faulted, "the stamp write faulted");
        DynamicTypePreWarmer.ReclassifyAbsent(faulted, nodeExists: false).Status
            .Should().Be(PreWarmStatus.Removed, "a fault against a missing node is the same non-verdict");

        foreach (var notAVerdict in new[]
                 {
                     PreWarmStatus.TimedOut, PreWarmStatus.Compiled, PreWarmStatus.AlreadyBaked,
                     PreWarmStatus.NoSources, PreWarmStatus.UpstreamFailed, PreWarmStatus.UpstreamUnevaluated,
                 })
        {
            var outcome = new PreWarmOutcome("Crm/Mail", notAVerdict);
            DynamicTypePreWarmer.ReclassifyAbsent(outcome, nodeExists: false)
                .Should().BeSameAs(outcome, $"{notAVerdict} is not an image verdict and must not be touched");
        }
    }

    // ——— the existence question, against a real mesh ——————————————————————————————

    /// <summary>
    /// Absence is asserted by a LISTING that came back and did not name the node — never by a
    /// point read (which on an absent node terminates with a routing NotFound and opens the
    /// storm-breaker), never by the shape of the failure message.
    /// </summary>
    [Fact(Timeout = 120000)]
    public async Task TypeNodeExists_AnswersByListing()
    {
        var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
        var typeId = "Widget" + Guid.NewGuid().ToString("N")[..8];
        var typePath = $"{TestPartition}/{typeId}";

        var absent = await DynamicTypePreWarmer.TypeNodeExists(Mesh, typePath, null)
            .FirstAsync().Timeout(TestTimeouts.Convergence).Await();
        absent.Should().BeFalse("a path nothing ever created is not in any listing");

        await meshService.CreateNode(new MeshNode(typeId, TestPartition)
            {
                NodeType = MeshNode.NodeTypePath, Name = typeId, State = MeshNodeState.Active,
                Content = new NodeTypeDefinition { Configuration = "config => config" },
            })
            .Take(1).Should().Within(TestTimeouts.Convergence).Emit("the type must exist before it is asked about");

        // The listing is eventually consistent — wait for it to reflect the create, bounded.
        var present = await Observable.Interval(200.Milliseconds()).StartWith(0L)
            .SelectMany(_ => DynamicTypePreWarmer.TypeNodeExists(Mesh, typePath, null).Take(1))
            .Where(exists => exists)
            .FirstAsync().Timeout(TestTimeouts.Convergence).Await();
        present.Should().BeTrue();
    }
}
