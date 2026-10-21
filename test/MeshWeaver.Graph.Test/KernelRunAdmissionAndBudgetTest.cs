using System;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Fixture;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Threading;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using System.Reactive.Linq;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// The <see cref="IoPoolNames.KernelScript"/> lane is admitted WITHOUT a queue. Its leaves wait on
/// each other — a cell that runs a cell holds its slot while the child needs a slot of the same lane
/// to warm up and execute — so a queued child behind waiting parents is the nested-gate deadlock
/// again, merely moved off <c>Compile</c>. Here the lane has ONE slot: the parent holds it, and the
/// child must be REFUSED (its Activity Failed, naming the refusal) so the parent sees the failure
/// and completes. With queued admission the child is never admitted and both stay Running.
/// </summary>
public class KernelScriptLaneRefusesInsteadOfQueuingTest(ITestOutputHelper output) : CSharpCellRunTestBase(output)
{
    /// <inheritdoc />
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder)
            .ConfigureServices(services =>
            {
                services.RemoveAll<IoPoolOptions>();
                return services.AddSingleton(new IoPoolOptions { KernelScript = 1 });
            });

    /// <summary>
    /// A parent waiting on a child, with the script lane saturated by that parent: the child is
    /// refused and fails visibly; the parent completes with the child's verdict.
    /// </summary>
    [Fact(Timeout = 180_000)]
    public async Task AChildCellOnASaturatedScriptLane_IsRefused_AndItsParentCompletes()
    {
        var ct = TestContext.Current.CancellationToken;
        Mesh.ServiceProvider.GetRequiredService<IoPoolRegistry>().Get(IoPoolNames.KernelScript)
            .Should().BeOfType<IoPool>().Which.MaxConcurrency.Should().Be(1,
                "the lane must have ONE slot, so the parent alone saturates it");

        var inner = await CreateCell("40 + 2", ct);
        var outer = await CreateCell(RunsAndWaitsFor(inner), ct);

        var outerLog = await Terminal(await Run(outer, ct),
            "the parent held the script lane's only slot while its child queued behind it", ct);
        outerLog.Status.Should().Be(ActivityStatus.Succeeded,
            "the parent must learn its child's fate instead of waiting for ever");
        outerLog.ReturnValue?.ToString().Should().Be("inner Failed",
            "the child could not be admitted while its parent held the only slot, so it was refused");

        var innerActivity = await Mesh.GetWorkspace().GetMeshNodeStream(inner)
            .Select(n => n?.ContentAs<CodeConfiguration>(Mesh.JsonSerializerOptions)?.LastActivityPath)
            .Where(p => !string.IsNullOrEmpty(p))
            .FirstAsync()
            .Timeout(Bound)
            .Await(ct);
        var innerLog = await Terminal(innerActivity!, "the refused child", ct);
        innerLog.Messages.Should().Contain(m => m.Message.Contains("Refused"),
            "the refused run names WHY it failed, so a reader is not left guessing");
    }
}

/// <summary>
/// The kernel's reply is the END of a run, and every wait for it is bounded by its hub's
/// <c>RequestTimeout</c> of silence. A healthy cell computing past that budget is still running: no
/// hop may turn its own deadline into a verdict on the run. Two hops waited: the dispatcher
/// (<c>CodeNodeType</c>, on the Code hub) and the kernel container (on the Activity hub), which used
/// to convert its own timeout into <c>Success=false</c>. Each derived class shortens the budget on a
/// different set of hubs so each hop is pinned on its own; both fail without the fix — the run reads
/// Failed while its script is still executing.
/// </summary>
public abstract class LongCellOutlivesTheRequestBudgetTestBase(ITestOutputHelper output) : CSharpCellRunTestBase(output)
{
    /// <summary>The request budget the selected hubs run with — far below the cell's run time.</summary>
    protected static readonly TimeSpan ShortBudget = TimeSpan.FromSeconds(2);

    /// <summary>Whether the hub at <paramref name="address"/> runs with <see cref="ShortBudget"/>.</summary>
    protected abstract bool HasShortBudget(Address address);

    /// <inheritdoc />
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder)
            .ConfigureDefaultNodeHub(c => HasShortBudget(c.Address) ? c.WithRequestTimeout(ShortBudget) : c);

    // A healthy script that runs three times the budget. Its wait is the script's own subject (a
    // long-running user computation), bridged through the sanctioned `.Await(Ct)`.
    private const string RunsPastTheBudget = """
        using System.Reactive.Linq;
        await Observable.Timer(System.TimeSpan.FromSeconds(6)).Take(1).Await(Ct);
        7
        """;

    /// <summary>The run's FIRST terminal status is Succeeded — it is never failed while still running.</summary>
    [Fact(Timeout = 180_000)]
    public async Task AHealthyCellRunningPastTheRequestBudget_Succeeds()
    {
        var ct = TestContext.Current.CancellationToken;
        var cell = await CreateCell(RunsPastTheBudget, ct);
        var log = await Terminal(await Run(cell, ct), "a healthy six-second cell", ct);

        log.Status.Should().Be(ActivityStatus.Succeeded,
            "the cell was still executing when the request budget ran out; a deadline is not a verdict on it");
        log.ReturnValue?.ToString().Should().Be("7", "the script ran to its end");
    }
}

/// <summary>Every node hub on the short budget — pins the DISPATCHER's wait, which expires first.</summary>
public class LongCellOutlivesTheDispatchersRequestBudgetTest(ITestOutputHelper output)
    : LongCellOutlivesTheRequestBudgetTestBase(output)
{
    /// <inheritdoc />
    protected override bool HasShortBudget(Address address) => true;
}

/// <summary>Only the Activity hubs on the short budget — pins the KERNEL CONTAINER's wait, whose
/// timeout used to come back to the dispatcher as <c>Success=false</c>.</summary>
public class LongCellOutlivesTheKernelContainersRequestBudgetTest(ITestOutputHelper output)
    : LongCellOutlivesTheRequestBudgetTestBase(output)
{
    /// <inheritdoc />
    protected override bool HasShortBudget(Address address)
        => address.ToString().Contains("/_Activity/", StringComparison.Ordinal);
}
