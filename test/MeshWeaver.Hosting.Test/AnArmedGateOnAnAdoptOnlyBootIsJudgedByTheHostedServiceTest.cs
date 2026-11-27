using System;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Reactive.Assertions;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Xunit;

namespace MeshWeaver.Hosting.Test;

/// <summary>
/// The WIRING half of "bake at build": <see cref="TheGateOnAnAdoptOnlyBootGatesOnAdoptionTest"/> pins the
/// verdict on the gate state; this pins that the hosted service actually delivers it on a gate-on /
/// sweep-off boot (<c>PreWarm:DynamicTypes</c> unset) - an armed gate that used to stay
/// <see cref="BakePhase.NotStarted"/> forever must now leave it for a measured terminal, and an
/// unarmed gate must be left alone.
/// </summary>
public class AnArmedGateOnAnAdoptOnlyBootIsJudgedByTheHostedServiceTest(ITestOutputHelper output)
    : MonolithMeshTestBase(output)
{
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder)
            .ConfigureServices(services => services.AddNodeTypeBakeGate(gatesReadiness: true));

    private NodeTypeBakeGateState Gate => Mesh.ServiceProvider.GetRequiredService<NodeTypeBakeGateState>();

    [Fact(Timeout = 120000)]
    public async Task AnArmedGate_LeavesNotStartedForAMeasuredVerdict_WithoutCompilingAnything()
    {
        using var lifetime = new ManualLifetime();
        using var service = new DynamicTypePreWarmerHostedService(
            Mesh.ServiceProvider, lifetime,
            Mesh.ServiceProvider.GetRequiredService<ILogger<DynamicTypePreWarmerHostedService>>());
        Gate.GatesReadiness.Should().BeTrue();
        Gate.Phase.Should().Be(BakePhase.NotStarted, "nothing has kicked yet");

        await service.StartAsync(TestContext.Current.CancellationToken);
        lifetime.Start();

        var phase = await Observable.Interval(TimeSpan.FromMilliseconds(50))
            .Select(_ => Gate.Phase)
            .FirstAsync(p => p is BakePhase.Complete or BakePhase.Faulted or BakePhase.Regressed)
            .Timeout(TestTimeouts.CrossSilo)
            .ObserveCompletion(
                ex => FileOutput.WriteLine($"late fault waiting for the adoption verdict: {ex}"),
                TestContext.Current.CancellationToken);

        phase.Should().Be(BakePhase.Complete,
            "a test mesh has no dynamic NodeType left uncovered, so the adoption is trivially proven - "
            + "and the gate was measured rather than left at NotStarted (fail-open)");
        Gate.Detail.Should().Contain("adopted the prebuilt bundles").And.Contain("nothing compiled");
        Gate.ReadinessGranted.Should().BeTrue();
    }

    private sealed class ManualLifetime : IHostApplicationLifetime, IDisposable
    {
        private readonly CancellationTokenSource started = new();
        private readonly CancellationTokenSource stopping = new();
        private readonly CancellationTokenSource stopped = new();

        public CancellationToken ApplicationStarted => started.Token;
        public CancellationToken ApplicationStopping => stopping.Token;
        public CancellationToken ApplicationStopped => stopped.Token;

        public void StopApplication() => stopping.Cancel();

        public void Start() => started.Cancel();

        public void Dispose()
        {
            started.Dispose();
            stopping.Dispose();
            stopped.Dispose();
        }
    }
}
