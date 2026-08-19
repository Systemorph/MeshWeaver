using System;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Reflection;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Kernel.Hub;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace MeshWeaver.AI.Test;

/// <summary>
/// #1790 — <c>ActivityLogLogger.SealOverflowLocked</c> must not leave <c>system-security</c>
/// latched on the thread that issued the publish.
///
/// <para><b>The defect.</b> The seal wrote its overflow segment inside
/// <c>Observable.Using(() =&gt; access.ImpersonateAsSystem(), _ =&gt; meshService.CreateOrUpdateNode(…))</c>.
/// Impersonation is an <c>AsyncLocal</c> store/restore pair
/// (<c>AccessService.AccessContextScope</c> saves the previous value and writes it back on
/// <c>Dispose</c>). <c>Observable.Using</c> OPENS that scope on the SUBSCRIBING thread and DISPOSES
/// it when the inner observable TERMINATES — for a cross-hub write, the owning hub's response
/// thread. The two halves therefore land on different threads: the subscriber keeps
/// <c>system-security</c> forever, and the terminating thread has the subscriber's captured
/// "previous" identity written into its context.</para>
///
/// <para><b>Why the subscribing thread matters here.</b> <c>SealOverflowLocked</c> is called from
/// <c>PublishSnapshotLocked</c>, and the FIRST publish of a script run is issued from the script's
/// own thread (<c>Console.WriteLine</c> → <c>LoggerTextWriter</c> → <c>PublishSnapshotLocked</c>,
/// inside <c>KernelExecutor.RunOnePass</c>). A latch there runs the rest of the script as System —
/// which is how a <c>--render</c> export came to resolve embedded areas the submitting user may not
/// read (the review finding on #1785, measured against
/// <c>DocumentExportAreaAccessTest</c>). The publish write was fixed there; the seal was split out
/// to #1790 rather than changed without a repro. This is that repro.</para>
///
/// <para><b>Why the assertion is the NEGATIVE one.</b> An identity leak has no failing operation to
/// observe — the seal succeeds either way. The only thing that distinguishes the broken shape from
/// the fixed one is what the CALLER's thread is left holding, so that is what is asserted, at the
/// one moment it is observable: immediately after the publish returns and before the enclosing
/// scope's own <c>Dispose</c> would paper over the latch by restoring the same value.</para>
///
/// <para><b>Non-vacuity.</b> Three guards, because "the identity is fine" also passes when the
/// seal never ran. The tail-flush timer is cancelled up front, so the seal can only be issued
/// from this thread; the counters are asserted UNTOUCHED immediately before the operation, so an
/// early seal cannot silently turn it into a no-op; and the test then waits for those counters to
/// advance, which happens only in the segment write's success callback. Reverting
/// <c>SealOverflowLocked</c> to <c>Observable.Using</c> turns the identity assertion red on the
/// first run.</para>
/// </summary>
[Collection("KernelTests")]
public class ActivityLogSealIdentityTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const int DefaultTimeoutMs = 120000;
    private const string OwnerPath = "rbuergi";
    private const string CallerId = "alice";
    private const string SystemId = "system-security";

    [Fact(Timeout = DefaultTimeoutMs)]
    public async Task SealingTheOverflow_LeavesTheCallersIdentityOnTheCallingThread()
    {
        var client = GetClient();
        var access = client.ServiceProvider.GetRequiredService<AccessService>();
        client.ServiceProvider.GetService<IMeshService>().Should().NotBeNull(
            "the seal IS a mesh write — with no IMeshService resolvable from the logger's hub, "
            + "SealOverflowLocked returns before it impersonates and this test asserts nothing");

        var activityPath = await CreateActivity();
        var logger = new ActivityLogLogger(client, activityPath);
        ILogger asLogger = logger;

        // The first append publishes immediately and stamps the throttle window; the next append
        // to land inside that window arms the 100 ms tail-flush timer. Kill it: a timer-thread
        // flush would seal on the TIMER thread, leaving this thread untouched and the assertion
        // below trivially true. A disposed SerialDisposable also disposes every later assignment,
        // so no tail flush can be armed again for the rest of the test.
        var pendingFlush = PendingFlushOf(logger);
        for (var i = 0; i < 100 && pendingFlush.Disposable is null; i++)
            asLogger.LogInformation("arm {Index}", i);
        pendingFlush.Disposable.Should().NotBeNull(
            "the throttle must have armed a tail flush — if nothing is armed, cancelling it "
            + "proves nothing and a timer could still steal the seal");
        pendingFlush.Dispose();

        // Fill the window past the seal threshold. Every one of these lands inside the throttle
        // window, so none of them publishes and the tail flush that would is cancelled.
        for (var i = 0; i < ActivityLog.MessageWindowLimit + 2; i++)
            asLogger.LogInformation("line {Index}", i);

        // Nothing may have sealed yet: SealOverflowLocked is a no-op while a seal is in flight, so
        // an early seal (one of the appends above crossing the 100 ms throttle window on a heavily
        // loaded machine) would make the operation under test do nothing and the assertion below
        // vacuously true. Read synchronously, before the operation — a loud failure, never a
        // silent pass.
        SealedCountOf(logger).Should().Be(0, "no seal may have completed before the operation under test");
        SealInFlightOf(logger).Should().BeFalse("no seal may have STARTED before the operation under test");

        // ── The operation under test ────────────────────────────────────────────────────────
        // Complete bypasses the throttle and publishes SYNCHRONOUSLY on this thread — the same
        // shape as the script thread's own first publish. A real caller is ambient throughout.
        using (access.SwitchAccessContext(new AccessContext { ObjectId = CallerId, Name = CallerId }))
        {
            logger.Complete(ActivityStatus.Succeeded);

            access.Context?.ObjectId.Should().Be(CallerId,
                "the seal's ImpersonateAsSystem scope must be closed by the call that opened it. "
                + $"Observing '{SystemId}' here means the scope was opened on this thread and left "
                + "to be disposed by the write's terminating thread — so every later statement on "
                + "this thread, i.e. the rest of the script run, executes as System (#1790)");
        }

        // Non-vacuity: the counters only advance in the segment write's success callback, so this
        // proves SealOverflowLocked really ran — and, since the only other publisher was cancelled
        // above, that it ran on the thread the assertion was made on.
        var sealedCount = await Observable.Interval(TimeSpan.FromMilliseconds(50))
            .StartWith(0L)
            .Select(_ => SealedCountOf(logger))
            .Where(count => count > 0)
            .FirstAsync()
            .Timeout(TimeSpan.FromSeconds(60))
            .ToTask(TestContext.Current.CancellationToken);
        Output.WriteLine($"sealed {sealedCount} messages into the overflow segment");
    }

    /// <summary>
    /// Materialises the Activity MeshNode the logger publishes to — same shape as
    /// <c>ActivityTerminalSettleTest.CreateKernelSession</c>.
    /// </summary>
    private async Task<string> CreateActivity()
    {
        var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
        var activityNamespace = $"{OwnerPath}/_Activity";
        var activityId = $"seal-{Guid.NewGuid():N}";
        await meshService.CreateNode(new MeshNode(activityId, activityNamespace)
        {
            Name = "Seal-identity activity",
            NodeType = "Activity",
            MainNode = OwnerPath,
            State = MeshNodeState.Active,
            Content = new ActivityLog("ScriptExecution") { Status = ActivityStatus.Running }
        }).Should().Within(30.Seconds()).Emit();
        return $"{activityNamespace}/{activityId}";
    }

    /// <summary>
    /// The logger's tail-flush handle. Private by design — the null guard turns a rename into a
    /// loud failure rather than a silently vacuous test.
    /// </summary>
    private static SerialDisposable PendingFlushOf(ActivityLogLogger logger)
        => (SerialDisposable)FieldOf(logger, "pendingFlush");

    /// <summary>How many messages the logger has sealed away — advanced only by a successful
    /// segment write.</summary>
    private static int SealedCountOf(ActivityLogLogger logger)
        => (int)FieldOf(logger, "_sealedCount");

    /// <summary>Whether a segment write is outstanding — set as the seal subscribes, cleared by
    /// whichever of its callbacks runs.</summary>
    private static bool SealInFlightOf(ActivityLogLogger logger)
        => (bool)FieldOf(logger, "_sealInFlight");

    private static object FieldOf(ActivityLogLogger logger, string name)
    {
        var field = typeof(ActivityLogLogger)
            .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
        field.Should().NotBeNull(
            $"ActivityLogLogger.{name} is what this test reads — if it was renamed or retyped, "
            + "update the test rather than losing the coverage");
        return field!.GetValue(logger)!;
    }
}
