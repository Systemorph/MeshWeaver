using System;
using System.Linq;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Fixture;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Kernel;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Mesh.Threading;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// What every C#-cell test below shares: create an executable Code cell, run it through
/// <c>CodeNodeType</c>'s own dispatch (<see cref="ExecuteScriptRequest"/>), and wait on the run's
/// Activity — read off the authoritative node stream — until it reaches a terminal status.
/// Every wait is on the condition, never the clock.
/// </summary>
public abstract class CSharpCellRunTestBase(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    /// <summary>How long a run may take to reach a terminal status before the test calls it stuck.</summary>
    protected static TimeSpan Bound => TestTimeouts.Convergence;

    /// <summary>
    /// The source of a cell that RUNS the cell at <paramref name="innerPath"/> and waits for its run to
    /// reach a terminal status, returning <c>"inner {Status}"</c>. Its waits go through the sanctioned
    /// <c>.Await(Ct)</c> bridge — never a bare <c>await</c> on an observable, whose Rx awaiter resumes
    /// the script inline on the thread delivering the notification — and <c>Ct</c> (the run's
    /// cancellation) ends them.
    /// </summary>
    protected static string RunsAndWaitsFor(string innerPath) => $$"""
        using System.Reactive.Linq;
        using MeshWeaver.Data;
        var started = await Mesh
            .Observe<ExecuteScriptResponse>(new ExecuteScriptRequest(), o => o.WithTarget(new Address("{{innerPath}}")))
            .Take(1)
            .Await(Ct);
        var settled = await Mesh.GetMeshNodeStream(started.Message.ActivityLog!)
            .Select(n => n?.ContentAs<ActivityLog>(Mesh.JsonSerializerOptions))
            .Where(l => l is not null && l.Status.IsTerminal())
            .Take(1)
            .Await(Ct);
        "inner " + settled!.Status
        """;

    /// <summary>Creates an executable C# cell and returns its path.</summary>
    protected async Task<string> CreateCell(string code, CancellationToken ct)
    {
        var id = $"cell{Guid.NewGuid():N}"[..12];
        var path = $"{TestPartition}/{id}";
        var mesh = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
        var access = Mesh.ServiceProvider.GetRequiredService<AccessService>();
        await access
            .RunAsSystem(() => mesh.CreateNode(MeshNode.FromPath(path) with
            {
                NodeType = CodeNodeType.NodeType,
                Name = id,
                State = MeshNodeState.Active,
                Content = new CodeConfiguration { Code = code, IsExecutable = true, Language = "csharp" },
            }))
            .FirstAsync()
            .Timeout(Bound)
            .Await(ct);
        return path;
    }

    /// <summary>Runs the cell and returns the path of the Activity the run writes to.</summary>
    protected async Task<string> Run(string cellPath, CancellationToken ct)
    {
        var dispatch = await RequestHub
            .Observe<ExecuteScriptResponse>(new ExecuteScriptRequest(), o => o.WithTarget(new Address(cellPath)))
            .FirstAsync()
            .Timeout(Bound)
            .Await(ct);
        dispatch.Message.Success.Should().BeTrue(
            $"the dispatch of '{cellPath}' must be accepted before its run can be asserted on");
        dispatch.Message.ActivityLog.Should().NotBeNullOrEmpty(
            "the dispatcher names the Activity it created — that node is what the cell's Output pane follows");
        return dispatch.Message.ActivityLog!;
    }

    /// <summary>
    /// The run's ActivityLog once it is terminal. 🚨 <c>ContentAs</c>, never <c>is ActivityLog</c>:
    /// content that crossed a hub boundary can be untyped JSON, and a cast would read as "still running".
    /// </summary>
    protected async Task<ActivityLog> Terminal(string activityPath, string because, CancellationToken ct)
    {
        var options = Mesh.JsonSerializerOptions;
        return (await Mesh.GetWorkspace().GetMeshNodeStream(activityPath)
            .Where(node => node is not null)
            .Select(node => node.ContentAs<ActivityLog>(options))
            .Where(log => log is not null && log.Status.IsTerminal())
            .FirstAsync()
            .Timeout(Bound)
            .Catch<ActivityLog?, TimeoutException>(ex => Observable.Throw<ActivityLog?>(
                new TimeoutException($"Activity {activityPath} was still Running after {Bound}: {because}", ex)))
            .Await(ct))!;
    }
}

/// <summary>
/// The silent half of the Education journey stall: a C# cell whose kernel FAULTS — instead of
/// running the script — must leave its Activity <see cref="ActivityStatus.Failed"/> with the reason,
/// never Running for ever.
///
/// <para>The C# branch of <c>CodeNodeType.HandleExecuteScript</c> used to post the
/// <see cref="SubmitCodeRequest"/> fire-and-forget, on the assumption that the Roslyn kernel
/// "always" settles the log. Every failure that happens BEFORE the kernel runs the script — the
/// container's dispatch faulting, a DeliveryFailure, a routing NotFound — therefore left the run
/// Running, and nothing logged it. These tests fail on that code: the Activity never turns terminal.</para>
///
/// <para>The kernel here is a real kernel PLUG-IN, not a mock: an <see cref="IKernelHubConfigurator"/>
/// registered in DI the same way <c>AddKernel()</c> registers the Roslyn one, wired into the real
/// Activity hub by <c>ActivityNodeType</c>'s own <c>AddKernelSubHubHandlers</c>. It never writes the
/// Activity — exactly the failure shape under test.</para>
/// </summary>
public class CSharpCellKernelFaultFailsTheActivityTest(ITestOutputHelper output) : CSharpCellRunTestBase(output)
{
    private const string AnswerFailure = "// answer-failure";
    private const string Throw = "// throw";

