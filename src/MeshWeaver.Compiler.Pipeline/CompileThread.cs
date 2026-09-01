using System;
using System.Threading;
using System.Threading.Tasks;

namespace MeshWeaver.Graph.Configuration;

/// <summary>
/// Runs the synchronous Roslyn compile leaf (Emit + assembly load + reflection) on a
/// DEDICATED long-running thread instead of the shared <see cref="ThreadPool"/>.
///
/// <para>🚨 Why not <c>Task.Run</c> (the ThreadPool): a Roslyn Emit is multi-second,
/// CPU-bound, SYNCHRONOUS work. <c>Task.Run</c> occupies a ThreadPool worker thread for
/// that whole duration. A burst of concurrent compiles (a compile-heavy CI shard, or a
/// portal under load) blocks the pool's worker threads, and the ThreadPool grows only
/// slowly (hill-climbing adds ~1-2 threads/second). Until it catches up, the reactive
/// continuations that deliver every cross-hub response — which ALSO run on the
/// ThreadPool — are starved and time out. That is the bulk-only "a different test times
/// out each run" flake class (InvitationService, LinkedInTelemetryImport, …): not CPU
/// exhaustion, but ThreadPool worker-thread starvation by long synchronous compiles.</para>
///
/// <para>A dedicated thread keeps the compile's CPU work OFF the pool the actor/reactive
/// scheduler depends on. <see cref="TaskCreationOptions.LongRunning"/> makes the default
/// scheduler spin up a fresh thread (never a pooled one); concurrency is naturally bounded
/// by the compilation service's per-NodeType single-flight, so this never explodes into a
/// thread storm. <see cref="ExecutionContext"/> still flows (AsyncLocal — the
/// <c>AccessService</c> identity the compile re-establishes), exactly as <c>Task.Run</c>
/// did, so the off-pool move changes scheduling only, not identity.</para>
/// </summary>
public static class CompileThread
{
    /// <summary>
    /// Runs <paramref name="work"/> on a dedicated long-running thread and returns a
    /// <see cref="Task{T}"/> that completes with its result. The returned task is hot.
    /// </summary>
    public static Task<T> Run<T>(Func<T> work) =>
        Task.Factory.StartNew(
            work,
            CancellationToken.None,
            TaskCreationOptions.LongRunning | TaskCreationOptions.DenyChildAttach,
            TaskScheduler.Default);
}
