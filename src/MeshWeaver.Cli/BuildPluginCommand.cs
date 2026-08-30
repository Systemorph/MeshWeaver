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
        // 🚨 The tester image runs as a NON-ROOT user, so the bake mount must be writable by it —
        // the `chmod 777 "$OWN_BAKE"` every docker-run gate performs. Proven the hard way on the
        // verb's first production run (Manufacturing #37): 15/15 NodeTypes compiled, then
        // `UnauthorizedAccessException: Access to the path '/bake/Manufacturing.zip' is denied`
        // writing the first bundle. Applied to a caller-supplied directory too: the caller asked
        // for a bake THERE, and a mount the container cannot write is a promise this command
        // cannot keep. Windows has no unix mode and no such non-root docker convention; skip.
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(bake,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute |
                UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute);

        // ── dependencies: INSTALL, never build (PluginBuildContract step 2) ─────────────────────
        // Proven necessary on the verb's first production run: without the external modules the
        // gate installed 162 files and registered ZERO NodeTypes — every '$type' (AgentConfiguration
        // first) degraded to an untyped JsonElement, and the gate correctly refused to judge bytes
        // it never installed. A package with a Skill/Agent subtree needs MeshWeaver.AI REGISTERED,
        // not merely resolvable.
        var moduleArgs = new List<string>();
        var extDir = options.ExternalModulesDir is { Length: > 0 } e ? Path.GetFullPath(e) : null;
        if (options is { RegistryModules.Length: > 0 })
        {
            if (options.RegistryUrl is not { Length: > 0 } || options.RegistryKey is not { Length: > 0 })
            {
                await error.WriteLineAsync(
                    "error: --registry-modules needs --registry-url and a key (MW_REGISTRY_KEY or "
                    + "--registry-key). A dependency install that silently skips is a gate that "
                    + "tests nothing, so this refuses rather than proceeding without them.");
                return 6;
            }
            extDir ??= Path.Combine(Path.GetTempPath(), $"memex-ext-{Environment.ProcessId}");
            var fetched = await FetchRegistryModules(
                options.RegistryUrl!, options.RegistryKey!, options.RegistryModules!, extDir, ct);
            if (fetched is null) return 7;
            moduleArgs.AddRange(fetched);
        }
        else if (extDir is not null)
        {
            foreach (var dir in Directory.EnumerateDirectories(extDir))
            {
                var name = Path.GetFileName(dir);
                if (File.Exists(Path.Combine(dir, $"{name}.dll")))
                { moduleArgs.Add("--module"); moduleArgs.Add($"/ext/{name}/{name}.dll"); }
            }
        }
        if (extDir is not null && !OperatingSystem.IsWindows() && Directory.Exists(extDir))
            File.SetUnixFileMode(extDir, WorldRwx);

        // ── stage 1: PRODUCE ───────────────────────────────────────────────────────────────────
        // Module args go to BOTH stages: the compile needs the externals to RESOLVE their types,
        // the gate needs them to REGISTER them (the node-repo gate passes them to both for exactly
        // this reason, and dropping either half reintroduces one of the two failures).
        var compile = await RunInImage(pinned, repo, options, extDir,
            mounts: [$"{bake}:/bake"],
            env: [],
            args: ["compile", "/repo", ..AllowArgs(repo, options), ..moduleArgs, ..options.ExtraArgs,
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
        return await RunInImage(pinned, repo, options, extDir,
            mounts: [$"{bake}:/seed:ro"],
            env: ["MW_INSTALL_DIFF=1"],
            args: ["/repo", ..AllowArgs(repo, options), ..moduleArgs, ..options.ExtraArgs, "--seed", "/seed"],
            ct);
    }

    private const UnixFileMode WorldRwx =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
        UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute |
        UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute;

    /// <summary>
    /// Fetches each named package's sealed bundle from the plugin registry and composes it under
    /// <paramref name="extDir"/> in the layout the tester consumes (<c>/ext/&lt;name&gt;/&lt;name&gt;.dll</c>).
    /// The client half of the contract <c>PluginBundleEndpoints</c> serves, and byte-for-byte the
    /// composition the node-repo gate performs in shell: index → advertised version → bundle zip →
    /// <c>meshweaver/manifest.json</c> names the entry assembly (single-DLL fallback) →
    /// <c>meshweaver/modules/</c> copied whole. Returns the <c>--module</c> args, or null on failure
    /// (each failure already reported, naming the package).
    /// </summary>
    private async Task<List<string>?> FetchRegistryModules(
        string registryUrl, string key, string modules, string extDir, CancellationToken ct)
    {
        var args = new List<string>();
        Directory.CreateDirectory(extDir);
        using var http = new HttpClient { BaseAddress = new Uri(registryUrl.TrimEnd('/') + "/") };
        http.DefaultRequestHeaders.Authorization = new("Bearer", key);

        System.Text.Json.JsonDocument index;
        try
        {
            await using var s = await http.GetStreamAsync("api/plugins/bundles/index.json", ct);
            index = await System.Text.Json.JsonDocument.ParseAsync(s, cancellationToken: ct);
        }
        catch (Exception ex)
        {
            await error.WriteLineAsync($"error: registry index unreadable at {registryUrl}: {ex.Message}");
            return null;
        }

        using (index)
        foreach (var pkg in modules.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string? version = null;
            var root = index.RootElement;
            var bundles = root.TryGetProperty("bundles", out var b) ? b
                        : root.TryGetProperty("Bundles", out var b2) ? b2 : default;
            if (bundles.ValueKind == System.Text.Json.JsonValueKind.Array)
                foreach (var entry in bundles.EnumerateArray())
                {
                    var plugin = entry.TryGetProperty("plugin", out var pl) ? pl.GetString()
                               : entry.TryGetProperty("Plugin", out var pl2) ? pl2.GetString() : null;
                    if (!string.Equals(plugin, pkg, StringComparison.OrdinalIgnoreCase)) continue;
                    version = entry.TryGetProperty("version", out var ve) ? ve.GetString()
                            : entry.TryGetProperty("Version", out var ve2) ? ve2.GetString() : null;
                    break;
                }
            if (version is not { Length: > 0 })
            {
                await error.WriteLineAsync(
                    $"error: the registry at {registryUrl} does not advertise package '{pkg}' to this "
                    + "key — check the key's grants and the package name.");
                return null;
            }

            var zipPath = Path.Combine(extDir, $"{pkg}.bundle.zip");
            try
            {
                await using var body = await http.GetStreamAsync($"api/plugins/bundles/{pkg}/{version}", ct);
                await using var file = File.Create(zipPath);
                await body.CopyToAsync(file, ct);
            }
            catch (Exception ex)
            {
                await error.WriteLineAsync($"error: download failed for {pkg}@{version}: {ex.Message}");
                return null;
            }

            var unpack = Path.Combine(extDir, $"unpack-{pkg}");
            if (Directory.Exists(unpack)) Directory.Delete(unpack, true);
            System.IO.Compression.ZipFile.ExtractToDirectory(zipPath, unpack);
            File.Delete(zipPath);

            var manifest = Path.Combine(unpack, "meshweaver", "manifest.json");
            string? name = null;
            if (File.Exists(manifest))
                using (var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(manifest)))
                    if (doc.RootElement.TryGetProperty("module", out var m)
                        && m.TryGetProperty("assemblyName", out var an))
                        name = an.GetString();
            var modulesDir = Path.Combine(unpack, "meshweaver", "modules");
            if (name is not { Length: > 0 })
            {
                var dlls = Directory.Exists(modulesDir) ? Directory.GetFiles(modulesDir, "*.dll") : [];
                if (dlls.Length != 1)
                {
                    await error.WriteLineAsync(
                        $"error: {pkg}: cannot identify the entry assembly (no manifest "
                        + $"module.assemblyName; meshweaver/modules holds {dlls.Length} dll(s)).");
                    return null;
                }
                name = Path.GetFileNameWithoutExtension(dlls[0]);
            }
            if (!File.Exists(Path.Combine(modulesDir, $"{name}.dll")))
            {
                await error.WriteLineAsync($"error: {pkg}: meshweaver/modules/{name}.dll is missing from the bundle.");
                return null;
            }
            var target = Path.Combine(extDir, name);
            if (Directory.Exists(target)) Directory.Delete(target, true);
            Directory.Move(modulesDir, target);
            Directory.Delete(unpack, true);
            args.Add("--module"); args.Add($"/ext/{name}/{name}.dll");
            await output.WriteLineAsync($"external module composed: {name} ({pkg}@{version})");
        }
        return args;
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
        string? extDir,
        IEnumerable<string> mounts, IEnumerable<string> env, IEnumerable<string> args,
        CancellationToken ct)
    {
        var docker = new List<string> { "run", "--rm", "-v", $"{repo}:/repo" };
        foreach (var m in mounts) { docker.Add("-v"); docker.Add(m); }
        if (extDir is { Length: > 0 } ext)
        { docker.Add("-v"); docker.Add($"{ext}:/ext"); }
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
    // 🚨 init-properties, NOT primary-constructor parameters: adding a parameter to a record's
    // primary constructor changes the synthesized constructor's ARITY — a binary break the
    // public-surface gate rejects even though nothing was removed (the "adding is source-compatible
    // everywhere the compiler looks" trap, third sighting today). Same resolution as Seed's
    // overload in #2821: extend without touching the existing shape.
    /// <summary>Registry base URL for the dependency install (PluginBuildContract step 2).</summary>
    public string? RegistryUrl { get; init; }
    /// <summary>Space-separated packages to install as built artifacts before building.</summary>
    public string? RegistryModules { get; init; }
    /// <summary>The mwi_ instance key; prefer sourcing from $MW_REGISTRY_KEY.</summary>
    public string? RegistryKey { get; init; }

    public IReadOnlyList<string> ExtraArgs => Extra ?? [];
}