    /// <summary>A kernel that faults before running anything and never touches the ActivityLog.</summary>
    private sealed class FaultingKernel : IKernelHubConfigurator
    {
        public MessageHubConfiguration Configure(MessageHubConfiguration config)
        {
            config.TypeRegistry.WithType(typeof(SubmitCodeRequest), nameof(SubmitCodeRequest));
            config.TypeRegistry.WithType(typeof(SubmitCodeResponse), nameof(SubmitCodeResponse));
            return config.WithHandler<SubmitCodeRequest>((hub, request) =>
            {
                if (request.Message.Code.Contains(Throw, StringComparison.Ordinal))
                    throw new InvalidOperationException("faulting kernel: the submission handler threw");
                // The KernelContainer's own dispatch-fault shape: Success=false, log untouched.
                hub.Post(
                    new SubmitCodeResponse(request.Message.Id, false) { Error = "faulting kernel: dispatch failed" },
                    o => o.ResponseFor(request));
                return request.Processed();
            });
        }
    }

    /// <inheritdoc />
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder)
            .ConfigureServices(services =>
            {
                services.RemoveAll<IKernelHubConfigurator>();
                return services.AddSingleton<IKernelHubConfigurator>(new FaultingKernel());
            });

    /// <summary>
    /// The kernel answers a failure it did not record — the KernelContainer's dispatch-fault reply.
    /// The dispatcher must write that failure onto the Activity, reason and all.
    /// </summary>
    [Fact(Timeout = 180_000)]
    public async Task AKernelThatAnswersAFailureItDidNotRecord_LeavesTheActivityFailed()
    {
        var ct = TestContext.Current.CancellationToken;
        var cell = await CreateCell($"{AnswerFailure}\n1 + 1", ct);
        var activity = await Run(cell, ct);

        var log = await Terminal(activity,
            "the kernel answered Success=false and the dispatcher never reconciled it", ct);

        log.Status.Should().Be(ActivityStatus.Failed,
            "the kernel never ran the script, so nothing else will ever settle this run");
        log.Messages.Should().Contain(m => m.Message.Contains("faulting kernel: dispatch failed"),
            "the reader of the Output pane must see WHY the run failed, not a bare red status");
        log.Messages.Should().Contain(m => m.MessageKey == "activity.codeRun.failed",
            "the entry is keyed so the Output pane renders it in the viewer's language");
    }

    /// <summary>
    /// The kernel's handler throws — the delivery itself faults. The dispatcher must observe that
    /// fault and fail the Activity rather than leave it Running.
    /// </summary>
    [Fact(Timeout = 180_000)]
    public async Task AKernelWhoseHandlerThrows_LeavesTheActivityFailed()
    {
        var ct = TestContext.Current.CancellationToken;
        var cell = await CreateCell($"{Throw}\n1 + 1", ct);
        var activity = await Run(cell, ct);

        var log = await Terminal(activity,
            "the kernel's handler threw and the dispatcher never observed the delivery fault", ct);

        log.Status.Should().Be(ActivityStatus.Failed,
            "a faulted delivery is a run that will never be settled by anyone else");
        log.Messages.Should().Contain(m => m.Message.StartsWith("C# run failed", StringComparison.Ordinal),
            "the failure is recorded on the transcript, not only in the host's log");
    }
}

/// <summary>
/// The control for <see cref="CSharpCellKernelFaultFailsTheActivityTest"/>, on the REAL Roslyn
/// kernel: observing the kernel's answer must never turn a run the kernel settled into something
/// else. A success stays Succeeded; a script that throws is Failed by the kernel itself, once.
/// Without this, "the Activity ends Failed" could be satisfied by a reconcile that fails every run.
/// </summary>
public class CSharpCellRunKeepsTheKernelsVerdictTest(ITestOutputHelper output) : CSharpCellRunTestBase(output)
{
    /// <summary>A successful run stays Succeeded — the reconcile does not fire on success.</summary>
    [Fact(Timeout = 180_000)]
    public async Task ASuccessfulRun_StaysSucceeded()
    {
        var ct = TestContext.Current.CancellationToken;
        var cell = await CreateCell("1 + 1", ct);
        var activity = await Run(cell, ct);

        var log = await Terminal(activity, "the real kernel ran `1 + 1`", ct);

        log.Status.Should().Be(ActivityStatus.Succeeded,
            "the kernel ran the script and settled the log; nothing may overwrite that verdict");
        log.Messages.Should().NotContain(m => m.Message.StartsWith("C# run failed", StringComparison.Ordinal),
            "the dispatcher's reconcile must stay silent for a run that succeeded");
    }

    /// <summary>
    /// A script that throws is failed BY THE KERNEL; the dispatcher's reconcile (the response is
    /// Success=false) must not overwrite or duplicate that settled verdict.
    /// </summary>
    [Fact(Timeout = 180_000)]
    public async Task AScriptThatThrows_IsFailedByTheKernel_AndNotOverwritten()
    {
        var ct = TestContext.Current.CancellationToken;
        var cell = await CreateCell("throw new System.InvalidOperationException(\"boom from the cell\");", ct);
        var activity = await Run(cell, ct);

        var log = await Terminal(activity, "the real kernel ran a throwing script", ct);

        log.Status.Should().Be(ActivityStatus.Failed, "the script threw");
        log.Messages.Should().Contain(m => m.Message.Contains("boom from the cell"),
            "the kernel records the script's own exception");
    }
}
