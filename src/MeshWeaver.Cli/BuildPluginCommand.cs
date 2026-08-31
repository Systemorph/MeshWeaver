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

        // ── the upstream seed comes FIRST, because it is also where the modules come from ───────
        // Round 4's lesson: sourcing module DLLs from the package index takes the LATEST version,
        // while the sealed publication's types were built against the SEALED module mvids — 10
        // upstream types were DECLINED ('MeshWeaver.AI built against mvid:699963b6…, live is
        // 813bec7e…') because the composed module and the publication disagreed. The reusable gate
        // never has this skew: with an upstream seed, compose-sealed-modules takes the modules FROM
        // the publication, identity-matched. Same here — the identity is asked of the IMAGE
        // (--print-framework-identity, the gate's own pre-bake step), the seed is fetched before
        // stage 1, and the requested modules come from the publication's SEALED MODULE SET
        // (`…/prebuilt/{identity}/{source}/modules`, MeshWeaver#2698/#2707). Round 5's lesson: the
        // seed's own zips are the NODE-REPO content packs — no module bundle is among them, so
        // scanning them for 'AI'/'Essentials' refuses on every run. Only a build with NO upstream
        // seed falls back to the package index's latest.
        var seedDir = bake; // the gate consumes one dir: own bake + upstream bundles
        string? sealedIdentity = null;
        if (options.UpstreamSeed is { Length: > 0 } upstreams)
        {
            var line = await Capture("docker",
                ["run", "--rm", "--init", "--entrypoint", "/app/mw-plugin-test", pinned,
                 "--print-framework-identity"], ct);
            var idIdx = line.IndexOf("identity=", StringComparison.Ordinal);
            var frameworkIdentity = idIdx >= 0
                ? line[(idIdx + "identity=".Length)..].Split(' ', '\n', '\r')[0].Trim()
                : "";
            if (frameworkIdentity.Length == 0)
            {
                await error.WriteLineAsync(
                    $"error: could not resolve a framework identity from the image (got: '{line.Trim()}') "
                    + "— cannot address an upstream publication.");
                return 8;
            }
            await output.WriteLineAsync($"framework identity: {frameworkIdentity} (from the image)");
            sealedIdentity = frameworkIdentity;
            var seeded = await FetchUpstreamSeed(
                options.RegistryUrl!, options.RegistryKey!, upstreams, frameworkIdentity, seedDir, ct);
            if (!seeded) return 8;
        }

        // ── dependencies: INSTALL, never build (PluginBuildContract step 2) ─────────────────────
        // Module DLLs register the types a Skill/Agent subtree needs; without them the gate
        // installs files and registers ZERO NodeTypes (round 1's lesson).
        var moduleArgs = new List<string>();
        var extDir = options.ExternalModulesDir is { Length: > 0 } e ? Path.GetFullPath(e) : null;
        if (options is { RegistryModules.Length: > 0 })
        {
            extDir ??= Path.Combine(Path.GetTempPath(), $"memex-ext-{Environment.ProcessId}");
            var fetched = options.UpstreamSeed is { Length: > 0 } sources && sealedIdentity is { Length: > 0 }
                ? await ComposeSealedModules(
                    options.RegistryUrl!, options.RegistryKey!, sources, sealedIdentity,
                    options.RegistryModules!, extDir, ct)
                : await FetchRegistryModules(
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

            var name = await StageModuleBundle(zipPath, extDir, pkg);
            if (name is null) return null;
            args.Add("--module"); args.Add($"/ext/{name}/{name}.dll");
            await output.WriteLineAsync($"external module composed: {name} ({pkg}@{version})");
        }
        return args;
    }

    /// <summary>
    /// Unpacks one module bundle zip into <paramref name="extDir"/> in the tester's layout
    /// (<c>/ext/&lt;name&gt;/&lt;name&gt;.dll</c>): <c>meshweaver/manifest.json</c>'s
    /// <c>module.assemblyName</c> names the entry assembly (single-DLL fallback), the whole
    /// <c>meshweaver/modules/</c> directory moves under the assembly's name. Deletes the zip.
    /// Returns the assembly name, or null with the failure already reported against
    /// <paramref name="label"/>.
    /// </summary>
    private async Task<string?> StageModuleBundle(string zipPath, string extDir, string label)
    {
        var unpack = Path.Combine(extDir, $"unpack-{label}");
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
                    $"error: {label}: cannot identify the entry assembly (no manifest "
                    + $"module.assemblyName; meshweaver/modules holds {dlls.Length} dll(s)).");
                return null;
            }
            name = Path.GetFileNameWithoutExtension(dlls[0]);
        }
        if (!File.Exists(Path.Combine(modulesDir, $"{name}.dll")))
        {
            await error.WriteLineAsync($"error: {label}: meshweaver/modules/{name}.dll is missing from the bundle.");
            return null;
        }
        var target = Path.Combine(extDir, name);
        if (Directory.Exists(target)) Directory.Delete(target, true);
        Directory.Move(modulesDir, target);
        Directory.Delete(unpack, true);
        return name;
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

    /// <summary>
    /// Composes the requested module packages from the upstream publication's SEALED MODULE SET —
    /// <c>…/api/plugins/bundles/prebuilt/{identity}/{source}/modules</c> — the client half of the
    /// contract <c>.github/scripts/compose-sealed-modules.sh</c> speaks for the reusable gate
    /// (MeshWeaver#2698: the publication is the unit of consistency; #2707 seals the module set
    /// beside the bundles). The seed's own zips are the node-repo CONTENT packs; no module bundle
    /// is among them — round 5 proved that by refusing on every run — so module bytes must come
    /// from the module set the same seal carries, never from the registry's latest index (round
    /// 4's mvid declines) and never from a scan of the seed. A wanted package matches the bundle
    /// named <c>{package}.module.nupkg</c> case-insensitively; the first upstream listing it wins.
    /// Returns the <c>--module</c> args, or null with every failure already reported RED and named.
    /// </summary>
    private async Task<List<string>?> ComposeSealedModules(
        string registryUrl, string key, string upstreamSources, string identity,
        string modules, string extDir, CancellationToken ct)
    {
        Directory.CreateDirectory(extDir);
        var args = new List<string>();
        using var http = new HttpClient { BaseAddress = new Uri(registryUrl.TrimEnd('/') + "/") };
        http.DefaultRequestHeaders.Authorization = new("Bearer", key);

        var sources = upstreamSources.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var sealedSets = new List<(string Source, List<string> Bundles)>();
        foreach (var src in sources)
        {
            var url = $"api/plugins/bundles/prebuilt/{identity}/{src}/modules";
            using var response = await http.GetAsync(url, ct);
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                await error.WriteLineAsync(
                    $"error: upstream '{src}' answers 404 for the module set of identity {identity} — either "
                    + "it has no SEALED publication for this identity, or its publication predates module "
                    + $"sealing (no modules/_index). Republish '{src}' under a core that carries "
                    + "MeshWeaver#2707. Not composing from the registry instead: that is the decline this "
                    + "exists to end.");
                return null;
            }
            if (response.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden)
            {
                await error.WriteLineAsync(
                    $"error: the registry refused the instance key for '{src}' ({(int)response.StatusCode}) — "
                    + $"the instance needs a whole-source grant '{src}/*'.");
                return null;
            }
            if (!response.IsSuccessStatusCode)
            {
                await error.WriteLineAsync(
                    $"error: registry answered {(int)response.StatusCode} for {url} — refusing.");
                return null;
            }
            List<string> bundles;
            try
            {
                using var doc = await System.Text.Json.JsonDocument.ParseAsync(
                    await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
                if (!doc.RootElement.TryGetProperty("modules", out var arr)
                    || arr.ValueKind != System.Text.Json.JsonValueKind.Array)
                {
                    await error.WriteLineAsync(
                        $"error: {url} answered 200 but not a module-set index — refusing to guess.");
                    return null;
                }
                bundles = arr.EnumerateArray()
                    .Select(e => e.GetString())
                    .Where(n => n is { Length: > 0 })
                    .Select(n => n!)
                    .ToList();
            }
            catch (System.Text.Json.JsonException ex)
            {
                await error.WriteLineAsync($"error: {url} answered 200 but not JSON: {ex.Message}");
                return null;
            }
            sealedSets.Add((src, bundles));
            await output.WriteLineAsync(
                $"upstream '{src}' sealed {bundles.Count} module bundle(s) for identity {identity}");
        }

        foreach (var pkg in modules.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var wanted = $"{pkg.ToLowerInvariant()}.module.nupkg";
            var hit = sealedSets
                .Select(set => (set.Source, Name: set.Bundles.FirstOrDefault(
                    b => string.Equals(b, wanted, StringComparison.OrdinalIgnoreCase))))
                .FirstOrDefault(x => x.Name is not null);
            if (hit.Name is null)
            {
                await error.WriteLineAsync(
                    $"error: no sealed upstream publication ({upstreamSources}) for identity {identity} carries "
                    + $"module package '{pkg}' (looked for {wanted}). The upstream that owns it must compose it "
                    + "in its bake (module-artifacts / registry-modules) so its seal carries it; composing it "
                    + "from anywhere else would carry the wrong mvid and every publication type built against "
                    + "it would be DECLINED.");
                return null;
            }
            var zipPath = Path.Combine(extDir, hit.Name);
            try
            {
                await using var body = await http.GetStreamAsync(
                    $"api/plugins/bundles/prebuilt/{identity}/{hit.Source}/modules/{hit.Name}", ct);
                await using var file = File.Create(zipPath);
                await body.CopyToAsync(file, ct);
            }
            catch (Exception ex)
            {
                await error.WriteLineAsync(
                    $"error: could not fetch sealed module {hit.Name} of '{hit.Source}' for identity {identity}: {ex.Message}");
                return null;
            }
            var name = await StageModuleBundle(zipPath, extDir, pkg);
            if (name is null) return null;
            args.Add("--module"); args.Add($"/ext/{name}/{name}.dll");
            await output.WriteLineAsync(
                $"external module composed from the SEALED publication of '{hit.Source}': {name} ({pkg}, {hit.Name})");
        }
        return args;
    }

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
    /// <summary>Space-separated upstream SOURCES whose sealed publication seeds the gate's mesh
    /// (e.g. "plugins") — the packages a `requires` chain reaches, not merely their DLLs.</summary>
    public string? UpstreamSeed { get; init; }

    public IReadOnlyList<string> ExtraArgs => Extra ?? [];
}
