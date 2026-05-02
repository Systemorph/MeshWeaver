#pragma warning disable CS1591

using System;
using System.IO;
using System.Linq;
using System.Reactive.Linq;
using System.Threading.Tasks;
using System.Reactive.Threading.Tasks;
using FluentAssertions;
using MeshWeaver.Data;
using MeshWeaver.Graph;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Layout;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace MeshWeaver.AI.Test;

/// <summary>
/// End-to-end coverage of a script's <c>Log</c> global propagating through the
/// ActivityLog stream. A Code node with <c>IsExecutable=true</c> containing
/// <c>Log.LogInformation("...")</c> is dispatched via <see cref="ExecuteScriptRequest"/>;
/// we subscribe to the response's ActivityLog path and assert the messages
/// arrive. Replaces the earlier <c>IProgress&lt;string&gt; Progress</c>-based
/// harness. Runs in Monolith to avoid Orleans kernel-activation noise; the
/// Orleans variant is a sibling skip-placeholder until that infrastructure
/// issue is resolved.
/// </summary>
public class ActivityLogStreamTest : MonolithMeshTestBase
{
    public ActivityLogStreamTest(ITestOutputHelper output) : base(output) { }

    // Use the standard MonolithMeshTestBase.ConfigureMesh so DevLogin (rbuergi)
    // + AddGraph + AddSampleUsers are wired the same way as MonolithKernelTest.
    // Tests below put their Code nodes at `rbuergi/<id>`; activities land at
    // `rbuergi/_Activity/<guid>` (top-level, per CodeNodeType's default).
    private const string ScriptsPartition = "rbuergi";

