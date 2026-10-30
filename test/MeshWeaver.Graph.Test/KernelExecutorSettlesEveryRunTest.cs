using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Fixture;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Kernel.Hub;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// #7362, option A: the kernel executor settles every run it owns. A run in flight when its host
/// goes away ends <see cref="ActivityStatus.Failed"/> with a stated, keyed reason — never Running
/// for ever — and a cancel reaches a run wherever it is parked, not only once its script runs.
///
/// <para>Real mesh, real Roslyn kernel. A cell parks on a GATE node the test opens in a
/// <c>finally</c>, so "still running" lasts exactly as long as the test needs; every wait is on a
/// condition, never on the clock.</para>
/// </summary>
public abstract class KernelRunSettlementTestBase(ITestOutputHelper output) : CSharpCellRunTestBase(output)
{
    /// <summary>A cell that waits until the gate node is named "go", then returns 7.</summary>
    protected static string WaitsForGate(string gatePath) => $$"""
        using System.Reactive.Linq;
        await Mesh.GetMeshNodeStream("{{gatePath}}")
            .Where(n => n?.Name == "go")
            .Take(1)
            .Await(Ct);
        7
        """;

    /// <summary>Creates a closed gate node and returns its path.</summary>
    protected async Task<string> CreateGate(CancellationToken ct)
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

    /// <summary>Opens the gate, releasing a script parked on it.</summary>
    protected async Task OpenGate(string gatePath, CancellationToken ct)
    {
        var access = Mesh.ServiceProvider.GetRequiredService<AccessService>();
        await access
            .RunAsSystem(() => Mesh.GetWorkspace().GetMeshNodeStream(gatePath)
                .Update(n => n with { Name = "go" }))
            .FirstAsync()
            .Timeout(Bound)
            .Await(ct);
    }

    /// <summary>The run's Activity once it is Running — the script (or a stage) is parked.</summary>
    protected async Task<ActivityLog> Running(string activityPath, CancellationToken ct)
    {
        var options = Mesh.JsonSerializerOptions;
        return (await Mesh.GetWorkspace().GetMeshNodeStream(activityPath)
            .Where(node => node is not null)
            .Select(node => node.ContentAs<ActivityLog>(options))
            .Where(log => log is not null && log.Status == ActivityStatus.Running)
            .FirstAsync()
            .Timeout(Bound)
            .Await(ct))!;
    }

    /// <summary>
    /// Disposes the Activity's hub — the kernel host and, with it, its executor — as a recycle does,
    /// with a stated reason, issued off the router.
    /// </summary>
    protected async Task DisposeKernelHost(string activityPath, CancellationToken ct)
    {
        var access = Mesh.ServiceProvider.GetRequiredService<AccessService>();
        await access
            .RunAsSystem(() => Observable.Defer(() =>
            {
                Mesh.NodeOperationIssuingHub().Post(
                    new DisposeRequest
                    {
                        Reason = "KernelExecutorSettlesEveryRunTest: the kernel host goes away while a run is in flight",
                    },
                    o => o.WithTarget(new Address(activityPath)));
                return Observable.Return(Unit.Default);
            }))
            .Await(ct);
    }

    /// <summary>Requests the cancel the way the Output pane does: RequestedStatus = Cancelled on the Activity.</summary>
    protected void Cancel(string activityPath)
    {
        var access = Mesh.ServiceProvider.GetRequiredService<AccessService>();
        using (access.ImpersonateAsSystem())
            Mesh.CancelActivity(activityPath);
    }

    /// <summary>Whether <paramref name="log"/> carries the keyed host-shutdown reason.</summary>
    protected static bool SaysTheHostShutDown(ActivityLog log)
        => log.Messages.Any(m => m.MessageKey == KernelHostShutDownException.MessageKey);
}

/// <summary>The kernel host goes away mid-run: the run ends Failed with its reason.</summary>
public class KernelHostDisposedMidRunFailsTheRunTest(ITestOutputHelper output) : KernelRunSettlementTestBase(output)
{
    /// <summary>
    /// A script parked on its gate when the Activity hub is disposed: before the fix the executor's
    /// disposal wrote nothing and the run stayed Running for ever; now it is Failed, saying why.
    /// </summary>
    [Fact(Timeout = 180_000)]
    public async Task ARunInFlight_WhenItsKernelHostIsDisposed_EndsFailedWithTheReason()
    {
        var ct = TestContext.Current.CancellationToken;
        var gate = await CreateGate(ct);
        try
        {
            var activity = await Run(await CreateCell(WaitsForGate(gate), ct), ct);
            await Running(activity, ct);

            await DisposeKernelHost(activity, ct);

            var log = await Terminal(activity, "its kernel host was disposed while the script was parked", ct);
            log.Status.Should().Be(ActivityStatus.Failed, "the run did not finish, and nobody cancelled it");
            SaysTheHostShutDown(log).Should().BeTrue(
                "the reader must see WHY the run ended — the host shut down — in their language");
        }
        finally
        {
            await OpenGate(gate, CancellationToken.None);
        }
    }

