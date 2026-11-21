using System;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Xunit;

namespace MeshWeaver.Hosting.Test;

/// <summary>
/// MeshWeaver#8440 (review): the boot-seeding hold the untracked-module gate waits on is taken when
/// the warm-up service is CONSTRUCTED, not when its warm-up starts at <c>ApplicationStarted</c>. The
/// host resolves every hosted service before it starts any, so the hold precedes the silo, the HTTP
/// server and every activation they admit — the window in which the gate could judge a type the pass
/// is about to adopt is closed from the first moment anything can flip a type Pending. And the hold
/// must end: when the seeding stages are done, and on a stop before the warm-up ever ran.
/// </summary>
public class TheWarmUpHoldsTheBootSeedingFromConstructionTest(ITestOutputHelper output)
    : MonolithMeshTestBase(output)
{
    private NodeTypeAdoptionRegistry Adoptions =>
        Mesh.ServiceProvider.GetRequiredService<NodeTypeAdoptionRegistry>();

    private DynamicTypePreWarmerHostedService NewService(IHostApplicationLifetime lifetime)
        => new(Mesh.ServiceProvider, lifetime,
            Mesh.ServiceProvider.GetRequiredService<ILogger<DynamicTypePreWarmerHostedService>>());

    [Fact]
    public async Task TheHold_StandsFromConstructionUntilTheSeedingStagesEnd()
    {
        using var lifetime = new ManualLifetime();
        using var service = NewService(lifetime);
        Adoptions.IsBootSeeding.Should().BeTrue(
            "the hold is taken before StartAsync and before ApplicationStarted — before anything can flip a type Pending");

        await service.StartAsync(TestContext.Current.CancellationToken);
        Adoptions.IsBootSeeding.Should().BeTrue("StartAsync only registers the kick; nothing has been offered yet");

        lifetime.Start();
        await Adoptions.WhenBootSeedingClear()
            .Timeout(TestTimeouts.CrossSilo)
            .ObserveCompletion(
                ex => FileOutput.WriteLine($"late fault on the boot-seeding release: {ex}"),
                TestContext.Current.CancellationToken);
        Adoptions.IsBootSeeding.Should().BeFalse("the seeding stages ran to their end and released the hold");
    }

    [Fact]
    public void AStopBeforeTheWarmUpRan_ReleasesTheHold()
    {
        using var lifetime = new ManualLifetime();
        var service = NewService(lifetime);
        Adoptions.IsBootSeeding.Should().BeTrue();
        service.Dispose();
        service.Dispose();
        Adoptions.IsBootSeeding.Should().BeFalse(
            "a host that stops before ApplicationStarted must not leave every gated type waiting out the budget");
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
