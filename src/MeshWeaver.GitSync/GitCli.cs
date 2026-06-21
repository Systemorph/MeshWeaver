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

    private GitCommandResult Exec(
        string workingDir,
        IReadOnlyList<string> args,
        IReadOnlyDictionary<string, string>? env,
        CancellationToken ct)
    {
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

        using var p = new Process { StartInfo = psi };
        p.Start();
        // Cancellation kills the whole git process tree so a slot is never leaked on unsubscribe.
        using var reg = ct.Register(() =>
        {
            try { if (!p.HasExited) p.Kill(entireProcessTree: true); }
            catch { /* already gone */ }
        });
        // Drain both streams concurrently BEFORE WaitForExit so a full stderr/stdout buffer can't deadlock.
        var outTask = p.StandardOutput.ReadToEndAsync(ct);
        var errTask = p.StandardError.ReadToEndAsync(ct);
        p.WaitForExit();
        var stdout = outTask.GetAwaiter().GetResult();
        var stderr = errTask.GetAwaiter().GetResult();
        return new GitCommandResult(p.ExitCode, stdout.TrimEnd('\n', '\r'), stderr.TrimEnd('\n', '\r'));
    }
}
