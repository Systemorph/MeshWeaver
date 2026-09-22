using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Compiler;
using MeshWeaver.Graph.Configuration;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// 🚨 A NodeType compile must not occupy the shared <see cref="ThreadPool"/> — the pool Orleans grain
/// turns, the routing pool's subscribe legs and every reactive continuation run on.
///
/// <para>Two things put it there, and both are pinned here. (1) Roslyn's <c>ConcurrentBuild</c>, on by
/// default, fans an emit out onto <see cref="TaskScheduler.Default"/> from whatever thread started it —
/// so <see cref="CompileThread"/>'s dedicated thread kept only the CALLER off the pool, never the work.
/// (2) The service's async compile leaf ran its Roslyn tail on a pool worker after its first await.
/// Measured with a ThreadPool dispatch probe at 6 CPUs: six concurrent emits held the probe's p99 at
/// ~420 ms with <c>ConcurrentBuild</c> on and ~0.2 ms with it off
/// (<c>Doc/Architecture/CompileOffTheThreadPool</c>).</para>
///
/// <para>The instrument is deterministic, not a timing: an <see cref="AsyncLocal{T}"/> whose change
/// handler fires whenever a thread starts running code under the emit's
/// <see cref="ExecutionContext"/>. Roslyn's fan-out tasks flow that context, so every pool worker
/// that picks up a piece of the emit is COUNTED — no sampling, no latency threshold.</para>
/// </summary>
public class RoslynEmitStaysOffTheThreadPoolTest
{
    /// <summary>Enough types and method bodies that Roslyn's concurrent build has work to fan out.</summary>
    private static string Source()
    {
        var sb = new StringBuilder("namespace Probe {");
        for (var c = 0; c < 24; c++)
        {
            sb.Append("public class C").Append(c).Append(" {");
            for (var m = 0; m < 24; m++)
                sb.Append("public int M").Append(m).Append("(int x) { var s = 0; for (var i = 0; i < x; i++) s += i * ")
                  .Append(m).Append("; return s + ").Append(c).Append("; }");
            sb.Append('}');
        }
        return sb.Append('}').ToString();
    }

    private static CSharpCompilation Compilation() =>
        EmitPipeline.CreateEmitCompilation(
            Source(), "DynamicNode_PoolProbe_" + Guid.NewGuid().ToString("N"),
            [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)],
            parsePath: "", CancellationToken.None);

    /// <summary>
    /// Emits <paramref name="compilation"/> on a <see cref="CompileThread"/> thread and returns how
    /// many ThreadPool workers ran any part of that emit.
    /// </summary>
    private static async Task<int> PoolWorkersThatRanTheEmit(CSharpCompilation compilation)
    {
        var tag = new object();
        var poolEntries = 0;
        var marker = new AsyncLocal<object?>(args =>
        {
            // ThreadContextChanged: this thread just STARTED running under a context carrying the
            // tag — i.e. a work item queued from inside the emit. The dedicated thread itself sets
            // the value directly (ThreadContextChanged false), so it is never counted.
            if (args.ThreadContextChanged && ReferenceEquals(args.CurrentValue, tag)
                && Thread.CurrentThread.IsThreadPoolThread)
                Interlocked.Increment(ref poolEntries);
        });

        await CompileThread.Run(() =>
        {
            marker.Value = tag;
            var (bytes, _) = EmitPipeline.EmitToMemory(compilation, "Probe/Node", CancellationToken.None);
            marker.Value = null;
            return bytes.Length;
        });
        return Volatile.Read(ref poolEntries);
    }

    [Fact]
    public async Task TheEmitRunsWholly_OnItsDedicatedThread_WithTheRunOptions()
    {
        var compilation = Compilation();
        compilation.Options.ConcurrentBuild.Should().BeFalse(
            "every compilation the pipeline builds must run with ConcurrentBuild off");

        (await PoolWorkersThatRanTheEmit(compilation)).Should().Be(0,
            "a compile started on a CompileThread must not hand any of its work to the shared "
            + "ThreadPool — that pool delivers every grain turn and routing leg on the silo");
    }

    /// <summary>
    /// NEGATIVE CONTROL — the same emit with Roslyn's default (<c>ConcurrentBuild</c> on) DOES run on
    /// pool workers even though it was started on a dedicated thread. Without this the zero above
    /// could be an instrument that counts nothing.
    /// </summary>
    [Fact]
    public async Task Control_RoslynsDefaultConcurrentBuild_FansTheEmitOutOntoThePool()
    {
        var compilation = Compilation();
        var concurrent = compilation.WithOptions(compilation.Options.WithConcurrentBuild(true));

        (await PoolWorkersThatRanTheEmit(concurrent)).Should().BeGreaterThan(0,
            "control: ConcurrentBuild queues the emit's work onto TaskScheduler.Default — the "
            + "starvation source a dedicated thread alone does not remove");
    }

    /// <summary>
    /// The content key must NOT move: <c>ConcurrentBuild</c> is a scheduling choice, so it is applied
    /// after the fingerprinted factory and the fingerprint still renders Roslyn's default.
    /// </summary>
    [Fact]
    public void TheContentKeyStillRendersTheCanonicalOptions()
    {
        EmitPipeline.CreateCompilationOptions().ConcurrentBuild.Should().BeTrue(
            "the fingerprinted factory is unchanged — changing it would re-key every NodeType");
        EmitPipeline.OptionsFingerprint.Should().Contain("CSharpCompilationOptions.ConcurrentBuild=true\n",
            "the content key renders the canonical options, not the run options");
        EmitPipeline.CreateRunCompilationOptions()
            .WithConcurrentBuild(true).Should().Be(EmitPipeline.CreateCompilationOptions(),
                "the run options differ from the canonical ones in ConcurrentBuild ONLY");
    }
}
