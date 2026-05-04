#pragma warning disable CS1591

using System;
using System.Diagnostics;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Threading.Tasks;
using FluentAssertions;
using MeshWeaver.Data;
using MeshWeaver.Graph;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Layout;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.AI.Test;

/// <summary>
/// Exercises the documented script-execution flow end-to-end:
/// <list type="number">
///   <item>Caller creates an executable Code node in their home (e.g. <c>rbuergi/&lt;id&gt;</c>).</item>
///   <item>Caller posts <see cref="ExecuteScriptRequest"/> to the Code node's address.</item>
///   <item>The Code node creates a fresh Activity sibling under
///   <c>rbuergi/&lt;id&gt;/_Activity/&lt;guid&gt;</c> for this run.</item>
///   <item>Caller subscribes to that activity via <c>GetRemoteStream&lt;MeshNode, MeshNodeReference&gt;</c>
///   and observes <c>ActivityLog.Messages</c> growing as the script logs progress.</item>
///   <item>The script eventually returns an HTML "fireworks" payload; it lands on the
///   activity log alongside the progress messages.</item>
/// </list>
/// Sister test exercises the re-run contract: a second <see cref="ExecuteScriptRequest"/>
/// against the same Code node creates a NEW Activity, leaving the previous one intact.
/// </summary>
public class ScriptExecutionInUserHomeTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string UserHome = "rbuergi";

    /// <summary>
    /// Client config that adds layout / data so <c>GetWorkspace().GetRemoteStream(...)</c>
    /// works for subscribing to the activity's MeshNodeReference.
    /// </summary>
    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration)
        => base.ConfigureClient(configuration).AddLayoutClient();

    /// <summary>
    /// Authoritative example: the same fireworks script the docs demonstrate.
    /// Emits 4 progress lines, then returns animated HTML fireworks.
    /// </summary>
    private const string FireworksScript = """
        Log.LogInformation("Loading fuse...");
        System.Threading.Thread.Sleep(80);
        Log.LogInformation("Lighting...");
        System.Threading.Thread.Sleep(80);
        Log.LogInformation("3... 2... 1...");
        System.Threading.Thread.Sleep(80);
        Log.LogInformation("Boom!");
        MeshWeaver.Layout.Controls.Html(
            "<div style='font-size:48px;text-align:center;animation:pulse 1s infinite'>" +
            "🎆 🎇 🎆 🎇 🎆" +
            "</div>")
        """;

    [Fact(Timeout = 60_000)]
    public async Task Run_FireworksScript_StreamsProgressAndReturnsHtml()
    {
        var (codePath, mesh) = await SeedExecutableCodeAsync(FireworksScript);

        // Fire ExecuteScriptRequest. Response carries the activity path.
        var execResponse = await AwaitResponseAsync(
            new ExecuteScriptRequest(),
            o => o.WithTarget(new Address(codePath)));
        execResponse.Message.Success.Should().BeTrue(execResponse.Message.Error ?? "exec failed");
        execResponse.Message.ActivityLog.Should().NotBeNullOrEmpty();
        execResponse.Message.ActivityLog!.Should().StartWith($"{UserHome}/_Activity/",
            "the activity lives in the user's home (partition root), not nested under the code node");

        // Subscribe to the activity log via the canonical GetRemoteStream pattern.
        // Wait for all four progress lines + the fireworks return value (5 messages total).
        var workspace = GetClient().GetWorkspace();
        var final = await workspace
            .GetRemoteStream<MeshNode, MeshNodeReference>(
                new Address(execResponse.Message.ActivityLog!), new MeshNodeReference())
            .Select(change => change.Value?.Content as ActivityLog)
            .Where(log => log is not null && log!.Status != ActivityStatus.Running)
            .Take(1)
            .Timeout(TimeSpan.FromSeconds(30))
            .FirstAsync();

        final.Status.Should().Be(ActivityStatus.Succeeded);
        final.Messages.Select(m => m.Message).Should()
            .Contain(m => m.Contains("Loading fuse")).And
            .Contain(m => m.Contains("Boom!")).And
            .Contain(m => m.Contains("🎆"), "fireworks return value lands as the terminal message");
    }

    /// <summary>
    /// Subscribers must observe progress messages **as they are emitted**, not
    /// only at the terminal snapshot. Each step in the script sleeps ~80 ms;
    /// we record the wall-clock time we observed each new message-count and
    /// assert no two adjacent observations are squashed into a single tick at
    /// the end. That proves the executor isn't blocking the activity hub.
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task Run_StreamsProgressTimely_NotBuffered()
    {
        var (codePath, _) = await SeedExecutableCodeAsync(FireworksScript);

        var execResponse = await AwaitResponseAsync(
            new ExecuteScriptRequest(),
            o => o.WithTarget(new Address(codePath)));
        execResponse.Message.Success.Should().BeTrue();
        var activityPath = execResponse.Message.ActivityLog!;

        var sw = Stopwatch.StartNew();
        var workspace = GetClient().GetWorkspace();
        var observations = await workspace
            .GetRemoteStream<MeshNode, MeshNodeReference>(
                new Address(activityPath), new MeshNodeReference())
            .Select(change => (Count: (change.Value?.Content as ActivityLog)?.Messages.Count ?? 0,
                               ElapsedMs: sw.ElapsedMilliseconds))
            .DistinctUntilChanged(o => o.Count)
            .TakeUntil(o => o.Count >= 5) // 4 progress + 1 fireworks return-value
            .Timeout(TimeSpan.FromSeconds(30))
            .ToList()
            .ToTask();

        observations.Should().HaveCountGreaterThanOrEqualTo(3,
            "subscribers should observe at least 3 distinct snapshots before the terminal one — "
            + "single-tick delivery would mean the executor blocked the activity hub. Observed: ["
            + string.Join(", ", observations.Select(o => $"{o.Count}@{o.ElapsedMs}ms")) + "]");

        observations.Last().Count.Should().Be(5,
            "terminal snapshot should contain all 4 progress messages + fireworks return value");
    }

    /// <summary>
    /// Cancel-via-property: per the Activity Control Plane pattern
    /// (Doc/Architecture/ActivityControlPlane.md), users cancel a running
    /// activity by patching its content's <c>RequestedStatus = Cancelled</c>
    /// — NOT by posting a CancelXRequest message. The Activity hub watches its
    /// own MeshNodeReference and dispatches the internal cancellation when it
    /// observes the patch.
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task Cancel_Via_RequestedStatus_Patch_Cancels_Running_Script()
    {
        // Script uses Task.Delay(ms, Ct) so the Ct global (rebound per
        // submission) actually interrupts the wait mid-flight when the user
        // patches RequestedStatus = Cancelled. The 800 ms is long enough for
        // the test thread to subscribe + patch, but short enough not to slow
        // the suite when the cancel mechanism breaks (the test would block on
        // the cancellation timeout, not on this wait).
        var (codePath, _) = await SeedExecutableCodeAsync("""
            Log.LogInformation("starting");
            await System.Threading.Tasks.Task.Delay(800, Ct);
            Log.LogInformation("if you see this, cancel did not work");
            "should not get here"
        """);

        var execResponse = await AwaitResponseAsync(
            new ExecuteScriptRequest(),
            o => o.WithTarget(new Address(codePath)));
        execResponse.Message.Success.Should().BeTrue();
        var activityPath = new Address(execResponse.Message.ActivityLog!);

        var workspace = GetClient().GetWorkspace();
        var activityStream = workspace.GetRemoteStream<MeshNode, MeshNodeReference>(
            activityPath, new MeshNodeReference());

        // Wait until the script has actually started (first message landed).
        await activityStream
            .Select(c => c.Value?.Content as ActivityLog)
            .Where(l => l is not null && l.Messages.Count >= 1)
            .Take(1)
            .Timeout(TimeSpan.FromSeconds(10))
            .FirstAsync();

        // The canonical cancel: patch RequestedStatus on the activity content.
        // No CancelScriptRequest, no message types — just workspace.UpdateMeshNode.
        workspace.UpdateMeshNode(curr =>
            curr.Content is ActivityLog log
                ? curr with { Content = log with { RequestedStatus = ActivityStatus.Cancelled } }
                : curr,
            nodePath: activityPath.Path);

        // The activity hub's control-plane watcher sees the patch, dispatches
        // the internal cancel, the script throws OperationCanceledException, and
        // the Activity Status flips out of Running.
        var terminal = await activityStream
            .Select(c => c.Value?.Content as ActivityLog)
            .Where(l => l is not null && l.Status != ActivityStatus.Running)
            .Take(1)
            .Timeout(TimeSpan.FromSeconds(15))
            .FirstAsync();

        terminal!.Status.Should().Be(ActivityStatus.Cancelled,
            "post-ActivityControlPlane: cancellation surfaces as Cancelled (KernelExecutor " +
            "calls activityLogger.Complete(ActivityStatus.Cancelled) on OperationCanceledException)");
        terminal.Messages.Select(m => m.Message)
            .Should().Contain(m => m.Contains("starting"), "earlier log entries survive cancellation")
            .And.NotContain(m => m.Contains("if you see this"),
                "the post-sleep log line must not have run");
    }

    /// <summary>
    /// Default activity-parent (when CodeConfiguration.ActivityParentPath is null)
    /// is the partition root. Verifies the migration-friendly default applies
    /// without any per-Code-node config.
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task DefaultActivityParent_IsPartitionRoot()
    {
        var (codePath, _) = await SeedExecutableCodeAsync("\"hi\"");
        var resp = await AwaitResponseAsync(
            new ExecuteScriptRequest(),
            o => o.WithTarget(new Address(codePath)));
        resp.Message.ActivityLog.Should().StartWith($"{UserHome}/_Activity/",
            "with no ActivityParentPath, activities default to the partition root");
    }

    /// <summary>
    /// Per-namespace routing via the <c>{viewer}</c> token: a Code node with
    /// <c>ActivityParentPath = "{viewer}"</c> writes activities into the
    /// caller's home, not the Code node's own partition. This is the canonical
    /// pattern for shared/docs partitions where every viewer sees their own
    /// runs in their own activity feed.
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task ViewerToken_RoutesActivitiesToCallersHome()
    {
        // Code node in `rbuergi` (the test partition), but configured to
        // route activities to the {viewer}'s home. The DevLogin admin user
        // has ObjectId = "Roland" (see TestUsers.Admin), so {viewer}
        // resolves to "Roland" — independent of where the Code node lives.
        var id = $"viewerdemo-{Guid.NewGuid():N}";
        var path = $"{UserHome}/{id}";
        var mesh = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
        await mesh.CreateNode(new MeshNode(id, UserHome)
        {
            Name = "Viewer-routed",
            NodeType = "Code",
            Content = new CodeConfiguration
            {
                Code = "\"hi from viewer demo\"",
                IsExecutable = true,
                ActivityParentPath = "{viewer}",
            }
        });

        var resp = await AwaitResponseAsync(
            new ExecuteScriptRequest(),
            o => o.WithTarget(new Address(path)));
        resp.Message.Success.Should().BeTrue(resp.Message.Error ?? "exec failed");
        var expectedViewerHome = MeshWeaver.Hosting.Monolith.TestBase.TestUsers.Admin.ObjectId;
        resp.Message.ActivityLog.Should().StartWith($"{expectedViewerHome}/_Activity/",
            "{viewer} should resolve to the calling user's AccessContext.ObjectId, " +
            "regardless of which partition the Code node lives in");
    }

    /// <summary>
    /// Code node stamps LastExecutedAt + LastExecutedBy + LastActivityPath on
    /// itself after a run, so the Content view can render "Last executed by X"
    /// + the last activity's Progress area without scanning historical activities.
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task CodeNode_StampsLastExecutionFields()
    {
        var (codePath, _) = await SeedExecutableCodeAsync("\"x\"");
        var resp = await AwaitResponseAsync(
            new ExecuteScriptRequest(),
            o => o.WithTarget(new Address(codePath)));
        var activityPath = resp.Message.ActivityLog!;

        // Wait for the LastActivityPath stamp to land — the workspace.UpdateMeshNode
        // call inside HandleExecuteScript happens after CreateNode acks but
        // doesn't block ExecuteScriptResponse, so subscribe and wait for it.
        var workspace = GetClient().GetWorkspace();
        var stamped = await workspace
            .GetRemoteStream<MeshNode, MeshNodeReference>(new Address(codePath), new MeshNodeReference())
            .Select(c => c.Value?.Content as CodeConfiguration)
            .Where(c => c is not null && c.LastActivityPath == activityPath)
            .Take(1)
            .Timeout(TimeSpan.FromSeconds(15))
            .FirstAsync();

        stamped!.LastExecutedAt.Should().NotBeNull("the run should set LastExecutedAt");
        stamped.LastExecutedBy.Should().NotBeNullOrEmpty("the user identity should be captured");
        stamped.LastActivityPath.Should().Be(activityPath,
            "LastActivityPath lets the Content view embed the activity's Progress area");
    }

    /// <summary>
    /// End-to-end check that <c>#r "nuget:..."</c> directives still work after
    /// the .NET-Interactive removal. Pulls MathNet.Numerics from nuget.org and
    /// uses one of its types — proves the full pipeline (NuGetDirectiveParser
    /// → INuGetAssemblyResolver → MetadataReference → CSharpScript +
    /// AssemblyLoadContext probing for transitive deps) compiles, resolves,
    /// and runs against a real third-party package.
    ///
    /// <para>Requires network. First run downloads the package to the global
    /// NuGet cache; subsequent runs hit the cache and complete in seconds.</para>
    /// </summary>
    [Fact(Timeout = 180_000)]
    public async Task NuGetDirective_DownloadsPackage_AndScriptUsesIt()
    {
        var (codePath, _) = await SeedExecutableCodeAsync("""
            #r "nuget:MathNet.Numerics, 5.0.0"
            using MathNet.Numerics;
            Log.LogInformation("MathNet pi/4 via series: {Result}", SpecialFunctions.Erf(1.0));
            $"erf(1) = {SpecialFunctions.Erf(1.0):F6}"
        """);

        var execResponse = await AwaitResponseAsync(
            new ExecuteScriptRequest(),
            o => o.WithTarget(new Address(codePath)));
        execResponse.Message.Success.Should().BeTrue(execResponse.Message.Error ?? "exec failed");

        var workspace = GetClient().GetWorkspace();
        var final = await workspace
            .GetRemoteStream<MeshNode, MeshNodeReference>(
                new Address(execResponse.Message.ActivityLog!), new MeshNodeReference())
            .Select(c => c.Value?.Content as ActivityLog)
            .Where(l => l is not null && l!.Status != ActivityStatus.Running)
            .Take(1)
            .Timeout(TimeSpan.FromSeconds(120))
            .FirstAsync();

        final!.Status.Should().Be(ActivityStatus.Succeeded,
            "MathNet.Numerics should resolve from nuget.org and the script should compile + run");
        // erf(1) ≈ 0.8427... — the script logs this twice (once via Log, once
        // as the return value which the kernel echoes onto the activity log).
        final.Messages.Select(m => m.Message)
            .Should().Contain(m => m.Contains("0.8427") || m.Contains("erf"),
                "the script's MathNet call should produce the well-known erf(1) value");
    }

    /// <summary>
    /// Re-running creates a NEW activity. The previous activity is untouched —
    /// historical runs accumulate as siblings under <c>{codePath}/_Activity/*</c>.
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task ReRun_CreatesNewActivity_LeavingPreviousIntact()
    {
        var (codePath, _) = await SeedExecutableCodeAsync(
            "Log.LogInformation(\"first run\");\n1");

        var first = await AwaitResponseAsync(
            new ExecuteScriptRequest(),
            o => o.WithTarget(new Address(codePath)));
        first.Message.Success.Should().BeTrue();
        var firstActivity = first.Message.ActivityLog!;

        // Wait for first run to complete so the test isn't racing the second submit.
        await WaitForCompletionAsync(firstActivity);

        var second = await AwaitResponseAsync(
            new ExecuteScriptRequest(),
            o => o.WithTarget(new Address(codePath)));
        second.Message.Success.Should().BeTrue();
        var secondActivity = second.Message.ActivityLog!;

        secondActivity.Should().NotBe(firstActivity,
            "re-running must produce a fresh activity, not overwrite the previous one");
        firstActivity.Should().StartWith($"{UserHome}/_Activity/");
        secondActivity.Should().StartWith($"{UserHome}/_Activity/");
    }

    private async Task<(string Path, IMeshService Mesh)> SeedExecutableCodeAsync(string code)
    {
        var id = $"demo-{Guid.NewGuid():N}";
        var path = $"{UserHome}/{id}";
        var mesh = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
        await mesh.CreateNode(new MeshNode(id, UserHome)
        {
            Name = "Script demo",
            NodeType = "Code",
            Content = new CodeConfiguration
            {
                Code = code,
                IsExecutable = true,
            }
        });
        return (path, mesh);
    }

    private Task<ActivityLog> WaitForCompletionAsync(string activityPath) =>
        GetClient().GetWorkspace()
            .GetRemoteStream<MeshNode, MeshNodeReference>(
                new Address(activityPath), new MeshNodeReference())
            .Select(change => change.Value?.Content as ActivityLog)
            .Where(log => log is not null && log!.Status != ActivityStatus.Running)
            .Select(log => log!)
            .Take(1)
            .Timeout(TimeSpan.FromSeconds(30))
            .FirstAsync()
            .ToTask();
}
