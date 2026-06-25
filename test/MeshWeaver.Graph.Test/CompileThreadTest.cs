using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Graph.Configuration;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// Pins the root cause of the bulk-only compile-shard flake class: the synchronous Roslyn
/// compile leaf must run OFF the shared <see cref="ThreadPool"/>.
///
/// <para>A Roslyn Emit is multi-second, CPU-bound, synchronous work. Run on <c>Task.Run</c>
/// it pins a ThreadPool worker thread for its whole duration; a burst of compiles blocks the
/// pool's (few, slow-growing) workers and starves the reactive continuations — which also run
/// on the ThreadPool — that deliver every cross-hub response, so a different unrelated test
/// times out each CI run. <see cref="CompileThread"/> moves that work onto a dedicated thread.
/// The control assertion proves the distinction is real (Task.Run genuinely IS the pool we
/// must avoid), so this test is a deterministic repro of the cause, not just a property check.</para>
/// </summary>
public class CompileThreadTest
{
    [Fact]
    public void Run_ExecutesWork_OnADedicatedThread_NotTheThreadPool()
    {
        var onDedicatedThread = CompileThread
            .Run(() => Thread.CurrentThread.IsThreadPoolThread)
            .GetAwaiter().GetResult();
        onDedicatedThread.Should().BeFalse(
            "the synchronous compile leaf must run on a dedicated thread — never a ThreadPool "
            + "worker the reactive scheduler needs to deliver cross-hub responses");

        // Control: Task.Run (what the leaf used before) runs ON the ThreadPool — the exact
        // starvation source we are moving compiles off of.
        var onPoolThread = Task.Run(() => Thread.CurrentThread.IsThreadPoolThread)
            .GetAwaiter().GetResult();
        onPoolThread.Should().BeTrue(
            "control: Task.Run schedules on the shared ThreadPool — a burst of multi-second "
            + "compiles there is what starved the reactive continuations");
    }
}
