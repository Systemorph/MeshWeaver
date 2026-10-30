using System;
using System.Collections.Immutable;
using System.Linq;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Fixture;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// #7362 — a C# cell whose kernel has not answered within the dispatcher's request budget must SAY
/// so on its Activity ("No answer from the kernel yet", keyed so it renders in the viewer's
/// language) instead of staying Running silently. It is not a verdict: the status stays Running, and
/// a late reply completes the run exactly as an in-time one would.
///
/// <para>Only the CELL's hub (the dispatcher, <c>CodeNodeType</c>) runs on the short budget; the
/// kernel side keeps its default. The cell's script parks on a GATE node the test opens, so "the
/// kernel has not answered" lasts exactly as long as the test needs — the wait is on conditions,
/// never on the clock. The gate is opened in a <c>finally</c>, so a failing assertion cannot strand
/// the parked script.</para>
/// </summary>
public class KernelReplyTimeoutNoticeTest(ITestOutputHelper output) : CSharpCellRunTestBase(output)
{
    /// <summary>The dispatcher's request budget — far below how long the gated script is held.</summary>
    private static readonly TimeSpan ShortBudget = TimeSpan.FromSeconds(2);

    /// <inheritdoc />
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder)
            .ConfigureDefaultNodeHub(c =>
            {
                var address = c.Address.ToString();
                return address.Contains("/cell", StringComparison.Ordinal)
                       && !address.Contains("/_Activity/", StringComparison.Ordinal)
                    ? c.WithRequestTimeout(ShortBudget)
                    : c;
            });

    /// <summary>A cell that waits until the gate node is named "go", then returns 7.</summary>
    private static string WaitsForGate(string gatePath) => $$"""
        using System.Reactive.Linq;
        await Mesh.GetMeshNodeStream("{{gatePath}}")
            .Where(n => n?.Name == "go")
            .Take(1)
            .Await(Ct);
        7
        """;

    private async Task<string> CreateGate(CancellationToken ct)
    {
        var path = $"{TestPartition}/gate{Guid.NewGuid():N}";
        var mesh = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
        var access = Mesh.ServiceProvider.GetRequiredService<AccessService>();
        await access
            .RunAsSystem(() => mesh.CreateNode(MeshNode.FromPath(path) with
            {
                Name = "wait",
                NodeType = CodeNodeType.NodeType,
                State = MeshNodeState.Active,
                Content = new CodeConfiguration { Code = "// gate", IsExecutable = false, Language = "csharp" },
            }))
            .FirstAsync()
            .Timeout(Bound)
            .Await(ct);
        return path;
    }

    private async Task OpenGate(string gatePath, CancellationToken ct)
    {
        var access = Mesh.ServiceProvider.GetRequiredService<AccessService>();
        await access
            .RunAsSystem(() => Mesh.GetWorkspace().GetMeshNodeStream(gatePath)
                .Update(n => n with { Name = "go" }))
            .FirstAsync()
            .Timeout(Bound)
            .Await(ct);
    }

    /// <summary>The Activity once it is still Running AND carries the no-answer-yet notice.</summary>
    private async Task<ActivityLog> AwaitingNotice(string activityPath, CancellationToken ct)
    {
        var options = Mesh.JsonSerializerOptions;
        return (await Mesh.GetWorkspace().GetMeshNodeStream(activityPath)
            .Where(node => node is not null)
            .Select(node => node.ContentAs<ActivityLog>(options))
            .Where(log => log is not null
                          && log.Messages.Any(m => m.MessageKey == CodeNodeType.NoKernelReplyYetKey))
            .FirstAsync()
            .Timeout(Bound)
            .Catch<ActivityLog?, TimeoutException>(ex => Observable.Throw<ActivityLog?>(
                new TimeoutException(
                    $"Activity {activityPath} never said that its kernel has not answered yet "
                    + $"(dispatcher budget {ShortBudget}, waited {Bound})", ex)))
            .Await(ct))!;
    }

    /// <summary>
    /// The kernel has not answered within the budget: the Activity shows the keyed
    /// "no answer yet" entry and is still Running — a notice, not a verdict.
    /// </summary>
    [Fact(Timeout = 180_000)]
    public async Task AKernelThatHasNotAnsweredWithinTheBudget_ShowsNoAnswerYet_AndStaysRunning()
    {
        var ct = TestContext.Current.CancellationToken;
        var gate = await CreateGate(ct);
        try
        {
            var cell = await CreateCell(WaitsForGate(gate), ct);
            var activity = await Run(cell, ct);

            var log = await AwaitingNotice(activity, ct);

            log.Status.Should().Be(ActivityStatus.Running,
                "the kernel has not answered YET; the run is still its own and nothing has failed");
            log.Messages.Should().Contain(
                m => m.MessageKey == CodeNodeType.NoKernelReplyYetKey && m.LogLevel == Microsoft.Extensions.Logging.LogLevel.Warning,
                "the reader of the Output pane must see that the run is waiting on the kernel, in their language");
        }
        finally
        {
            await OpenGate(gate, CancellationToken.None);
        }
    }

    /// <summary>
    /// The reply arrives AFTER the budget: the run completes normally (Succeeded, the script's
    /// return value) and the kernel's terminal transcript supersedes the notice.
    /// </summary>
    [Fact(Timeout = 180_000)]
    public async Task ALateReply_CompletesTheRunNormally_AndSupersedesTheNotice()
    {
        var ct = TestContext.Current.CancellationToken;
        var gate = await CreateGate(ct);
        string activity;
        try
        {
            var cell = await CreateCell(WaitsForGate(gate), ct);
            activity = await Run(cell, ct);
            await AwaitingNotice(activity, ct);
        }
        finally
        {
            await OpenGate(gate, CancellationToken.None);
        }

        var log = await Terminal(activity, "the gated cell, released after the dispatcher's budget ran out", ct);

        log.Status.Should().Be(ActivityStatus.Succeeded,
            "a late reply completes the run exactly as an in-time one would");
        log.ReturnValue?.ToString().Should().Be("7", "the script ran to its end");
        log.Messages.Should().NotContain(m => m.MessageKey == CodeNodeType.NoKernelReplyYetKey,
            "the kernel's answer supersedes the notice — a finished run must not still say it is waiting");
    }
}

