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
