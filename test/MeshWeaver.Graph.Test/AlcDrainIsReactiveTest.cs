using System.Reflection;
using MeshWeaver.Graph.Configuration;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// #2488 for the assembly-load-context drain: waiting for disposal is a SUBSCRIPTION, never a race.
///
/// <para>🚨 What this refuses to let back in. <c>Dispose</c> used to spin against a 5 s deadline and
/// then — by its own comment — "unload anyway". That makes the one case where we KNOW a scanner is
/// still live also the case where its assembly is pulled out from under it: a
/// <c>TypeLoadException '…format is invalid'</c> mid <c>GetTypes</c>/attribute resolution, which
/// surfaces far from the cause as a render that never completes or a use-after-unload crash. The
/// fix has no branch to get wrong: the unload runs when the last scan releases, or the context is
/// simply RETAINED. Retaining memory beats corrupting a live assembly, and a drain that never
/// completes is a leak to FIX, not a budget to spend.</para>
/// </summary>
public class AlcDrainIsReactiveTest
{
    private static bool IsUnloaded(NodeAssemblyLoadContext context) =>
        (bool)typeof(NodeAssemblyLoadContext)
            .GetField("_disposed", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(context)!;

    [Fact]
    public void Dispose_WithNoScanInFlight_UnloadsImmediately()
    {
        var context = new NodeAssemblyLoadContext("MeshWeaver.Test.Quiet", dllPath: null);

        context.Dispose();

        Assert.True(IsUnloaded(context),
            "with no scan pinned the drain is already complete, so the unload runs inline — the "
            + "signal replays to the subscriber rather than waiting for a future notification");
    }

    [Fact]
    public void Dispose_WhileAScanIsPinned_WaitsForIt_ThenUnloadsOnRelease()
    {
        var context = new NodeAssemblyLoadContext("MeshWeaver.Test.Pinned", dllPath: null);
        var scan = context.Pin();

        context.Dispose();

        // THE INVARIANT: a scan is in flight, so nothing has been unloaded — and critically, no
        // clock is running that would unload it anyway.
        Assert.False(IsUnloaded(context),
            "an assembly a scan is still reading must NOT be unloaded — this is the corruption the "
            + "old 5 s ceiling caused every time it expired");

        // The scan finishes: the release IS the signal, and the unload happens because of it.
        scan.Dispose();

        Assert.True(IsUnloaded(context),
            "the last pin release announces the drain, and the subscription unloads on it");
    }

    [Fact]
    public void Pin_IsRefusedOnceDisposeHasStarted_SoTheDrainCanOnlyShrink()
    {
        var context = new NodeAssemblyLoadContext("MeshWeaver.Test.Closing", dllPath: null);
        var scan = context.Pin();
        context.Dispose();

        // Without this the drain could never complete: a new scan arriving after Dispose would keep
        // the count above zero indefinitely, turning "retain until quiet" into "retain forever".
        Assert.Throws<ObjectDisposedException>(() => context.Pin());

        scan.Dispose();
        Assert.True(IsUnloaded(context));
    }
}
