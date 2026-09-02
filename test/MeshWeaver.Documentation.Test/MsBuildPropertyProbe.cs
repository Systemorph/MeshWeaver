using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using Xunit;

namespace MeshWeaver.Documentation.Test;

/// <summary>
/// Evaluates MSBuild properties of a project in this repository, out of process, the way CD does.
/// Shared by the two #3022 guards — <see cref="CompiledVersionAttributesIgnoreVersionOverrideGuard"/>
/// (does <c>-p:Version=</c> reach a compiled attribute?) and
/// <see cref="CdImageLegPropertiesDoNotForkTheIdentityGuard"/> (does ANY property CD's two image
/// legs pass reach one?) — because both ask the same question of the same evaluator and a second
/// copy of a process launcher is a second place for the draining and timeout rules below to rot.
/// </summary>
internal static class MsBuildPropertyProbe
{
    /// <summary>
    /// Evaluates <paramref name="project"/> (a repo-relative path) and returns the requested
    /// properties. Fails RED — never skips — when the project is missing, <c>dotnet</c> cannot be
    /// launched, MSBuild exits non-zero, or the answer is not the JSON shape <c>-getProperty</c>
    /// promises: "could not measure" must never be reported as "measured and fine".
    /// </summary>
    /// <param name="project">Repo-relative path of the project to evaluate, forward-slashed.</param>
    /// <param name="propertyNames">The properties to read back.</param>
    /// <param name="extraArguments">Extra MSBuild switches, e.g. <c>-p:Version=…</c>.</param>
    public static IReadOnlyDictionary<string, string> Evaluate(
        string project,
        IReadOnlyList<string> propertyNames,
        params string[] extraArguments)
    {
        var root = FindRepoRoot();
        var projectPath = Path.Combine(root, project.Replace('/', Path.DirectorySeparatorChar));
        Assert.True(File.Exists(projectPath),
            $"{project} does not exist — a #3022 guard evaluates it to prove that a property CD "
            + "passes cannot reach a compiled version attribute. Re-point the guard at another "
            + "project that inherits the root Directory.Build.props and is part of "
            + "FrameworkBuildIdentity.ContentSurfaceAssemblies.");

        var psi = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add("msbuild");
        psi.ArgumentList.Add(projectPath);
        psi.ArgumentList.Add("-nologo");
        // CIRun is how the platform is built everywhere the identity matters; evaluating without it
        // would exercise the local-dev branch and prove nothing about CD.
        psi.ArgumentList.Add("-p:CIRun=true");
        foreach (var name in propertyNames)
            psi.ArgumentList.Add($"-getProperty:{name}");
        foreach (var argument in extraArguments)
            psi.ArgumentList.Add(argument);

        using var process = Process.Start(psi);
        Assert.NotNull(process);

        // 🚨 Drain BOTH pipes concurrently via the event-based reader, never
        // `StandardOutput.ReadToEnd()` then `StandardError.ReadToEnd()`. Sequential draining
        // deadlocks whenever the child writes enough to the *undrained* pipe to fill its buffer:
        // MSBuild blocks writing stderr, the test blocks reading stdout, and neither moves. That
        // these guards exist to catch a wedge makes hanging in the same way particularly poor.
        // Event-based reading needs no TaskCompletionSource and no async bridge, so it stays
        // inside the house rules for `test/`.
        var outBuffer = new StringBuilder();
        var errBuffer = new StringBuilder();
        process!.OutputDataReceived += (_, e) => { if (e.Data is not null) outBuffer.AppendLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) errBuffer.AppendLine(e.Data); };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        // `-getProperty` only EVALUATES (no restore, no compile) — sub-second warm, a few seconds
        // cold. A minute is far past any legitimate evaluation, so hitting it means MSBuild is
        // wedged, and the guard says so instead of hanging out xUnit's method timeout.
        if (!process.WaitForExit(60_000))
        {
            // Kill the TREE, not just `dotnet` — MSBuild spawns node processes that outlive the
            // launcher, and a guard that leaks build nodes on every timeout degrades the machine
            // it is measuring on.
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { /* already gone */ }
            Assert.Fail(
                $"`dotnet msbuild {project} -getProperty:…` did not finish within 60s. Evaluation "
                + "does not restore or build, so this is a wedged MSBuild, not a slow one.");
        }

        // 🚨 The TIMED overload waits only for the PROCESS; the parameterless one is what waits for
        // the async readers started above to finish flushing. Reading the buffers straight after
        // WaitForExit(ms) can therefore catch a TRUNCATED stdout — and a truncated JSON document
        // makes this guard fail on a parse error instead of on its subject, which is exactly the
        // "measured the wrong thing" failure the guard exists to prevent. The process has already
        // exited here, so this returns as soon as the readers drain.
        process.WaitForExit();

        var stdout = outBuffer.ToString();
        var stderr = errBuffer.ToString();

        Assert.True(process.ExitCode == 0,
            $"`dotnet msbuild {project} -getProperty:…` exited {process.ExitCode}.\n"
            + $"arguments: {string.Join(' ', extraArguments)}\n"
            + $"stdout:\n{stdout}\nstderr:\n{stderr}");

        using var document = JsonDocument.Parse(stdout);
        var properties = document.RootElement.GetProperty("Properties");
        return propertyNames.ToDictionary(
            name => name,
            name => properties.GetProperty(name).GetString() ?? string.Empty,
            StringComparer.Ordinal);
    }

    /// <summary>The repository root — the directory holding <c>MeshWeaver.slnx</c>.</summary>
    public static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "MeshWeaver.slnx")))
            dir = dir.Parent;
        return dir?.FullName
               ?? throw new InvalidOperationException("Could not locate the repository root (no MeshWeaver.slnx above the test binary).");
    }
}
