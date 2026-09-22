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
/// scheduler spin up a fresh thread (never a pooled one). The number of such threads is the
/// number of DISTINCT NodeTypes compiling at once (the compilation service single-flights per
/// NodeType) — not a small constant on a big mesh, but each is one OS thread the kernel
/// time-slices fairly against the pool, which is what keeps the pool's queue moving.</para>
///
/// <para>🚨 <b>A dedicated thread is only HALF of keeping a compile off the pool.</b> Roslyn's
/// <c>ConcurrentBuild</c> (on by default) fans the emit back out onto
/// <see cref="TaskScheduler.Default"/> from whatever thread started it, so the work run here
/// must be built with <c>EmitPipeline.CreateRunCompilationOptions()</c> (ConcurrentBuild off).
/// And the work must actually be HANDED to this method: an <c>async</c> leaf passed through
/// <c>Task.Run</c> resumes its CPU-bound tail on a pool worker after its first await — which is
/// where every production emit ran until the emit half was split out and hopped here
/// (<c>Doc/Architecture/CompileOffTheThreadPool</c>).</para>
///
/// <para><see cref="ExecutionContext"/> still flows (AsyncLocal — the
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
