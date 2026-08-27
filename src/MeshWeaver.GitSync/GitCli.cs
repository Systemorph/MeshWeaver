using System.Collections.Concurrent;
using System.Diagnostics;
using MeshWeaver.Mesh.Threading;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.GitSync;

/// <summary>
/// Thin reactive wrapper around the <c>git</c> CLI. Every invocation is a blocking
/// <see cref="Process"/> leaf, so it runs through <see cref="IIoPool"/>'s
/// <see cref="IoPoolNames.Process"/> pool (<see cref="IIoPool.InvokeBlocking{T}"/>) — the
/// sanctioned boundary for sync-blocking work off the hub schedulers. The public surface is
/// <see cref="IObservable{T}"/>; no <c>async</c>/<c>await</c>/<c>Task</c> escapes a signature.
///
/// <para>The same system <c>git</c> is shared by the co-hosted Claude Code / Copilot CLIs, so a
/// working tree edited here and a working tree the AI harness operates on are byte-identical.</para>
/// </summary>
public sealed class GitCli(IoPoolRegistry ioPools, ILogger<GitCli>? logger = null)
{
    private IIoPool Pool => ioPools.Get(IoPoolNames.Process);

    /// <summary>
    /// Runs <c>git</c> with the given argument list in <paramref name="workingDir"/>. Args are passed
    /// via <see cref="ProcessStartInfo.ArgumentList"/> (no shell quoting). Optional <paramref name="env"/>
    /// adds environment variables (e.g. <c>GW_TOKEN</c> for the credential helper) — secrets travel in the
    /// environment, never in argv. Subscribe to run (cold; one Process pool slot per Subscribe).
    /// </summary>
    public IObservable<GitCommandResult> Run(
        string workingDir,
        IReadOnlyList<string> args,
        IReadOnlyDictionary<string, string>? env = null) =>
        Pool.InvokeBlocking(ct => Exec(workingDir, args, env, ct));

    internal GitCommandResult Exec(
        string workingDir,
        IReadOnlyList<string> args,
        IReadOnlyDictionary<string, string>? env,
        CancellationToken ct)
    {
        // Never spawn a process for work that has already been cancelled (the pool's token is
        // cancelled on unsubscribe): the git run could not be observed by anyone.
        ct.ThrowIfCancellationRequested();

        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = workingDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in args)
            psi.ArgumentList.Add(a);
        // Never block on an interactive prompt (auth, host-key) — fail fast instead of hanging a pool slot.
        psi.Environment["GIT_TERMINAL_PROMPT"] = "0";
        if (env is not null)
            foreach (var kv in env)
                psi.Environment[kv.Key] = kv.Value;

        logger?.LogDebug("git {Args} (cwd={Cwd})", string.Join(' ', args), workingDir);

        // 🚨 The two pipes MUST drain concurrently — a git that fills one buffer while we block on
        // the other deadlocks — and this leaf must stay genuinely SYNCHRONOUS, because that is what
        // IIoPool.InvokeBlocking takes. Both at once, with no Task: stderr goes to the event pump
        // (BeginErrorReadLine, raised by the runtime on its own thread) while stdout is read to EOF
        // on this pool thread. It used to be two ReadToEndAsync tasks unwrapped with
        // GetAwaiter().GetResult() — sync-over-async, and the only Task left in this assembly.
        //
        // Two asymmetries below are deliberate:
        //  • STDOUT stays ReadToEnd(), not the line pump. `git show <rev>:<path>` returns FILE
        //    CONTENT (GitWorkingTreeService.ShowFile, GitPackageSource.ShowFile), and a line pump
        //    would silently rewrite its CRLFs. stderr only ever reaches a log line or a Contains()
        //    probe, so reassembling ITS lines costs nothing.
        //  • STDERR accumulates into a ConcurrentQueue, not a StringBuilder. The handler is serial
        //    and WaitForExit() joins it, so a StringBuilder would in fact be ordered — but "correct
        //    because of two contracts, one of them an overload distinction" is a poor thing to leave
        //    for the next reader. Enqueue order is the only property the join relies on.
        var stderrLines = new ConcurrentQueue<string>();
        using var p = new Process { StartInfo = psi };
        p.ErrorDataReceived += (_, e) => { if (e.Data is not null) stderrLines.Enqueue(e.Data); };
        p.Start();
        // Cancellation kills the whole git process tree so a slot is never leaked on unsubscribe.
        // It also closes stdout, which is what releases the ReadToEnd() below.
        using var reg = ct.Register(() =>
        {
            try { if (!p.HasExited) p.Kill(entireProcessTree: true); }
            catch { /* already gone */ }
        });
        p.BeginErrorReadLine();
        var stdout = p.StandardOutput.ReadToEnd();
        // Parameterless WaitForExit() — the overload that also joins the async output handlers, so
        // stderrLines is complete when it returns. The timeout overloads do NOT make that promise.
        p.WaitForExit();

        // 🚨 A cancelled run must NOT report a result. The kill above leaves git with a signal exit
        // code, and returning that would look to every Expect(...) like git had genuinely failed —
        // an unsubscribe would surface as a GitWorkingTreeException rather than as cancellation.
        // ReadToEndAsync(ct) used to raise this for free; reading synchronously means raising it
        // here. (Caught in review of #2531 — it was a real regression, not a hypothetical.)
        ct.ThrowIfCancellationRequested();

        return new GitCommandResult(
            p.ExitCode,
            stdout.TrimEnd('\n', '\r'),
            string.Concat(stderrLines.Select(l => l + '\n')).TrimEnd('\n', '\r'));
    }
}
