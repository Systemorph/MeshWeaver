using System;
using System.Reactive;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using MeshWeaver.Hosting.Monolith.TestBase;
using Xunit;

namespace MeshWeaver.Hosting.Test;

/// <summary>
/// Why <see cref="CopyMoveDeadlineReleasesHubTest"/> (and its create sibling) failed once in a
/// combined local run and passed alone: the tests disposed the mesh, waited for
/// <see cref="Messaging.IMessageHub.DisposalCompleted"/>, and collected IMMEDIATELY — reading the
/// signal as "nothing holds the hub any more".
///
/// <para>The hub signals <c>DisposalCompleted</c> from inside its own ShutDown turn, synchronously
/// (<c>SignalDisposalCompleted</c> → <c>ReplaySubject.OnNext</c>), and goes on running that turn
/// afterwards. Until it returns, the signalling thread's stack holds the hub. A loaded run can
/// pre-empt that thread right after the signal, and a collection in that window finds the hub alive
/// although nothing retains it.</para>
///
/// <para>This pins the window deterministically: an observer subscribed to <c>DisposalCompleted</c>
/// PARKS the signalling thread, the test collects while it is parked, and the hub is alive. Released,
/// the same hub is collected (<see cref="Collected.Of"/>). The retention tests therefore wait for
/// collectability within a bound instead of collecting once.</para>
/// </summary>
public class TheDisposalSignalFiresBeforeTheDisposingTurnLetsGoTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private bool selfDisposed;
    private int release;

    [Fact]
    public async Task WhileTheSignallingTurnIsParked_TheDisposedMeshIsStillReachable()
    {
        WeakReference? weak = null;
        try
        {
            weak = await DisposeMeshAndParkItsSignal();
            Collected.FullCollection();
            Collected.FullCollection();
            weak.IsAlive.Should().BeTrue(
                "DisposalCompleted has fired, but the turn that fired it is still on its thread's stack — "
                + "an immediate collection cannot tell this apart from a retained hub");
        }
        finally
        {
            Volatile.Write(ref release, 1);
        }

        await Collected.Of(weak!).Should().Within(TestTimeouts.Convergence).Emit(
            "once the signalling turn is released nothing holds the disposed mesh");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private async Task<WeakReference> DisposeMeshAndParkItsSignal()
    {
        var hub = Mesh;
        var parked = new AsyncSubject<Unit>();
        // Captures the subject and this test object only — never the hub — so the observer itself
        // cannot be what keeps the hub alive.
        hub.DisposalCompleted.Subscribe(_ => ParkTheSignallingThread(parked));

        var weak = new WeakReference(hub);
        hub.Dispose();
        await parked.Should().Within(TestTimeouts.Convergence).Emit("the hub signals DisposalCompleted");

        var provider = ServiceProvider;
        ServiceProvider = null!;
        selfDisposed = true;
        (provider as IDisposable)?.Dispose();
        return weak;
    }

    private void ParkTheSignallingThread(AsyncSubject<Unit> parked)
    {
        parked.OnNext(Unit.Default);
        parked.OnCompleted();
        SpinWait.SpinUntil(() => Volatile.Read(ref release) == 1, TestTimeouts.Convergence);
    }

    /// <inheritdoc />
    public override async ValueTask DisposeAsync()
    {
        Volatile.Write(ref release, 1);
        if (selfDisposed)
        {
            GC.SuppressFinalize(this);
            return;
        }

        await base.DisposeAsync();
    }
}
