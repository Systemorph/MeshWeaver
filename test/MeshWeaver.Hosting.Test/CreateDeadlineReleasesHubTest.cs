using System;
using System.Reactive.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Hosting.Test;

/// <summary>
/// A completed create must not leave its handling mesh hub in Rx's scheduled deadline queue.
/// The long budget keeps the cancelled deadline queued while this test collects; it does not
/// make the create or the assertion wait longer. The pre-fix Timeout fallback retained the
/// whole create-handler closure through that queue, including the disposed hub (#6307).
/// </summary>
public class CreateDeadlineReleasesHubTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private bool selfDisposed;

    /// <inheritdoc />
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder).ConfigureServices(services => services
            .AddSingleton(new MeshOperationOptions { Timeout = TimeSpan.FromMinutes(10) }));

    [Fact]
    public async Task CompletedCreate_DoesNotPinDisposedMeshUntilItsDeadline()
    {
        var weak = await CreateThenDisposeMesh();

        for (var i = 0; i < 3 && weak.IsAlive; i++)
        {
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true);
            GC.WaitForPendingFinalizers();
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true);
        }

        weak.IsAlive.Should().BeFalse(
            "the completed create's cancelled Rx deadline is still queued, but it must not retain "
            + "the handler's disposed mesh hub for the rest of the operation budget");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private async Task<WeakReference> CreateThenDisposeMesh()
    {
        var hub = Mesh;
        var path = $"{TestPartition}/create-deadline-{Guid.NewGuid():N}";
        var node = MeshNode.FromPath(path) with { NodeType = "Markdown", Name = "probe" };
        await hub.ServiceProvider.GetRequiredService<IMeshService>()
            .CreateNode(node).Should().Within(TestTimeouts.Convergence).Emit();

        var weak = new WeakReference(hub);
        hub.Dispose();
        await hub.DisposalCompleted.FirstOrDefaultAsync()
            .Timeout(TestTimeouts.Convergence)
            .Await(TestContext.Current.CancellationToken);

        var provider = ServiceProvider;
        ServiceProvider = null!;
        selfDisposed = true;
        (provider as IDisposable)?.Dispose();
        return weak;
    }

    /// <inheritdoc />
    public override async ValueTask DisposeAsync()
    {
        if (selfDisposed)
        {
            GC.SuppressFinalize(this);
            return;
        }

        await base.DisposeAsync();
    }
}
