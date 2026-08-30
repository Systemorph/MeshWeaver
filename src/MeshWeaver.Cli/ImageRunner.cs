using System.Diagnostics;

namespace MeshWeaver.Cli;

/// <summary>
/// The plumbing every <c>memex build …</c> verb shares: pull an image, pin it by DIGEST, run a
/// command inside it, and start a process without losing its exit code.
///
/// <para>Factored out of <see cref="BuildPluginCommand"/> when <c>build project</c> arrived
/// (#2841 + the no-SDK builder). Two verbs that each carried their own copy of the pull retry
/// and the digest pin would drift, and the pin is not a detail: a tag can move between two runs,
/// and then the bytes a verb reports on are not the bytes it built against.</para>
/// </summary>
public sealed class ImageRunner(TextWriter output, TextWriter error)
{
    /// <summary>Attempts for a registry PULL.</summary>
    public const int PullAttempts = 3;

    /// <summary>
    /// Mode bits a bind mount needs for a container that runs as a NON-ROOT user — the
    /// <c>chmod 777</c> every docker-run gate performs. Proven the hard way (Manufacturing #37):
    /// 15/15 NodeTypes compiled, then <c>UnauthorizedAccessException</c> writing the first output.
    /// </summary>
    public const UnixFileMode WorldRwx =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
        UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute |
        UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute;

    /// <summary>
    /// Makes a directory writable by the image's non-root user. Windows has no unix mode and no
    /// such docker convention; there it is a no-op.
    /// </summary>
    /// <param name="directory">The directory to open up.</param>
    public static void MakeContainerWritable(string directory)
    {
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(directory, WorldRwx);
    }

    /// <summary>
    /// Pull the image and resolve it to a digest.
    ///
    /// <para>🚨 Bounded retry, and deliberately NOT a skip-trapdoor: after the last attempt this
    /// returns null and the caller fails RED. It exists because a registry pull is the one step
    /// here that fails for reasons that have nothing to do with the change under test — on
    /// 2026-08-30 core CD lost a whole release to <c>Connection refused</c> on one pull.</para>
    /// </summary>
    /// <param name="image">The image reference to pull.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The digest-pinned reference, the tag when the image is local-only, or null on failure.</returns>
    public async Task<string?> PullImage(string image, CancellationToken ct)
    {
        for (var attempt = 1; attempt <= PullAttempts; attempt++)
        {
            if (await Exec("docker", ["pull", image], ct) == 0)
            {
                var digest = await Capture("docker",
                    ["image", "inspect", image, "--format", "{{index .RepoDigests 0}}"], ct);
                // No digest is not a failure to retry: a locally-built image legitimately has none,
                // and pinning is then simply not available. Say so rather than inventing one.
                if (string.IsNullOrWhiteSpace(digest))
                {
                    await output.WriteLineAsync(
                        $"note: '{image}' has no repo digest (local image?) — using the tag as given, "
                        + "so the run is pinned only as well as the tag is.");
                    return image;
                }
                return digest.Trim();
            }
            if (attempt < PullAttempts)
            {
                await error.WriteLineAsync(
                    $"pull attempt {attempt} of {PullAttempts} failed for '{image}' — retrying in 10s");
                await Task.Delay(TimeSpan.FromSeconds(10), ct);
            }
        }
        await error.WriteLineAsync(
            $"error: could not pull '{image}' in {PullAttempts} attempts. Three consecutive failures "
            + "is no longer a transient — check the registry and the credentials rather than retrying.");
        return null;
    }

    /// <summary>
    /// Runs <paramref name="args"/> through the image's <c>/app/mw-plugin-test</c> entry point.
    /// </summary>
    /// <param name="image">The (ideally digest-pinned) image reference.</param>
    /// <param name="mounts">Bind mounts, each already in <c>host:container[:ro]</c> form.</param>
    /// <param name="env">Environment assignments, each <c>NAME=value</c>.</param>
    /// <param name="args">Arguments for the tool inside the image.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The container's exit code.</returns>
    public Task<int> RunInImage(
        string image,
        IEnumerable<string> mounts,
        IEnumerable<string> env,
        IEnumerable<string> args,
        CancellationToken ct)
    {
        // --init, always: the tool is the container's PID 1 otherwise, and a PID-namespace init
        // with SIG_DFL for SIGABRT SPINS on an unhandled exception instead of dying (#1741).
        var docker = new List<string> { "run", "--rm", "--init" };
        foreach (var mount in mounts) { docker.Add("-v"); docker.Add(mount); }
        foreach (var assignment in env) { docker.Add("-e"); docker.Add(assignment); }
        docker.Add("--entrypoint"); docker.Add("/app/mw-plugin-test");
        docker.Add(image);
        docker.AddRange(args);
        return Exec("docker", docker, ct);
    }

    /// <summary>
    /// Resolves an image that must NOT be pulled — a locally built one, which no registry has.
    ///
    /// <para>🚨 Never a fallback for a failed pull, and never inferred: <c>docker buildx</c> stamps
    /// a repo digest on a purely local image too, so "has no digest" does not identify one and a
    /// heuristic here would quietly accept a stale local copy the day a registry is unreachable.
    /// The caller ASKS for this (<c>--no-pull</c>), and it still fails when the image is absent.</para>
    /// </summary>
    /// <param name="image">The local image reference.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The reference, or null when the daemon does not have it.</returns>
    public async Task<string?> UseLocalImage(string image, CancellationToken ct)
    {
        if ((await Capture("docker", ["image", "inspect", image, "--format", "{{.Id}}"], ct))
            is not { Length: > 0 } id)
        {
            await error.WriteLineAsync(
                $"error: --no-pull was given but the docker daemon has no image '{image}'. Build it "
                + "first, or drop --no-pull to pull it from a registry.");
            return null;
        }
        await output.WriteLineAsync(
            $"note: --no-pull — using the local image '{image}' ({id.Trim()[..19]}…) as given, so the "
            + "run is pinned only as well as that tag is.");
        return image;
    }

    /// <summary>Starts a process, echoes the command line, and returns its exit code.</summary>
    /// <param name="file">The executable.</param>
    /// <param name="args">Its arguments.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The exit code, or 127 when the executable could not be started.</returns>
    public async Task<int> Exec(string file, IEnumerable<string> args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(file) { UseShellExecute = false };
        foreach (var a in args) psi.ArgumentList.Add(a);
        await output.WriteLineAsync($"$ {file} {string.Join(' ', psi.ArgumentList)}");
        using var p = Process.Start(psi);
        if (p is null)
        {
            await error.WriteLineAsync($"error: could not start '{file}' — is it installed and on PATH?");
            return 127;
        }
        await p.WaitForExitAsync(ct);
        return p.ExitCode;
    }

    /// <summary>Runs a process and returns its stdout, or the empty string when it failed.</summary>
    /// <param name="file">The executable.</param>
    /// <param name="args">Its arguments.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>Standard output on success, otherwise the empty string.</returns>
    public static async Task<string> Capture(string file, IEnumerable<string> args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(file)
        { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi);
        if (p is null) return string.Empty;
        var stdout = await p.StandardOutput.ReadToEndAsync(ct);
        await p.WaitForExitAsync(ct);
        return p.ExitCode == 0 ? stdout : string.Empty;
    }
}
