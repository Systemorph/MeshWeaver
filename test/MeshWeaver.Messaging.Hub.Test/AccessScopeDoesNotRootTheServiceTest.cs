using System;
using System.Runtime.CompilerServices;
using System.Threading;
using Xunit;

namespace MeshWeaver.Messaging.Hub.Test;

/// <summary>
/// An access scope's flow MARKER must not root the <see cref="AccessService"/> that opened it.
///
/// <para>The marker is an <c>AsyncLocal</c> value, so every <see cref="ExecutionContext"/> captured
/// while a scope is open carries it: each timer, thread-pool item and Rx scheduled action started
/// under the scope. Such a capture can outlive the mesh by minutes (a pending <c>Timeout</c> on the
/// default scheduler). When the marker was the scope object itself, it held the service, the
/// service held its logger, and through the test logger's output-helper closure the whole mesh: a
/// disposed mesh stayed reachable until the timer fired. Measured in a heap dump of
/// <c>Memex.Portal.Shared.Test</c>: a pending <c>System.Threading.Timer</c>'s
/// <c>ExecutionContext</c> held <c>AccessService+AccessContextScope</c>, which held
/// <c>AccessService</c>, then <c>Logger</c>, <c>XUnitFileLogger</c>, the test class,
/// <c>MeshBuilder</c>, <c>MeshServiceProvider</c> and the mesh's <c>TypeRegistry</c> with its
/// XML documentation cache. 84 such caches were live where about 20 meshes were running, which
/// is the climb that took the host past the 6 GiB watchdog.</para>
///
/// <para>The capture is real, and the test checks that: inside it the opened identity is still
/// read. So a pass means the marker does not root the service, not that nothing was captured.</para>
/// </summary>
public class AccessScopeDoesNotRootTheServiceTest
{
    private static readonly AccessContext Alice = new() { ObjectId = "alice", Name = "Alice" };

    [Fact]
    public void ACapturedExecutionContextDoesNotKeepTheAccessServiceAlive()
    {
        var (service, captured) = OpenAScopeAndCaptureTheFlow();

        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);

        service.IsAlive.Should().BeFalse(
            "the only thing still holding anything of the scope is an ExecutionContext captured "
            + "inside it. That capture may carry the identity and an opaque marker, but not the "
            + "AccessService: the service holds the mesh's logger, and through it the mesh");
        GC.KeepAlive(captured);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (WeakReference Service, ExecutionContext Captured) OpenAScopeAndCaptureTheFlow()
    {
        var access = new AccessService();
        ExecutionContext captured;
        using (access.SwitchAccessContext(Alice))
            captured = ExecutionContext.Capture()!;

        // The capture carries the scope: replayed, it still reads the identity it was opened with.
        string? seen = null;
        ExecutionContext.Run(captured.CreateCopy(), _ => seen = access.Context?.ObjectId, null);
        seen.Should().Be("alice", "the captured flow must carry the scope, or this test proves nothing");

        return (new WeakReference(access), captured);
    }
}
