using System;
using System.Reactive;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Fixture;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Hosting.Test;

/// <summary>
/// 🚨 Issue #8057, the data half of #7548: <b>a node's VALUE must never run its readers on the
/// <c>sync/{id}</c> hub turn that received it.</b>
///
/// <para><b>What production showed.</b> On memex, a pod's shutdown drain
/// (<c>MeshTeardownExtensions.DrainAsync</c>) gave up after its 30 s bound. The only work left in
/// the whole hub tree was <c>sync/Zz9…</c>, a stream the node-stream cache opened, at
/// <c>RunLevel=Starting … openGates=1 … Executing(DataChangedEvent, 5067432ms)</c> with 31 turns
/// queued behind it. #7589 took the TERMINAL off that turn. A value still went out inline: the
/// turn applies the frame (<c>SynchronizationStream.UpdateStream</c> → <c>SetCurrent</c> →
/// <c>Store.OnNext</c>), and the cache relays it through its shared subject to every reader of the
/// path, one after another, on the same thread. One reader whose <c>OnNext</c> did not return held
/// the turn: every later reader of the path was never told, <c>OpenGate</c> (which runs after the
/// fan-out) never ran, and every later frame and the hub's own <c>ShutdownRequest</c> queued behind
/// it.</para>
///
/// <para><b>The test.</b> Two readers of one existing node. When the node's next value arrives, the
/// FIRST reader parks in its <c>OnNext</c>. The park is the subject: a volatile flag polled under a
/// bounded <see cref="SpinWait"/> and released in <c>finally</c>. While it is parked, the second
/// reader must hear that value, and then the value after it, which only a hub that is still turning
/// can deliver. Before the fix the second reader hears neither. A reader that parked on the test's
/// own thread held no hub turn, and the test reports that as inconclusive, never as a pass.</para>
/// </summary>
public class AReaderNeverRunsOnTheSyncHubTurnThatDeliversItsValueTest(ITestOutputHelper output)
    : MonolithMeshTestBase(output)
{
    private AccessService Access => Mesh.ServiceProvider.GetRequiredService<AccessService>();

    private static string NewPath() => $"{TestPartition}/rv{Guid.NewGuid().ToString("N")[..10]}";

    private Task<MeshNode> Create(string path, string name) =>
        Access.RunAsSystem(() => NodeFactory.CreateNode(MeshNode.FromPath(path) with
            {
                NodeType = "Markdown", Name = name, State = MeshNodeState.Active,
            }))
            .Should().Within(TestTimeouts.Convergence).Emit($"'{name}' is created", TestContext.Current.CancellationToken);

    /// <summary>A write of the node's name through the mesh's stream cache. Its outcome is not the
    /// subject; the error arm keeps it observed.</summary>
    private IDisposable Rename(string path, string name) =>
        Access.RunAsSystem(() => Mesh.GetWorkspace().GetMeshNodeStream(path).Update(n => n with { Name = name }))
            .Subscribe(_ => { }, ex => Output.WriteLine($"rename to '{name}' ended: {ex.Message}"));

    /// <summary>A reader of the node through the mesh's stream cache, every name it hears kept.</summary>
    private ReplaySubject<string> Read(string path, out IDisposable subscription)
    {
        var names = new ReplaySubject<string>();
        subscription = Access.RunAsSystem(() => Mesh.GetWorkspace().GetMeshNodeStream(path))
            .Subscribe(node => names.OnNext(node?.Name ?? string.Empty), ex => names.OnError(ex));
        return names;
    }

    private static Task<string> Hears(IObservable<string> names, string name, TimeSpan within, string because)
        => names.Where(n => n == name).Should().Within(within).Emit(because, TestContext.Current.CancellationToken);

    /// <summary>
    /// THE FALSIFIER. On main before the fix the second reader hears nothing once the first one
    /// parks: both values wait on the sync hub turn that is running the first reader.
    /// </summary>
    [Fact(Timeout = 240_000)]
    public async Task AReaderParkedInItsOnNext_DoesNotHoldTheOtherReadersOfTheSameNode()
    {
        var path = NewPath();
        await Create(path, "first");

        var testThread = Environment.CurrentManagedThreadId;
        var parkedReaderEntered = new AsyncSubject<Unit>();
        var parkedOnTestThread = 0;
        var release = 0;
        var parkExpired = 0;
        // Longer than the bounds the second reader is held to below, so an answer that only
        // arrives once the park expires is caught by parkExpired rather than counted as a pass.
        var parkBound = TestTimeouts.Convergence + TestTimeouts.Convergence;

        // Reader 1 subscribes first, so it sits in front of reader 2 in the cache's fan-out.
        using var parkedReader = Access.RunAsSystem(() => Mesh.GetWorkspace().GetMeshNodeStream(path))
            .Subscribe(node =>
            {
                if (node?.Name != "second")
                    return;
                if (Environment.CurrentManagedThreadId == testThread)
                    Volatile.Write(ref parkedOnTestThread, 1);
                parkedReaderEntered.OnNext(Unit.Default);
                parkedReaderEntered.OnCompleted();
                if (!SpinWait.SpinUntil(() => Volatile.Read(ref release) == 1, parkBound))
                    Volatile.Write(ref parkExpired, 1);
            }, _ => { });
        var other = Read(path, out var otherSubscription);
        using var _ = otherSubscription;
        try
        {
            await Hears(other, "first", TestTimeouts.Convergence, "the second reader hears the node as created");

            using var second = Rename(path, "second");
            await parkedReaderEntered.Should().Within(TestTimeouts.Convergence)
                .Emit("the node's next value must reach the first reader", TestContext.Current.CancellationToken);
            Volatile.Read(ref parkedOnTestThread).Should().Be(0,
                "INCONCLUSIVE, not a pass: the reader parked on the test's own thread, so no hub turn was held");

            await Hears(other, "second", TestTimeouts.Convergence,
                "a second reader of the SAME node must hear its value while the first reader is still parked "
                + "in its OnNext (#8057): the value must not be relayed to readers on the sync hub turn that "
                + "received it");

            using var third = Rename(path, "third");
            await Hears(other, "third", TestTimeouts.Convergence,
                "the node's next frame must still be applied: the sync hub keeps turning while a reader is parked");
            Volatile.Read(ref parkExpired).Should().Be(0,
                "the second reader was answered while the first reader still held its OnNext");
        }
        finally
        {
            Volatile.Write(ref release, 1);
        }
    }

    /// <summary>
    /// Negative control: the same two writes and two readers, with a first reader that returns at
    /// once. The second reader hears both, so the falsifier's red is the parked reader and nothing
    /// else in the apparatus.
    /// </summary>
    [Fact(Timeout = 240_000)]
    public async Task WithAReaderThatReturns_TheOtherReaderHearsEveryValue()
    {
        TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
        var path = NewPath();
        await Create(path, "first");

        var first = Read(path, out var firstSubscription);
        using var _ = firstSubscription;
        var other = Read(path, out var otherSubscription);
        using var __ = otherSubscription;
        await Hears(other, "first", TestTimeouts.Convergence, "the second reader hears the node as created");

        using var second = Rename(path, "second");
        await Hears(first, "second", TestTimeouts.Convergence, "the first reader hears the rename");
        await Hears(other, "second", TestTimeouts.Convergence, "the second reader hears the rename");
        using var third = Rename(path, "third");
        await Hears(other, "third", TestTimeouts.Convergence, "the second reader hears the next rename");
    }
}
