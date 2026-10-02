using System.Reactive.Concurrency;
using Xunit;

namespace MeshWeaver.Data.Test;

/// <summary>
/// Pins the scheduler of the data context's initialization-settle hop (#5555). Every hub's
/// <see cref="DataContext"/> settles its initialization gate through
/// <c>ObserveOn(DataContext.InitializationSettleScheduler)</c>. When that hop is Rx's
/// <see cref="TaskPoolScheduler.Default"/>, <c>ObserveOn</c> sees <see cref="ISchedulerLongRunning"/>
/// and starts a dedicated OS thread per subscription — one thread creation per hub activation.
/// The settle must stay one ordinary pool work item, so the scheduler must not offer long-running.
/// </summary>
public class InitializationSettleHopIsAPoolWorkItemTest
{
    /// <summary>The scheduler withholds <see cref="ISchedulerLongRunning"/>, so <c>ObserveOn</c>
    /// takes the per-item pooled path instead of a dedicated thread per hub.</summary>
    [Fact]
    public void TheSettleScheduler_OffersNoLongRunningCapability()
    {
        DataContext.InitializationSettleScheduler.AsLongRunning().Should().BeNull(
            because: "ObserveOn on a long-running-capable scheduler dedicates a thread per subscription, " +
                     "and this subscription exists once per hub activation");
        TaskPoolScheduler.Default.AsLongRunning().Should().NotBeNull(
            because: "negative control: Rx's own task pool DOES advertise long-running — which is the trap");
    }
}
