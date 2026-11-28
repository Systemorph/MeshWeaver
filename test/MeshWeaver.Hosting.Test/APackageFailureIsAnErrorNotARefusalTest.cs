using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive;
using System.Reactive.Linq;
using System.Threading.Tasks;
using MeshWeaver.Compiler;
using MeshWeaver.Fixture;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Reactive.Assertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Hosting.Test;

/// <summary>
/// 🚨 <b>A package's own NodeType failure is an ERROR ON THAT TYPE, never a refusal of the
/// platform roll</b> (Memex#1155; maintainer, 2026-11-28: <i>"we should probably allow the update
/// and error the code which does not compile. like this it's difficult to even see what's
/// happening"</i>).
///
/// <para><b>The incident.</b> A pod on a new 4.x image refused readiness because 7, then 13, then 24
/// package NodeTypes failed on it (compile errors, an untracked module's missing bundle, "not
/// seeded"), so the roll stalled with the 3.x pods serving. Worse, the refusing pod also refused to
/// publish its outcome (<c>MeshPublicationGate: REFUSING to publish … Nothing was written</c>), so
/// the mesh kept showing the 3.x <c>Ok</c> and no diagnostic was visible anywhere but a pod log
/// where the readiness line repeated every few seconds.</para>
///
/// <para>This file pins the declared default, one fact per test, each with its negative control:
/// the pod is Ready and admitted, the failing type reads <c>CompilationStatus.Error</c> with its
/// named refusal on the shared record, and PLATFORM-level failures (a sweep or adoption that could
/// not run at all) still refuse.</para>
/// </summary>
public class APackageFailureIsAnErrorNotARefusalTest(ITestOutputHelper output)
    : RegistryOnlyInstallationFixture(output)
{
    protected override bool StrictGate => false;

    [Fact(Timeout = 300_000)]
    public async Task AnUntrackedModuleTypeWithNoBundle_LetsThePodGoReady_AndTheTypeReadsError()
    {
        var ct = TestContext.Current.CancellationToken;
        // The registry answers but names no bundle: the module type cannot be served by this image.
        registry.Package = null;
        await SeedInstallation(ct);

        var gate = await WarmUp(ct);

        gate.Phase.Should().Be(BakePhase.Complete,
            "a package type's failure no longer holds the platform roll — the pod goes Ready");
        gate.ReadinessGranted.Should().BeTrue();
        gate.Admission.Should().Be(MeshAdmission.Admitted,
            "admission is never more permissive than readiness, and here both grant — so the Error "
            + "outcome below is allowed to reach the shared mesh");
        gate.Regressions.Should().BeEmpty();
        gate.ErrorTypes.Keys.Should().Contain(TypePath,
            $"the type is NAMED, not swallowed; errors: {string.Join(" | ", gate.ErrorTypes.Select(r => $"{r.Key} → {r.Value}"))}");
        gate.Detail.Should().Contain(TypePath).And.Contain("roll proceeds",
            "non-blocking must not mean invisible: the health payload names the type");

        var published = await Mesh.GetMeshNodeStream(TypePath)
            .Select(n => n?.ContentAs<NodeTypeDefinition>(Mesh.JsonSerializerOptions))
            .Should().Within(TestTimeouts.CrossSilo)
            .Match(d => d?.CompilationStatus == CompilationStatus.Error
                        && !string.IsNullOrWhiteSpace(d.CompilationError),
                "the Error outcome is PUBLISHED to the mesh with its named refusal — the pod used to "
                + "refuse to write it", ct);
        published?.CompilationError.Should().Contain("Untracked module content");
        published?.CompiledFrameworkVersion.Should().NotBe(PrebuiltAssemblySeeder.LiveFrameworkMvid,
            "no verified stamp for this framework is written for a build nobody verified");
    }

    // ——— platform-level failures still refuse ————————————————————————————————————————————

    [Fact(Timeout = 60000)]
    public void ASweepThatCouldNotRun_StillRefuses_UnderTheDeclaredDefault()
    {
        TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
        var gate = new NodeTypeBakeGateState { GatesReadiness = true, PackageFailuresRefuseReadiness = false };
        gate.MarkRunning("enumerating dynamic NodeTypes");

        gate.MarkFaulted("the warm-up stream faulted before the sweep could finish");

        gate.Phase.Should().Be(BakePhase.Faulted);
        gate.ReadinessGranted.Should().BeFalse("nothing was verified — that is a platform failure, not a package's");
        gate.Admission.Should().Be(MeshAdmission.Refused);
    }

    [Fact(Timeout = 60000)]
    public void AnUnmeasurableAdoption_StillRefuses_UnderTheDeclaredDefault()
    {
        TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
        var gate = new NodeTypeBakeGateState { GatesReadiness = true, PackageFailuresRefuseReadiness = false };
        gate.MarkRunning("adopting");

        gate.MarkFaulted("the prebuilt-bundle adoption could not be measured");

        gate.ReadinessGranted.Should().BeFalse();
    }

    // ——— the gate state: the declared default, and the strict option ———————————————————————

    [Fact(Timeout = 60000)]
    public void ACompileErrorOnAHealthyType_IsRecordedAsErrored_AndDoesNotGate()
    {
        TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
        var gate = new NodeTypeBakeGateState { GatesReadiness = true, PackageFailuresRefuseReadiness = false };
        gate.MarkRunning("enumerating dynamic NodeTypes");

        var watch = gate.MarkOutcome(new PreWarmOutcome("Edu/CourseCatalog", PreWarmStatus.CompileError, "error CS0246: The type 'Foo' could not be found")
        {
            WasHealthyBeforeBake = true,
            HasRegressionBaseline = true,
        });
        gate.MarkComplete("baked in 00:01:00 — compiled=3 alreadyBaked=0");

        watch.Should().BeFalse("nothing is held to watch for a recovery");
        gate.Phase.Should().Be(BakePhase.Complete);
        gate.ReadinessGranted.Should().BeTrue();
        gate.Errored["Edu/CourseCatalog"].Should().Contain("CS0246", "the diagnostic travels with the type's name");
        gate.Detail.Should().Contain("Edu/CourseCatalog").And.Contain("1 at Error");
    }

    [Fact(Timeout = 60000)]
    public void TheStrictOption_KeepsTheOriginalRefusal()
    {
        TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
        var gate = new NodeTypeBakeGateState { GatesReadiness = true, PackageFailuresRefuseReadiness = true };
        gate.MarkRunning("enumerating dynamic NodeTypes");

        gate.MarkOutcome(new PreWarmOutcome("Edu/CourseCatalog", PreWarmStatus.CompileError, "error CS0246"));
        gate.MarkComplete("baked");

        gate.Phase.Should().Be(BakePhase.Regressed);
        gate.ReadinessGranted.Should().BeFalse();
        gate.Errored.Should().BeEmpty();
    }

    [Fact(Timeout = 60000)]
    public void TheClassDefaultIsStrict_AndTheHostRegistrationDeclaresThePolicy()
    {
        TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
        new NodeTypeBakeGateState().PackageFailuresRefuseReadiness.Should().BeTrue(
            "a state built directly keeps the strict reading, so existing unit pins stay meaningful");

        StateFor(null).PackageFailuresRefuseReadiness.Should().BeFalse("absent config = a package's failure is an Error, never a refusal");
        StateFor("false").PackageFailuresRefuseReadiness.Should().BeFalse();
        StateFor("true").PackageFailuresRefuseReadiness.Should().BeTrue("an explicit true restores the strict gate");

        static NodeTypeBakeGateState StateFor(string? value)
        {
            var config = new ConfigurationBuilder()
                .AddInMemoryCollection(value is null
                    ? new Dictionary<string, string?>()
                    : new Dictionary<string, string?>
                    {
                        [NodeTypeBakeGateExtensions.PackageFailuresRefuseReadinessConfigKey] = value,
                    })
                .Build();
            return new ServiceCollection()
                .AddSingleton<IConfiguration>(config)
                .AddNodeTypeBakeGate(gatesReadiness: true)
                .BuildServiceProvider()
                .GetRequiredService<NodeTypeBakeGateState>();
        }
    }

    // ——— the Error outcome publication lands ———————————————————————————————————————————————

    [Fact(Timeout = 60000)]
    public void AnErrorStampHeldWhileTheSweepMeasures_IsReleasedOnAdmission_AndRefusedUnderStrict()
    {
        TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
        foreach (var strict in new[] { false, true })
        {
            var gate = new NodeTypeBakeGateState { GatesReadiness = true, PackageFailuresRefuseReadiness = strict };
            var publication = new MeshPublicationGate([gate]);
            var written = 0;
            gate.MarkRunning("enumerating dynamic NodeTypes");

            // The compile of a broken type settles mid-sweep: its Error stamp is HELD.
            publication.Publish("NodeType compile stamp for Edu/CourseCatalog", () =>
                Observable.Defer(() =>
                {
                    written++;
                    return Observable.Return(Unit.Default);
                })).Subscribe();
            written.Should().Be(0, "held while the verdict is still forming");

            gate.MarkOutcome(new PreWarmOutcome("Edu/CourseCatalog", PreWarmStatus.CompileError, "error CS0246"));
            gate.MarkComplete("baked");
            publication.Reconsider();

            if (strict)
            {
                written.Should().Be(0, "strict: the refused pod writes nothing - the incident");
                publication.WithheldCount.Should().Be(1);
            }
            else
            {
                written.Should().Be(1, "the Error outcome reaches the mesh once the pod is admitted");
                publication.ReleasedCount.Should().Be(1);
            }
        }
    }

    // ——— adopt-only: an uncovered type does not hold the roll either ———————————————————————

    [Fact(Timeout = 60000)]
    public void AnUncoveredTypeOnAnAdoptOnlyBoot_IsNamedAndDoesNotHoldTheRoll()
    {
        TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
        var gate = new NodeTypeBakeGateState { GatesReadiness = true, PackageFailuresRefuseReadiness = false };
        gate.MarkRunning("adopting");
        var report = new NodeTypeBakeReport(
            System.Collections.Immutable.ImmutableList.Create(
                new NodeTypeBakeEntry("Crm/Contact", BakeState.Baked),
                new NodeTypeBakeEntry("Publish/Slide", BakeState.FrameworkStale) { ProducedByPlatformBuild = "4.1.100" }),
            "framework-1") { LivePlatformVersion = "4.1.200" };

        var proven = gate.ApplyAdoption(report, TimeSpan.FromSeconds(3));

        proven.Should().BeFalse("nothing was proven for the uncovered type");
        gate.Phase.Should().Be(BakePhase.Complete);
        gate.ReadinessGranted.Should().BeTrue();
        gate.ErrorTypes.Keys.Should().Contain("Publish/Slide");
        gate.Detail.Should().Contain("Publish/Slide");
    }
}