    /// <summary>
    /// Negative control: a run that already finished keeps its own verdict when its host is
    /// disposed afterwards — the settle-on-dispose only touches runs that are still owed an answer.
    /// </summary>
    [Fact(Timeout = 180_000)]
    public async Task AFinishedRun_KeepsItsVerdict_WhenItsHostIsDisposedLater()
    {
        var ct = TestContext.Current.CancellationToken;
        var activity = await Run(await CreateCell("1 + 1", ct), ct);
        var finished = await Terminal(activity, "the in-time `1 + 1` run", ct);
        finished.Status.Should().Be(ActivityStatus.Succeeded);

        await DisposeKernelHost(activity, ct);

        // The re-activated hub serves what the store holds — the run's own verdict.
        var options = Mesh.JsonSerializerOptions;
        var reread = (await Mesh.GetWorkspace().GetMeshNodeStream(activity)
            .Where(node => node is not null)
            .Select(node => node.ContentAs<ActivityLog>(options))
            .Where(log => log is not null && log.Status.IsTerminal())
            .FirstAsync()
            .Timeout(Bound)
            .Await(ct))!;
        reread.Status.Should().Be(ActivityStatus.Succeeded, "a settled run is never re-judged by its host's disposal");
        SaysTheHostShutDown(reread).Should().BeFalse("the run finished before its host went away");
    }
}

/// <summary>A cancel reaches a run parked in a preparation stage, before its script ever runs.</summary>
public class KernelCancelReachesAParkedStageTest(ITestOutputHelper output) : KernelRunSettlementTestBase(output)
{
    /// <summary>
    /// A cell-surface provider whose resolution never answers — the kernel's preparation stage
    /// parks on it — and which says when a run has reached it.
    /// </summary>
    private sealed class ParkedCellSurface : ICellSurfaceAssemblyProvider
    {
        public AsyncSubject<Unit> Reached { get; } = new();

        public IObservable<IReadOnlyCollection<CellSurfaceAssembly>> ResolveCellSurfaceAssemblies()
            => Observable.Create<IReadOnlyCollection<CellSurfaceAssembly>>(_ =>
            {
                Reached.OnNext(Unit.Default);
                Reached.OnCompleted();
                return System.Reactive.Disposables.Disposable.Empty;
            });
    }

    private readonly ParkedCellSurface parked = new();

    /// <inheritdoc />
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder)
            .ConfigureServices(services =>
            {
                services.RemoveAll<ICellSurfaceAssemblyProvider>();
                return services.AddSingleton<ICellSurfaceAssemblyProvider>(parked);
            });

    /// <summary>
    /// The run is parked in the cell-surface stage; a cancel must end it Cancelled. Before the fix
    /// the run's token reached only the script pass, so the run stayed Running for ever.
    /// </summary>
    [Fact(Timeout = 180_000)]
    public async Task ARunParkedInAPreparationStage_IsCancelled()
    {
        var ct = TestContext.Current.CancellationToken;
        var activity = await Run(await CreateCell("1 + 1", ct), ct);
        await parked.Reached.Should().Within(Bound).Emit(
            "the run must be parked in the cell-surface stage before the cancel is sent");

        Cancel(activity);

        var log = await Terminal(activity, "a cancel was sent while the run was parked in a preparation stage", ct);
        log.Status.Should().Be(ActivityStatus.Cancelled, "the cancel reached the stage the run was parked in");
    }
}

/// <summary>
/// Negative control for <see cref="KernelCancelReachesAParkedStageTest"/>: with the stages answering,
/// an ordinary run is unaffected — it succeeds with its value and says nothing about a shutdown.
/// </summary>
public class KernelOrdinaryRunIsUnaffectedTest(ITestOutputHelper output) : KernelRunSettlementTestBase(output)
{
    /// <summary>`1 + 1` runs to Succeeded exactly as before.</summary>
    [Fact(Timeout = 180_000)]
    public async Task AnOrdinaryRun_Succeeds()
    {
        var ct = TestContext.Current.CancellationToken;
        var activity = await Run(await CreateCell("1 + 1", ct), ct);

        var log = await Terminal(activity, "an ordinary `1 + 1` run", ct);

        log.Status.Should().Be(ActivityStatus.Succeeded);
        log.ReturnValue?.ToString().Should().Be("2");
        SaysTheHostShutDown(log).Should().BeFalse();
    }
}