/// <summary>
/// The negative control for <see cref="KernelReplyTimeoutNoticeTest"/>: a run whose kernel answers
/// within the (default) budget never shows the no-answer-yet notice on ANY snapshot of its Activity —
/// so the notice cannot be satisfied by a write that fires on every run.
/// </summary>
public class KernelReplyInTimeShowsNoNoticeTest(ITestOutputHelper output) : CSharpCellRunTestBase(output)
{
    /// <summary>An in-time reply: every snapshot up to the terminal one is free of the notice.</summary>
    [Fact(Timeout = 180_000)]
    public async Task AnInTimeReply_NeverShowsTheNoAnswerYetNotice()
    {
        var ct = TestContext.Current.CancellationToken;
        var cell = await CreateCell("1 + 1", ct);
        var activity = await Run(cell, ct);
        var options = Mesh.JsonSerializerOptions;

        var snapshots = await Mesh.GetWorkspace().GetMeshNodeStream(activity)
            .Where(node => node is not null)
            .Select(node => node.ContentAs<ActivityLog>(options))
            .Where(log => log is not null)
            .Select(log => log!)
            .TakeUntil(log => log.Status.IsTerminal())
            .Aggregate(ImmutableList<ActivityLog>.Empty, (acc, log) => acc.Add(log))
            .Timeout(Bound)
            .Await(ct);

        snapshots[^1].Status.Should().Be(ActivityStatus.Succeeded, "the real kernel ran `1 + 1` in time");
        snapshots.Should().NotContain(
            log => log.Messages.Any(m => m.MessageKey == CodeNodeType.NoKernelReplyYetKey),
            "the kernel answered within the budget, so no snapshot may say it has not");
    }
}
