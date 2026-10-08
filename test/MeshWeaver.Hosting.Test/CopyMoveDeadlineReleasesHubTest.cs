using System;
using System.Reactive.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using MeshWeaver.ServiceProvider;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Hosting.Test;

/// <summary>
/// The copy and move siblings of <see cref="CreateDeadlineReleasesHubTest"/>. A completed copy or
/// move must not leave its handling hub in Rx's scheduled deadline queue: an absolute
/// <c>Timeout(deadline, fallback)</c> parks a <c>LocalScheduler.WorkItem</c> that outlives the
/// subscription until the deadline, and that work item retains the fallback observable. Before
/// this test the copy's and the move's fallbacks were lambdas in the handler's own closure scope
/// (they read the live stage), so the work item retained the closure that owns the hub — the same
/// <c>WorkItem → Timeout.Absolute → Defer → handler closure → hub</c> chain #6308 cut for create.
/// The long budget keeps the cancelled deadline queued while this test collects; it does not make
/// the operation or the assertion wait longer.
/// </summary>
public class CopyMoveDeadlineReleasesHubTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private bool selfDisposed;

    /// <inheritdoc />
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder).ConfigureServices(services => services
            .AddSingleton(new MeshOperationOptions { Timeout = TimeSpan.FromMinutes(10) }));

    [Fact]
    public async Task CompletedCopy_DoesNotPinDisposedMeshUntilItsDeadline()
    {
        var weak = await OperateThenDisposeMesh(move: false);
        Collect(weak);
        weak.IsAlive.Should().BeFalse(
            "the completed copy's cancelled Rx deadline is still queued, but it must not retain "
            + "the handler's disposed mesh hub for the rest of the operation budget");
    }

    [Fact]
    public async Task CompletedMove_DoesNotPinDisposedMeshUntilItsDeadline()
    {
        var weak = await OperateThenDisposeMesh(move: true);
        Collect(weak);
        weak.IsAlive.Should().BeFalse(
            "the completed move's cancelled Rx deadline is still queued, but it must not retain "
            + "the handler's disposed mesh hub for the rest of the operation budget");
    }

    private static void Collect(WeakReference weak)
    {
        for (var i = 0; i < 3 && weak.IsAlive; i++)
        {
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true);
            GC.WaitForPendingFinalizers();
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true);
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private async Task<WeakReference> OperateThenDisposeMesh(bool move)
    {
        var ct = TestContext.Current.CancellationToken;
        var hub = Mesh;
        var meshService = hub.ServiceProvider.GetRequiredService<IMeshService>();
        var source = $"{TestPartition}/deadline-source-{Guid.NewGuid():N}";
        var target = $"{TestPartition}/deadline-target-{Guid.NewGuid():N}";
        await meshService
            .CreateNode(MeshNode.FromPath(source) with { NodeType = "Markdown", Name = "probe" })
            .Should().Within(TestTimeouts.Convergence).Emit(cancellationToken: ct);

        if (move)
        {
            // Issued from a client hub THIS method owns and disposes — not ObserveNodeOperation:
            // the base caches its RequestHub client in a field of the (live) test object, and a
            // client's parent is the mesh, so that cache alone would pin the mesh and make this
            // assertion fail for a reason that is not the product's (measured: the copy, issued
            // the same way, stays alive too).
            var client = hub.ServiceProvider.CreateMessageHub(CreateClientAddress(), ConfigureClient)!;
            var response = await client
                .Observe(new MoveNodeRequest(source, target), o => o.WithTarget(client.NodeOperationTarget()))
                .Select(d => d.Message)
                .Should().Within(TestTimeouts.Convergence).Emit(cancellationToken: ct);
            response.Success.Should().BeTrue($"the move must land before its retention is measured: {response.Error}");
            await client.DisposeAndJoinAsync(message => Output.WriteLine(message), TestTimeouts.Convergence);
        }
        else
        {
            await meshService.CopyNode(source, target)
                .Should().Within(TestTimeouts.Convergence).Emit(cancellationToken: ct);
        }

        var weak = new WeakReference(hub);
        hub.Dispose();
        await hub.DisposalCompleted.FirstOrDefaultAsync()
            .Timeout(TestTimeouts.Convergence)
            .Await(ct);

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
