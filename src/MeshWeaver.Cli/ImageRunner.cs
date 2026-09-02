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
    /// The docker argv for a run on the COMPOSED GATE HOST (#3113): the PLATFORM (portal) image's
    /// own <c>dotnet</c> starting the tester CLI out of <paramref name="hostDirectory"/>, mounted
    /// read-only at <see cref="GateHost.HostMount"/>.
    ///
    /// <para>Pure, and therefore pinned by a test rather than by a docker-shaped run CI would have
    /// to skip — the defect this shape exists to prevent is not a crash but a run that succeeds
    /// against the WRONG reference set, which looks identical from outside. Same reason
    /// <c>DockerImageGateArgsTest</c> pins the combo gate's argv.</para>
    ///
    /// <para>🚨 Everything before the image reference is DOCKER's and everything after it is the
    /// tool's. A flag that drifts past the image is not a flag any more: docker never sees it and
    /// <c>mw-plugin-test</c> receives it as an unknown argument, so the run silently stops being
    /// composed while still looking correct.</para>
    /// </summary>
    /// <param name="platformImage">The PORTAL image reference — it supplies <c>dotnet</c>, the
    /// <c>/app</c> reference set and the implementation frameworks.</param>
    /// <param name="hostDirectory">The composed host on this machine (portal <c>/app</c> + tester CLI).</param>
    /// <param name="user">
    /// <c>uid:gid</c> to run as, or null to accept the image's own user. The portal image runs as
    /// ROOT, so without this every file the run writes into a mounted output directory lands
    /// root-owned and a later step of the caller's job cannot rewrite it — the reason both
    /// node-repo-publish-bake.yml and node-repo-module-pack.yml pass <c>$(id -u):$(id -g)</c>.
    /// </param>
    /// <param name="mounts">Bind mounts, each already in <c>host:container[:ro]</c> form.</param>
    /// <param name="env">Environment assignments, each <c>NAME=value</c>.</param>
    /// <param name="toolArgs">Arguments for the tester CLI.</param>
    /// <returns>The complete argv for <c>docker</c>.</returns>
    public static string[] ComposedHostRunArgs(
        string platformImage,
        string hostDirectory,
        string? user,
        IEnumerable<string> mounts,
        IEnumerable<string> env,
        IEnumerable<string> toolArgs)
    {
        // --init for the same reason as every other run of this tool here (#1741): without it the
        // tool is the container's PID 1, and a PID-namespace init with SIG_DFL for SIGABRT is
        // SIGNAL_UNKILLABLE — a crashed process SPINS at 100% CPU instead of exiting.
        var docker = new List<string> { "run", "--rm", "--init" };
        if (user is { Length: > 0 })
        {
            docker.Add("--user");
            docker.Add(user);
        }
        // HOME=/tmp keeps the runtime's probes off the image's read-only paths — a run as a uid the
        // image never provisioned has no home otherwise.
        docker.Add("-e");
        docker.Add("HOME=/tmp");
        docker.Add("-v");
        docker.Add($"{hostDirectory}:{GateHost.HostMount}:ro");
        foreach (var mount in mounts) { docker.Add("-v"); docker.Add(mount); }
        foreach (var assignment in env) { docker.Add("-e"); docker.Add(assignment); }
        // 🚨 `dotnet`, not /app/mw-plugin-test: the ENTRY ASSEMBLY must live on the composed host,
        // because the framework identity and the TPA are read from the entry assembly's directory.
        // Starting the portal image's own /app/mw-plugin-test would be starting a binary that is
        // not there; starting the tester image would restore the subset reference set (#3113).
        docker.Add("--entrypoint"); docker.Add("dotnet");
        docker.Add(platformImage);
        docker.Add($"{GateHost.HostMount}/{GateHost.TesterCli}");
        docker.AddRange(toolArgs);
        return [.. docker];
    }

    /// <summary>Runs the tester CLI out of a composed gate host inside the platform image.</summary>
    /// <param name="platformImage">The PORTAL image reference.</param>
    /// <param name="hostDirectory">The composed host on this machine.</param>
    /// <param name="user"><c>uid:gid</c> to run as, or null for the image's own user.</param>
    /// <param name="mounts">Bind mounts, each already in <c>host:container[:ro]</c> form.</param>
    /// <param name="env">Environment assignments, each <c>NAME=value</c>.</param>
    /// <param name="toolArgs">Arguments for the tester CLI.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The container's exit code.</returns>
    public Task<int> RunOnComposedHost(
        string platformImage,
        string hostDirectory,
        string? user,
        IEnumerable<string> mounts,
        IEnumerable<string> env,
        IEnumerable<string> toolArgs,
        CancellationToken ct) =>
        Exec("docker", ComposedHostRunArgs(platformImage, hostDirectory, user, mounts, env, toolArgs), ct);

    /// <summary>
    /// Extracts an image's <c>/app</c> onto this machine — <c>docker create</c>, <c>docker cp</c>,
    /// <c>docker rm</c>, the same three commands the lanes run.
    /// </summary>
    /// <param name="image">The image reference to extract from.</param>
    /// <param name="destination">Where <c>/app</c> lands; removed first if it exists.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>True when the directory was extracted.</returns>
    public async Task<bool> ExtractApp(string image, string destination, CancellationToken ct)
    {
        if (Directory.Exists(destination)) Directory.Delete(destination, recursive: true);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var container = (await Capture("docker", ["create", image], ct)).Trim();
        if (container.Length == 0)
        {
            await error.WriteLineAsync($"error: could not create a container from '{image}' to read its /app.");
            return false;
        }
        try
        {
            if (await Exec("docker", ["cp", $"{container}:/app", destination], ct) != 0)
            {
                await error.WriteLineAsync($"error: could not copy /app out of '{image}'.");
                return false;
            }
        }
        finally
        {
            await Exec("docker", ["rm", container], ct);
        }
        return true;
    }

    /// <summary>
    /// The <c>uid:gid</c> a container should run as so its output is owned by whoever invoked the
    /// CLI, or null when this platform has no such mapping (Windows) or cannot report one.
    /// </summary>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The <c>uid:gid</c> pair, or null.</returns>
    public async Task<string?> ResolveInvokingUser(CancellationToken ct)
    {
        if (OperatingSystem.IsWindows()) return null;
        var uid = (await Capture("id", ["-u"], ct)).Trim();
        var gid = (await Capture("id", ["-g"], ct)).Trim();
        if (uid.Length > 0 && gid.Length > 0) return $"{uid}:{gid}";
        await output.WriteLineAsync(
            "note: could not resolve this user's uid/gid ('id' unavailable) — the run uses the "
            + "platform image's own user, so anything it writes into an output directory will be "
            + "owned by that user.");
        return null;
    }

    /// <summary>
    /// Runs a process, echoing its stderr to this runner's error writer, and returns its exit code
    /// with its stdout.
    ///
    /// <para>🚨 Unlike <see cref="Capture"/> this does NOT discard the diagnostic. The
    /// <c>framework-identity</c> verb writes its verdict — the canonical assemblies each side's
    /// manifest lacks — to stderr, and that text IS the actionable half of a mismatch (#1814). A
    /// refusal that swallowed it would report "the images disagree" and name nothing.</para>
    /// </summary>
    /// <param name="file">The executable.</param>
    /// <param name="args">Its arguments.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The exit code (127 when the process could not be started) and standard output.</returns>
    public async Task<(int ExitCode, string StdOut)> CaptureVerbose(
        string file, IEnumerable<string> args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(file)
        { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        await output.WriteLineAsync($"$ {file} {string.Join(' ', psi.ArgumentList)}");
        using var p = Process.Start(psi);
        if (p is null)
        {
            await error.WriteLineAsync($"error: could not start '{file}' — is it installed and on PATH?");
            return (127, string.Empty);
        }
        var stdout = await p.StandardOutput.ReadToEndAsync(ct);
        var stderr = await p.StandardError.ReadToEndAsync(ct);
        await p.WaitForExitAsync(ct);
        if (stderr.Length > 0) await error.WriteAsync(stderr);
        return (p.ExitCode, stdout);
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
