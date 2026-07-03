#pragma warning disable CS1591

using System;
using System.Linq;
using System.Reactive.Linq;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Kernel;
using MeshWeaver.Kernel.Hub;
using MeshWeaver.Layout;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace MeshWeaver.AI.Test;

/// <summary>
/// Deterministic repros for the ACTIVITY TERMINAL-SETTLE defect (memex-cloud,
/// 2026-07-02/03): a script activity's terminal <c>Succeeded</c>/<c>Failed</c>
/// write was unreliable, leaving zombie activities stuck at <c>Running</c> forever.
/// Two independent root causes, both pinned here:
/// <list type="number">
///   <item><b>Settle bypass</b> (<c>KernelExecutor</c>): the submission-outcome
///   subscribers ran unguarded. A throw inside the success path — most reachably
///   <c>JsonSerializer.SerializeToElement</c> on a non-serializable script return
///   value — skipped <c>ActivityLogLogger.Complete</c> entirely (activity Running
///   forever) AND propagated back through the <c>AsyncSubject</c> into the serial
///   Concat pump, so the pump never advanced and every later submission on that
///   kernel silently queued forever.</item>
///   <item><b>Terminal clobber</b> (<c>ActivityLogLogger</c>): every non-terminal
///   publish stamped <c>Status = Running, End = null</c> wholesale. A log append
///   arriving AFTER <c>Complete</c> — a late Subscribe-callback logging, a leaked
///   subscription still ticking, or the throttle tail-flush timer racing the
///   terminal write — re-published a Running snapshot on top of the terminal one,
///   reverting the activity to Running permanently (the production zombie shape).</item>
/// </list>
/// </summary>
[Collection("KernelTests")]
public class ActivityTerminalSettleTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    /// <summary>Share Mesh/SP across [Fact]s — see MonolithMeshTestBase.ShareMeshAcrossTests.</summary>
    protected override bool ShareMeshAcrossTests => true;

    private const int DefaultTimeoutMs = 120000;
    private const string OwnerPath = "rbuergi";

    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration)
        => base.ConfigureClient(configuration).AddLayoutClient();

    /// <summary>
    /// Materialises a per-test Activity MeshNode whose hub hosts the kernel
    /// handlers (via <c>ActivityNodeType.HubConfiguration</c>) and returns its
    /// address — same shape as <c>MonolithKernelTest.CreateKernelSession</c>.
    /// </summary>
    private async Task<Address> CreateKernelSession()
    {
        var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
        var kernelId = Guid.NewGuid().ToString("N");
        var activityNamespace = $"{OwnerPath}/_Activity";
        var activityNode = new MeshNode($"settle-{kernelId}", activityNamespace)
        {
            Name = "Terminal-settle kernel session",
            NodeType = "Activity",
            MainNode = OwnerPath,
            State = MeshNodeState.Active,
            Content = new ActivityLog("ScriptExecution") { Status = ActivityStatus.Running }
        };
        await meshService.CreateNode(activityNode).Should().Within(30.Seconds()).Emit();
        return new Address($"{activityNamespace}/settle-{kernelId}");
    }

    private IObservable<ActivityLog> ActivityLogStream(IMessageHub client, string activityPath)
        => client.GetWorkspace()
            .GetMeshNodeStream(activityPath)
            .Select(n => n?.Content as ActivityLog)
            .Where(l => l is not null)
            .Select(l => l!);

    // ── Root cause 1: settle bypass — post-completion settle-path throw ──────

    /// <summary>
    /// The prompt-case-(ii) gap: the script itself SUCCEEDS, but the settle path's
    /// return-value capture (<c>JsonSerializer.SerializeToElement</c>) throws — the
    /// return value's property getter faults during serialization. The activity must
    /// still settle terminally (Failed, with the settle error in the log), and the
    /// REPL pump must advance so the next submission on the same kernel still
    /// executes. Before the fix: <c>Complete</c> was skipped (activity Running
    /// forever) and the thrown exception unwound through the AsyncSubject into the
    /// Concat pump, wedging every later submission on this kernel.
    /// </summary>
    [Fact(Timeout = DefaultTimeoutMs)]
    public async Task NonSerializableReturnValue_SettlesFailed_AndPumpAdvances()
    {
        var client = GetClient();
        var kernelAddress = await CreateKernelSession();
        var logStream = ActivityLogStream(client, kernelAddress.Path);

        const string code = """
            class Boom { public int X => throw new InvalidOperationException("boom-return-getter"); }
            new Boom()
            """;
        client.Post(
            new SubmitCodeRequest(code) { Id = "cell-1" },
            o => o.WithTarget(kernelAddress));

        // (1) Terminal settle is mandatory — never a silent Running zombie.
        var settled = await logStream.Should().Within(30.Seconds()).Match(l =>
            l.Status == ActivityStatus.Failed
            && l.End != null
            && l.Messages.Any(m =>
                m.LogLevel == LogLevel.Error && m.Message.Contains("boom-return-getter")));
        Output.WriteLine($"settled: {settled!.Status}, messages: " +
            string.Join(" | ", settled.Messages.Select(m => m.Message)));

        // (2) The serial Concat pump must advance past the poisoned settle —
        //     a second submission on the SAME kernel still runs.
        client.Post(
            new SubmitCodeRequest("Console.WriteLine(\"second-run-alive\");") { Id = "cell-2" },
            o => o.WithTarget(kernelAddress));
        await logStream.Should().Within(30.Seconds()).Match(l =>
            l.Messages.Any(m => m.Message.Contains("second-run-alive")));
    }

    // ── Root cause 2: terminal clobber — appends after Complete ─────────────

    /// <summary>
    /// Manifestation (b) / prompt case (iii), end-to-end: a SUCCEEDING script
    /// schedules a Subscribe callback that fires ~400ms after the body (and its
    /// terminal Succeeded settle) completed and logs a line. 400ms &gt; the
    /// logger's 100ms throttle window, so before the fix the append took the
    /// immediate-publish branch and re-stamped <c>Status = Running, End = null</c>
    /// wholesale — permanently reverting the terminal status. The late message
    /// must land WITHOUT regressing the settle.
    /// </summary>
    [Fact(Timeout = DefaultTimeoutMs)]
    public async Task LateSubscribeCallbackLog_DoesNotRevertTerminalStatus()
    {
        var client = GetClient();
        var kernelAddress = await CreateKernelSession();
        var logStream = ActivityLogStream(client, kernelAddress.Path);

        const string code = """
            Observable.Timer(TimeSpan.FromMilliseconds(400))
                .Subscribe(_ => Log.LogInformation("late-callback-line"));
            "settle-done"
            """;
        client.Post(new SubmitCodeRequest(code) { Id = "cell-1" },
            o => o.WithTarget(kernelAddress));

        // The run settles Succeeded first…
        await logStream.Should().Within(30.Seconds()).Match(l =>
            l.Status == ActivityStatus.Succeeded && l.End != null);

        // …and the emission that carries the late callback's line must STILL be
        // terminal. Before the fix this emission was Status=Running / End=null.
        var withLateLine = await logStream
            .Where(l => l.Messages.Any(m => m.Message.Contains("late-callback-line")))
            .Should().Within(30.Seconds()).Emit();
        withLateLine.Status.Should().Be(ActivityStatus.Succeeded,
            "a post-completion append must never revert the terminal status");
        withLateLine.End.Should().NotBeNull(
            "the terminal End timestamp must survive post-completion appends");
    }

    /// <summary>
    /// Concurrency-shaped pin for the terminal-write-vs-append race, directly on
    /// <see cref="ActivityLogLogger"/>: hammer appends around <c>Complete</c>,
    /// then keep appending on a &gt;throttle-window cadence (the immediate-publish
    /// branch). Whatever snapshot carries a post-completion message MUST still
    /// carry the terminal status — the terminal write always survives appends.
    /// </summary>
    [Fact(Timeout = DefaultTimeoutMs)]
    public async Task HammeredAppends_NeverRevertTerminalStatus()
    {
        var client = GetClient();
        var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
        var activityId = $"hammer-{Guid.NewGuid():N}";
        var ns = $"{OwnerPath}/_Activity";
        var path = $"{ns}/{activityId}";
        await meshService.CreateNode(new MeshNode(activityId, ns)
        {
            Name = "Hammer activity",
            NodeType = "Activity",
            MainNode = OwnerPath,
            State = MeshNodeState.Active,
            Content = new ActivityLog("ScriptExecution") { Status = ActivityStatus.Running }
        }).Should().Within(30.Seconds()).Emit();

        var logger = new ActivityLogLogger(client, path);
        ILogger asLogger = logger;

        // Burst of appends right up to the completion moment (keeps a throttle
        // tail-flush timer in flight across Complete — the TOCTOU window)…
        for (var i = 0; i < 10; i++)
            asLogger.LogInformation("pre-{Index}", i);

        // …terminal settle mid-stream…
        logger.Complete(ActivityStatus.Succeeded);

        // …then late appends paced ABOVE the 100ms throttle so each one takes the
        // immediate-publish branch (the deterministic clobber path before the fix).
        var lateAppends = Observable.Interval(TimeSpan.FromMilliseconds(150))
            .Take(20)
            .Subscribe(i => asLogger.LogInformation("post-{Index}", i));
        try
        {
            var withLate = await ActivityLogStream(client, path)
                .Where(l => l.Messages.Any(m => m.Message.StartsWith("post-")))
                .Should().Within(30.Seconds()).Emit();
            withLate.Status.Should().Be(ActivityStatus.Succeeded,
                "the terminal status must survive concurrent and late appends");
            withLate.End.Should().NotBeNull();
        }
        finally
        {
            lateAppends.Dispose();
        }
    }

    // ── The production (a) script shape: callback throw → Failed settle ─────

    /// <summary>
    /// The literal memex-cloud 2026-07-02 script shape: a subscription callback
    /// inside the script trips <c>Workspace.GetRemoteStreamAsHub</c>'s
    /// "Owner cannot be the same as the subscriber" guard. The exception faults
    /// the script's Task; the activity MUST settle Failed with the exception
    /// message in the log — and STAY Failed (the production zombies reverted to
    /// Running via root cause 2).
    /// </summary>
    [Fact(Timeout = DefaultTimeoutMs)]
    public async Task SubscribeCallbackThrow_SettlesFailed_WithExceptionMessageInLog()
    {
        var client = GetClient();
        var kernelAddress = await CreateKernelSession();
        var logStream = ActivityLogStream(client, kernelAddress.Path);

        const string code = """
            Observable.Return(1).Subscribe(_ =>
                MeshWeaver.Data.WorkspaceExtensions.GetWorkspace(Mesh)
                    .GetRemoteStreamAsHub(Mesh.Address, new MeshWeaver.Data.CollectionsReference("nodes")));
            "unreachable"
            """;
        client.Post(new SubmitCodeRequest(code) { Id = "cell-1" },
            o => o.WithTarget(kernelAddress));

        await logStream.Should().Within(30.Seconds()).Match(l =>
            l.Status == ActivityStatus.Failed
            && l.End != null
            && l.Messages.Any(m =>
                m.Message.Contains("Owner cannot be the same as the subscriber")));
    }
}
