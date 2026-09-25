using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Kernel.Hub;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Scripting;
using Xunit;

namespace MeshWeaver.Compiler.Pipeline.Test;

/// <summary>
/// 🚨 A code cell's compile must not occupy the shared <see cref="ThreadPool"/> (#5388) — the same
/// rule <see cref="MeshWeaver.Graph.Test.RoslynEmitStaysOffTheThreadPoolTest"/> pins for a NodeType
/// emit (<c>Doc/Architecture/CompileOffTheThreadPool</c>).
///
/// <para>Before: the kernel ran a cell as ONE async leaf of the Compile pool, so the whole Roslyn
/// build + emit ran on a ThreadPool worker before the leaf's first await, and the emit ran with
/// Roslyn's default <c>ConcurrentBuild</c>, which fans the work out onto
/// <see cref="TaskScheduler.Default"/> from whatever thread starts it. Now
/// <see cref="ScriptSession.Compile"/> is the CPU-bound half on its own, built with
/// <c>ConcurrentBuild</c> off, and the executor runs it on the CPU lane.</para>
///
/// <para>The instrument is deterministic, not a timing: an <see cref="AsyncLocal{T}"/> whose change
/// handler fires whenever a thread starts running code under the compile's
/// <see cref="ExecutionContext"/>. Roslyn's fan-out tasks flow that context, so every pool worker that
/// picks up a piece of the compile is COUNTED.</para>
/// </summary>
public class KernelCellCompileStaysOffTheThreadPoolTest
{
    /// <summary>The script's globals — public, as the scripting host requires.</summary>
    public sealed class ProbeGlobals
    {
        /// <summary>A value the script reads, so the submission protocol is exercised end to end.</summary>
        public int Seed { get; init; } = 41;
    }

    /// <summary>Enough types and method bodies that Roslyn's concurrent build has work to fan out.</summary>
    private static string Cell()
    {
        var sb = new StringBuilder();
        for (var c = 0; c < 24; c++)
        {
            sb.Append("public class C").Append(c).Append(" {");
            for (var m = 0; m < 24; m++)
                sb.Append("public int M").Append(m).Append("(int x) { var s = 0; for (var i = 0; i < x; i++) s += i * ")
                  .Append(m).Append("; return s + ").Append(c).Append("; }");
            sb.Append('}');
        }
        return sb.Append("Seed + 1").ToString();
    }

    private static ScriptOptions Options() => ScriptOptions.Default
        .WithReferences(typeof(object).Assembly, typeof(ProbeGlobals).Assembly);

    /// <summary>Runs <paramref name="work"/> on a dedicated thread under a tag and counts the pool workers that ran any of it.</summary>
    private static async Task<(int PoolWorkers, T Result)> Counted<T>(Func<T> work)
    {
        var tag = new object();
        var poolEntries = 0;
        var marker = new AsyncLocal<object?>(args =>
        {
            // ThreadContextChanged: this thread just STARTED running under a context carrying the
            // tag — a work item queued from inside the compile. The dedicated thread sets the value
            // directly (ThreadContextChanged false), so it is never counted.
            if (args.ThreadContextChanged && ReferenceEquals(args.CurrentValue, tag)
                && Thread.CurrentThread.IsThreadPoolThread)
                Interlocked.Increment(ref poolEntries);
        });

        var result = await CompileThread.Run(() =>
        {
            marker.Value = tag;
            try { return work(); }
            finally { marker.Value = null; }
        });
        return (Volatile.Read(ref poolEntries), result);
    }

    [Fact]
    public async Task ACellsCompile_HandsNoWorkToThePool_AndStillRuns()
    {
        using var session = new ScriptSession(new ProbeGlobals());

        var (poolWorkers, compiled) = await Counted(() =>
            session.Compile(Cell(), Options(), typeof(ProbeGlobals), CancellationToken.None));

        compiled.Compilation.Options.ConcurrentBuild.Should().BeFalse(
            "a cell's compilation must be emitted with ConcurrentBuild off");
        poolWorkers.Should().Be(0,
            "a cell compile started on a dedicated thread must not hand any of its work to the shared "
            + "ThreadPool — that pool delivers every grain turn and routing leg on the silo");

        (await session.ExecuteAsync(compiled, CancellationToken.None)).Should().Be(42,
            "the split must keep the submission protocol: the compiled cell runs and reads its globals");
    }

    /// <summary>
    /// NEGATIVE CONTROL — the same cell's compilation with Roslyn's default (<c>ConcurrentBuild</c> on,
    /// which is what <see cref="ScriptSession"/> emitted before) DOES run on pool workers although it
    /// was started on a dedicated thread. Without this the zero above could be an instrument that
    /// counts nothing.
    /// </summary>
    [Fact]
    public async Task Control_TheScriptCompilationsDefault_FansTheEmitOutOntoThePool()
    {
        var compilation = CSharpScriptCompilation();
        compilation.Options.ConcurrentBuild.Should().BeTrue("control: Roslyn's script default is a concurrent build");

        var (poolWorkers, emitted) = await Counted(() =>
        {
            using var pe = new System.IO.MemoryStream();
            return compilation.Emit(pe).Success;
        });

        emitted.Should().BeTrue();
        poolWorkers.Should().BeGreaterThan(0,
            "control: ConcurrentBuild queues the emit's work onto TaskScheduler.Default — the "
            + "starvation source a dedicated thread alone does not remove");
    }

    [Fact]
    public void WithoutConcurrentBuild_ChangesOnlyTheSchedulingOption()
    {
        var compilation = CSharpScriptCompilation();
        var run = ScriptSession.WithoutConcurrentBuild(compilation);

        run.Options.ConcurrentBuild.Should().BeFalse();
        run.Options.WithConcurrentBuild(true).Should().Be(compilation.Options,
            "the cell's compile options differ from Roslyn's in ConcurrentBuild ONLY");
    }

    private static Compilation CSharpScriptCompilation() =>
        Microsoft.CodeAnalysis.CSharp.Scripting.CSharpScript
            .Create(Cell(), Options(), typeof(ProbeGlobals))
            .GetCompilation();
}