    /// <summary>
    /// Client config that adds layout / data so subscribers can use
    /// <c>workspace.GetRemoteStream(...)</c>. Without <see cref="Layout.LayoutExtensions.AddLayoutClient"/>,
    /// <c>GetWorkspace()</c> throws "AddData was not called" — and posting from
    /// the mesh hub directly leaves <c>SubscribeRequest</c> without a return route.
    /// </summary>
    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration)
        => base.ConfigureClient(configuration).AddLayoutClient();

    [Fact(Timeout = 60_000)]
    public async Task Script_Log_Messages_Land_On_ActivityLog_Node()
    {
        // Seed a Code node with a script that logs two lines.
        var id = $"logrun-{Guid.NewGuid():N}";
        var path = $"{ScriptsPartition}/{id}";
        var mesh = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
        await mesh.CreateNode(new MeshNode(id, ScriptsPartition)
        {
            Name = "Log test",
            NodeType = "Code",
            Content = new CodeConfiguration
            {
                Code = """
                       Log.LogInformation("step-one");
                       Log.LogInformation("step-two");
                       """,
                IsExecutable = true
            }
        });

        // Dispatch via ExecuteScriptRequest; capture the ActivityLog path from the
        // response. That path points at the activity node the Code hub just created.
        var execResponse = await AwaitResponseAsync(
            new ExecuteScriptRequest(),
            o => o.WithTarget(new Address(path)));
        var exec = execResponse.Message;
        exec.Success.Should().BeTrue(exec.Error ?? "exec failed");
        exec.ActivityLog.Should().NotBeNullOrEmpty(
            "ExecuteScript must return the ActivityLog path for subscribers");

        // Subscribe to the activity log's MeshNodeReference and wait until both
        // messages are present. Give the kernel up to 30 s to compile + run.
        var workspace = GetClient().GetWorkspace();
        var observed = await workspace
            .GetRemoteStream<MeshNode, MeshNodeReference>(
                new Address(exec.ActivityLog!), new MeshNodeReference())
            .Select(change => change.Value?.Content as ActivityLog)
            .Where(log => log is not null && log.Messages.Count >= 2)
            .Take(1)
            .Timeout(TimeSpan.FromSeconds(30))
            .FirstAsync();

        observed.Messages.Select(m => m.Message)
            .Should().Contain(m => m.Contains("step-one"))
            .And.Contain(m => m.Contains("step-two"));
    }

    /// <summary>
    /// Polling progress: a script writes 4 log lines spaced ~150 ms apart. Subscribers
    /// of the ActivityLog stream must see the message count grow GRADUALLY — not just
    /// the final 4-message snapshot. Proves the Activity-hosted kernel publishes
    /// intermediate snapshots via <c>DataChangeRequest.Update</c> as the script runs,
    /// instead of buffering everything until the script returns.
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task Progress_Messages_Stream_Gradually_Not_Just_At_The_End()
    {
        var id = $"progress-{Guid.NewGuid():N}";
        var path = $"{ScriptsPartition}/{id}";
        var mesh = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
        await mesh.CreateNode(new MeshNode(id, ScriptsPartition)
        {
            Name = "Progress test",
            NodeType = "Code",
            Content = new CodeConfiguration
            {
                Code = """
                       Log.LogInformation("step-1");
                       System.Threading.Thread.Sleep(200);
                       Log.LogInformation("step-2");
                       System.Threading.Thread.Sleep(200);
                       Log.LogInformation("step-3");
                       System.Threading.Thread.Sleep(200);
                       Log.LogInformation("step-4");
                       """,
                IsExecutable = true
            }
        });

        var execResponse = await AwaitResponseAsync(
            new ExecuteScriptRequest(),
            o => o.WithTarget(new Address(path)));
        var exec = execResponse.Message;
        exec.Success.Should().BeTrue(exec.Error ?? "exec failed");
        exec.ActivityLog.Should().NotBeNullOrEmpty();

        // Collect every distinct snapshot we observe up to and including the
        // 4-message terminal state. Each snapshot is the full ActivityLog at
        // that moment; the count of messages grows monotonically.
        var workspace = GetClient().GetWorkspace();
        // Stream every distinct message-count. Close as soon as we observe the
        // terminal snapshot (4 messages) by using TakeUntil — and re-include
        // that final emission via the wrapping Concat.
        var counts = workspace
            .GetRemoteStream<MeshNode, MeshNodeReference>(
                new Address(exec.ActivityLog!), new MeshNodeReference())
            .Select(change => change.Value?.Content as ActivityLog)
            .Where(log => log is not null)
            .Select(log => log!.Messages.Count)
            .DistinctUntilChanged();

        var snapshots = await counts
            .Where(c => c <= 4)
            .TakeUntil(c => c >= 4)
            .Timeout(TimeSpan.FromSeconds(30))
            .ToList()
            .ToTask();

        // Gradual streaming → at least 3 distinct snapshots before we hit 4.
        // (We allow batching of two adjacent log calls but not all-at-once.)
        snapshots.Should().HaveCountGreaterThanOrEqualTo(3,
            "ActivityLog should publish intermediate snapshots as messages land — " +
            "not buffer everything until script completion. Snapshots seen: [" +
            string.Join(", ", snapshots) + "]");
        snapshots.Last().Should().Be(4, "terminal snapshot must contain all 4 log lines");
    }

    [Fact(Timeout = 60_000)]
    public async Task Script_Failure_Flips_ActivityLog_Status_To_Failed()
    {
        var id = $"logfail-{Guid.NewGuid():N}";
        var path = $"{ScriptsPartition}/{id}";
        var mesh = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
        await mesh.CreateNode(new MeshNode(id, ScriptsPartition)
        {
            Name = "Failing script",
            NodeType = "Code",
            Content = new CodeConfiguration
            {
                Code = """
                       Log.LogInformation("before-throw");
                       throw new System.InvalidOperationException("boom");
                       """,
                IsExecutable = true
            }
        });

        var execResponse = await AwaitResponseAsync(
            new ExecuteScriptRequest(),
            o => o.WithTarget(new Address(path)));
        var exec = execResponse.Message;
        exec.ActivityLog.Should().NotBeNullOrEmpty();

        // Stream the log until Status flips out of Running. Before-throw must be
        // present even though the script raised — Log is best-effort and survives.
        var workspace = GetClient().GetWorkspace();
        var observed = await workspace
            .GetRemoteStream<MeshNode, MeshNodeReference>(
                new Address(exec.ActivityLog!), new MeshNodeReference())
            .Select(change => change.Value?.Content as ActivityLog)
            .Where(log => log is not null && log.Status != ActivityStatus.Running)
            .Take(1)
            .Timeout(TimeSpan.FromSeconds(30))
            .FirstAsync();

        observed.Status.Should().Be(ActivityStatus.Failed);
        observed.Messages.Select(m => m.Message)
            .Should().Contain(m => m.Contains("before-throw"));
    }
}
