using System;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using MeshWeaver.Graph.Security;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using Xunit;

namespace MeshWeaver.Hosting.Monolith.Test;

/// <summary>
/// 🚨 A one-shot startup service must hold NOTHING once it has run — because the generic host
/// keeps its <c>IHostedService[]</c> for the whole process lifetime.
///
/// <para><b>The measured leak.</b> <c>MeshHubDisposalLeakTest</c> failed on this PR's first CI
/// run and its ClrMD GC-root analysis named the chain exactly:</para>
/// <code>
///   IHostedService[]
///     -> MeshWeaver.Graph.Security.SelfTypedDeclarationDurableRepair
///       -> MeshWeaver.Messaging.MessageHub  [Address=mesh/...]
/// </code>
/// <para>The repair held its hub in a field, so every mesh it was built for stayed reachable from
/// that array after disposal. This ACCUMULATES: a process that builds many meshes — the test
/// suite, a portal recycling silos — keeps every one of them alive.</para>
///
/// <para>🚨 <b>Why these tests exist rather than relying on the leak test.</b>
/// <c>MeshHubDisposalLeakTest</c> is the outcome check, but its ClrMD snapshot-attach is
/// unavailable on macOS, where it <c>Assert.SkipWhen</c>s as inconclusive (#674). So on every
/// developer machine here the only signal is CI. These pin the MECHANISM — the two fields are
/// released — deterministically and locally, so a future edit that reinstates the retention fails
/// where it is written rather than three hours later on a Linux runner.</para>
/// </summary>
public class SelfTypedDeclarationRepairRetainsNothingTest(ITestOutputHelper output)
    : MonolithMeshTestBase(output)
{
    private static object? Field(SelfTypedDeclarationDurableRepair repair, string name) =>
        typeof(SelfTypedDeclarationDurableRepair)
            .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(repair);

    /// <summary>
    /// The direct edge of the leak chain. Whatever the sweep does afterwards, the instance must
    /// not still be pointing at the hub when <c>StartAsync</c> returns.
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task StartAsync_RELEASES_theHub_soTheHostedServiceArrayCannotRootIt()
    {
        var repair = new SelfTypedDeclarationDurableRepair(Mesh);

        Field(repair, "hub").Should().NotBeNull(
            "the premise of this test is that the constructor holds the hub — if it stopped "
            + "doing so, the assertion below would be proving nothing");

        await repair.StartAsync(CancellationToken.None);

        Field(repair, "hub").Should().BeNull(
            "the host keeps IHostedService[] for the process lifetime, so a hub still referenced "
            + "here outlives the mesh it belongs to — the exact chain ClrMD named");
    }

    /// <summary>
    /// The release is an <c>Interlocked.Exchange</c>, which makes the pass one-shot: a second
    /// start must be a no-op rather than run the sweep again on a hub it no longer has.
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task StartAsync_IsOneShot_andTheSecondCallIsAnInertNoOp()
    {
        var repair = new SelfTypedDeclarationDurableRepair(Mesh);
        await repair.StartAsync(CancellationToken.None);

        var second = await Record.ExceptionAsync(() => repair.StartAsync(CancellationToken.None));

        second.Should().BeNull("a second start has no hub and must simply return");
        Field(repair, "hub").Should().BeNull("and it must not resurrect the field");
    }

    /// <summary>
    /// The other field. A finished one-shot has nothing left to cancel, so it drops its own
    /// handle — otherwise the sweep's closures (the storage adapter, and through it the hub)
    /// stay reachable from <c>IHostedService[]</c> for the rest of the process.
    ///
    /// <para>Waits on the CONDITION, never a sleep: the sweep terminates on its own schedule, and
    /// on a synchronously-completing store it is already released before this line runs.</para>
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task WhenTheSweepTerminates_theSubscriptionHandleIsDroppedToo()
    {
        var repair = new SelfTypedDeclarationDurableRepair(Mesh);
        await repair.StartAsync(CancellationToken.None);

        await Observable.Interval(TimeSpan.FromMilliseconds(50))
            .StartWith(0L)
            .Select(_ => Field(repair, "subscription"))
            .Where(s => s is null)
            .FirstAsync()
            .Timeout(30.Seconds())
            .ToTask();

        Field(repair, "subscription").Should().BeNull(
            "a finished sweep has nothing to cancel, so keeping the handle would keep its "
            + "closures — and the hub behind them — reachable for the process lifetime");
    }

    /// <summary>Shutdown must tolerate a service the host never started — and the release runs on
    /// both paths, so calling it twice has to be safe by construction.</summary>
    [Fact(Timeout = 60_000)]
    public async Task StopAsync_IsSafe_beforeAnyStart_andTwice()
    {
        var repair = new SelfTypedDeclarationDurableRepair(Mesh);

        (await Record.ExceptionAsync(() => repair.StopAsync(CancellationToken.None)))
            .Should().BeNull("a service the host never started has nothing to stop");

        await repair.StartAsync(CancellationToken.None);
        await repair.StopAsync(CancellationToken.None);

        (await Record.ExceptionAsync(() => repair.StopAsync(CancellationToken.None)))
            .Should().BeNull(
                "the sweep completing and the host stopping race by construction, so the release "
                + "path is reached twice in normal operation");
    }
}
