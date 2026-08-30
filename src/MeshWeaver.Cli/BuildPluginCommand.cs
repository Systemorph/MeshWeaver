using System.Diagnostics;

namespace MeshWeaver.Cli;

/// <summary>
/// <c>memex build plugin &lt;path&gt; --image &lt;image&gt;</c> — the whole CI contract for building a
/// plugin, in one verb (<c>Doc/Architecture/InMeshBuildAndTest</c>).
///
/// <para>A plugin repo's CI job becomes three lines: check out, install this tool, run this. The
/// tool pulls the image, pins it by DIGEST, and runs the platform's own builder and tester inside
/// it — so nothing in the job knows about docker, mounts, seeds, allow-files or registries.</para>
///
/// <para><b>Two stages, and the split is deliberate (#1763).</b> The same tool produces and
/// consumes: <c>compile … --output /bake</c> emits one bundle per package plus the bake identity,
/// then the gate runs with <c>--seed /bake</c> and stands a mesh up on <b>those bytes</b>. Testing
/// the baked bytes is a strictly stronger claim than a fused pass, because the bytes judged are the
/// bytes that ship.</para>
/// </summary>
public sealed class BuildPluginCommand(TextWriter output, TextWriter error)
{
    /// <summary>Attempts for a registry PULL — see <see cref="PullImage"/>.</summary>
    private const int PullAttempts = 3;

    /// <summary>
    /// The file the compile stage writes to record which framework the bundles were built against.
    /// Its presence is the POSITIVE signal that a bake actually happened: a compile that emitted no
    /// bundles still exits 0, so "the command returned" proves nothing.
    /// </summary>
    private const string BakeIdentityFile = "framework-mvid.txt";

    public async Task<int> RunAsync(BuildPluginOptions options, CancellationToken ct)
    {
        var repo = Path.GetFullPath(options.PluginPath);
        if (!Directory.Exists(repo))
        {
            await error.WriteLineAsync($"error: plugin path '{repo}' does not exist.");
            return 2;
        }

        // Pin by DIGEST once, and use it for both stages. The workflow this replaces already did
        // this (`${IMAGE%%:*}@${DIGEST}`) for a reason: a tag can move between the compile and the
        // gate, and then the bytes tested are not the bytes baked — which is the one claim the
        // two-stage split exists to make.
        var pinned = await PullImage(options.Image, ct);
        if (pinned is null) return 4;
        await output.WriteLineAsync($"image: {pinned}");

        var bake = options.BakeOutput is { Length: > 0 }
            ? Path.GetFullPath(options.BakeOutput)
            : Path.Combine(Path.GetTempPath(), $"memex-bake-{Environment.ProcessId}");
        Directory.CreateDirectory(bake);

        // ── stage 1: PRODUCE ───────────────────────────────────────────────────────────────────
        var compile = await RunInImage(pinned, repo, options,
            mounts: [$"{bake}:/bake"],
            env: [],
            args: ["compile", "/repo", ..AllowArgs(repo, options), ..options.ExtraArgs,
                   "--output", "/bake", ..SourceShaArgs(options)],
            ct);
        if (compile != 0) return compile;

        var identity = Path.Combine(bake, BakeIdentityFile);
        if (!File.Exists(identity) || new FileInfo(identity).Length == 0)
        {
            await error.WriteLineAsync(
                $"error: compile ran but wrote no bake identity ({BakeIdentityFile} missing or empty) "
                + "— the bake stage regressed. Refusing to test bytes that were never produced.");
            return 5;
        }

        // ── stage 2: CONSUME — stand a mesh up on the bytes stage 1 produced ───────────────────
        return await RunInImage(pinned, repo, options,
            mounts: [$"{bake}:/seed:ro"],
            env: ["MW_INSTALL_DIFF=1"],
            args: ["/repo", ..AllowArgs(repo, options), ..options.ExtraArgs, "--seed", "/seed"],
            ct);
    }

    private static IEnumerable<string> AllowArgs(string repo, BuildPluginOptions o)
    {
        var allow = o.AllowFile is { Length: > 0 } ? o.AllowFile : "plugin-gate.allow";
        return File.Exists(Path.Combine(repo, allow)) ? ["--allow", $"/repo/{allow}"] : [];
    }

    private static IEnumerable<string> SourceShaArgs(BuildPluginOptions o) =>
        o.SourceSha is { Length: > 0 } sha ? ["--source-sha", sha] : [];

    /// <summary>
    /// Pull the image and resolve it to a digest.
    ///
    /// <para>🚨 Bounded retry, and deliberately NOT a skip-trapdoor: after the last attempt this
    /// returns null and the command fails RED. It exists because a registry pull is the one step
    /// here that fails for reasons that have nothing to do with the change under test — on
    /// 2026-08-30 core CD lost a whole release to `Connection refused` on one pull, and the same
    /// class of fault had already put a bounded retry into three node-repo workflows.</para>
    /// </summary>
    private async Task<string?> PullImage(string image, CancellationToken ct)
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
                        + "so the two stages are pinned only as well as the tag is.");
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

    private Task<int> RunInImage(
        string image, string repo, BuildPluginOptions o,
        IEnumerable<string> mounts, IEnumerable<string> env, IEnumerable<string> args,
        CancellationToken ct)
    {
        var docker = new List<string> { "run", "--rm", "-v", $"{repo}:/repo" };
        foreach (var m in mounts) { docker.Add("-v"); docker.Add(m); }
        if (o.ExternalModulesDir is { Length: > 0 } ext)
        { docker.Add("-v"); docker.Add($"{Path.GetFullPath(ext)}:/ext"); }
        foreach (var e in env) { docker.Add("-e"); docker.Add(e); }
        docker.Add("--entrypoint"); docker.Add("/app/mw-plugin-test");
        docker.Add(image);
        docker.AddRange(args);
        return Exec("docker", docker, ct);
    }

    private async Task<int> Exec(string file, IEnumerable<string> args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(file) { UseShellExecute = false };
        foreach (var a in args) psi.ArgumentList.Add(a);
        await output.WriteLineAsync($"$ {file} {string.Join(' ', psi.ArgumentList)}");
        using var p = Process.Start(psi);
        if (p is null)
        {
            await error.WriteLineAsync($"error: could not start '{file}' — is Docker installed and on PATH?");
            return 127;
        }
        await p.WaitForExitAsync(ct);
        return p.ExitCode;
    }

    private static async Task<string> Capture(string file, IEnumerable<string> args, CancellationToken ct)
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

/// <summary>Arguments for <see cref="BuildPluginCommand"/>. A record so a caller cannot half-set it.</summary>
public sealed record BuildPluginOptions(
    string PluginPath,
    string Image,
    string? BakeOutput = null,
    string? ExternalModulesDir = null,
    string? SourceSha = null,
    string? AllowFile = null,
    IReadOnlyList<string>? Extra = null)
{
    public IReadOnlyList<string> ExtraArgs => Extra ?? [];
}
