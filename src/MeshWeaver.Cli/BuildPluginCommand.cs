
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
    /// <summary>The docker plumbing this verb shares with <c>build project</c>: the pull
    /// retry, the digest pin, and the process launch. One copy, so the pin cannot drift.</summary>
    private readonly ImageRunner _runner = new(output, error);

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

        // Argument validation happens BEFORE any docker work, so a misconfigured job fails in
        // seconds naming the input — not minutes later inside a container. (The first version
        // checked --upstream-seed after stage 1, and the 'proof' of its refusal path turned out
        // to have died at the container entrypoint before ever reaching the check.)
        if ((options.RegistryModules is { Length: > 0 } || options.UpstreamSeed is { Length: > 0 })
            && (options.RegistryUrl is not { Length: > 0 } || options.RegistryKey is not { Length: > 0 }))
        {
            await error.WriteLineAsync(
                "error: --registry-modules / --upstream-seed need --registry-url and a key "
                + "(MW_REGISTRY_KEY or --registry-key). A dependency install or seed that silently "
                + "skips is a gate that tests nothing, so this refuses rather than proceeding.");
            return 6;
        }

        // Pin by DIGEST once, and use it for both stages. The workflow this replaces already did
        // this (`${IMAGE%%:*}@${DIGEST}`) for a reason: a tag can move between the compile and the
        // gate, and then the bytes tested are not the bytes baked — which is the one claim the
        // two-stage split exists to make.
        var pinned = await _runner.PullImage(options.Image, ct);
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
        ImageRunner.MakeContainerWritable(bake);

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
        if (extDir is not null && Directory.Exists(extDir))
            ImageRunner.MakeContainerWritable(extDir);

        // ── stage 1: PRODUCE ───────────────────────────────────────────────────────────────────
        // Module args go to BOTH stages: the compile needs the externals to RESOLVE their types,
        // the gate needs them to REGISTER them (the node-repo gate passes them to both for exactly
        // this reason, and dropping either half reintroduces one of the two failures).
        var compile = await RunInImage(pinned, repo, extDir,
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

        // ── upstream seed: the PUBLICATION the gate's fresh mesh installs first ─────────────────
        // Round 3's lesson: module DLLs are necessary and NOT sufficient. The gate boots a fresh
        // mesh, and a package whose `requires` chain reaches upstream PACKAGES needs those
        // packages' bundles in the seed, or its own installs register nothing (Manufacturing:
        // 162 files installed, 0 NodeTypes — the working test-repos gate consumes 44 bundles,
        // this command's seed held 1). Fetched INTO the bake dir, whose framework-mvid.txt is by
        // construction the identity the publication must be sealed for.
        if (options.UpstreamSeed is { Length: > 0 } upstreams)
        {
            var frameworkIdentity = (await File.ReadAllTextAsync(identity, ct)).Trim();
            var seeded = await FetchUpstreamSeed(
                options.RegistryUrl!, options.RegistryKey!, upstreams, frameworkIdentity, bake, ct);
            if (!seeded) return 8;
        }

        // ── stage 2: CONSUME — stand a mesh up on the bytes stage 1 produced ───────────────────
        return await RunInImage(pinned, repo, extDir,
            mounts: [$"{bake}:/seed:ro"],
            env: ["MW_INSTALL_DIFF=1"],
            args: ["/repo", ..AllowArgs(repo, options), ..moduleArgs, ..options.ExtraArgs, "--seed", "/seed"],
            ct);
    }

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
    /// Fetches each upstream source's SEALED publication for <paramref name="frameworkIdentity"/>
    /// into <paramref name="seedDir"/> — the client half of the registry's
    /// <c>prebuilt/{identity}/{source}</c> route, byte-for-byte the node-repo gate's shell:
    /// index (<c>{"bundles":[…]}</c>) → each file → refuse a 404 (no sealed publication), a
    /// 401/403 (the key needs a whole-source grant '<c>src/*</c>'), and an EMPTY seed — sealed
    /// but empty is refused, never treated as "nothing to install".
    /// </summary>
    private async Task<bool> FetchUpstreamSeed(
        string registryUrl, string key, string upstreams, string frameworkIdentity,
        string seedDir, CancellationToken ct)
    {
        using var http = new HttpClient { BaseAddress = new Uri(registryUrl.TrimEnd('/') + "/") };
        http.DefaultRequestHeaders.Authorization = new("Bearer", key);
        var fetched = 0;
        foreach (var src in upstreams.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var basePath = $"api/plugins/bundles/prebuilt/{frameworkIdentity}/{src}";
            using var resp = await http.GetAsync(basePath, ct);
            if ((int)resp.StatusCode == 404)
            {
                await error.WriteLineAsync(
                    $"error: upstream '{src}' has no SEALED publication on {registryUrl} for identity "
                    + $"{frameworkIdentity}. Not compiling it instead.");
                return false;
            }
            if ((int)resp.StatusCode is 401 or 403)
            {
                await error.WriteLineAsync(
                    $"error: the registry refused the key for '{src}' ({(int)resp.StatusCode}) — the "
                    + $"instance needs a whole-source grant '{src}/*'.");
                return false;
            }
            if (!resp.IsSuccessStatusCode)
            {
                await error.WriteLineAsync($"error: registry answered {(int)resp.StatusCode} for {basePath} — refusing.");
                return false;
            }
            System.Text.Json.JsonDocument doc;
            try { doc = System.Text.Json.JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct)); }
            catch
            {
                await error.WriteLineAsync($"error: {basePath} answered 200 but not a publication index.");
                return false;
            }
            using (doc)
            {
                if (!doc.RootElement.TryGetProperty("bundles", out var bundles)
                    || bundles.ValueKind != System.Text.Json.JsonValueKind.Array)
                {
                    await error.WriteLineAsync($"error: {basePath} answered 200 but not a publication index.");
                    return false;
                }
                foreach (var nameEl in bundles.EnumerateArray())
                {
                    var name = nameEl.GetString();
                    if (name is not { Length: > 0 }) continue;
                    // Path-safety: the index names files, never paths — a traversal here would let
                    // a registry write outside the seed.
                    if (name.Contains('/') || name.Contains('\\') || name.Contains(".."))
                    {
                        await error.WriteLineAsync($"error: publication index for '{src}' names an unsafe path '{name}' — refusing.");
                        return false;
                    }
                    await using var body = await http.GetStreamAsync($"{basePath}/{name}", ct);
                    await using var file = File.Create(Path.Combine(seedDir, name));
                    await body.CopyToAsync(file, ct);
                    fetched++;
                }
            }
            await output.WriteLineAsync($"upstream '{src}': fetched from {registryUrl}");
        }
        if (fetched == 0)
        {
            await error.WriteLineAsync("error: sealed but empty — refusing an empty seed.");
            return false;
        }
        return true;
    }

    private Task<int> RunInImage(
        string image, string repo,
        string? extDir,
        IEnumerable<string> mounts, IEnumerable<string> env, IEnumerable<string> args,
        CancellationToken ct)
    {
        var all = new List<string> { $"{repo}:/repo" };
        all.AddRange(mounts);
        if (extDir is { Length: > 0 } ext)
            all.Add($"{ext}:/ext");
        return _runner.RunInImage(image, all, env, args, ct);
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
    /// <summary>Space-separated upstream SOURCES whose sealed publication seeds the gate's mesh
    /// (e.g. "plugins") — the packages a `requires` chain reaches, not merely their DLLs.</summary>
    public string? UpstreamSeed { get; init; }

    public IReadOnlyList<string> ExtraArgs => Extra ?? [];
}
