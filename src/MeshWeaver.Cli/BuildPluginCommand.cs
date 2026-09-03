
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
///
/// <para><b>Two IMAGES, and that split is deliberate too (#3022 for the lanes, #3113 here).</b> The
/// TESTER image executes; the PLATFORM (portal) image supplies the reference set, the framework
/// identity and the runtime. Both stages run from a host composed of the portal's <c>/app</c> with
/// the tester CLI beside it — see <see cref="GateHost"/> for what the tester's subset <c>/app</c>
/// cannot compile and why there is no fallback to it.</para>
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
        // Two /app extractions plus the composed host is roughly a gigabyte per invocation, and
        // the path carries this process's id — so a later run never reuses it and stale copies
        // accumulate until the disk is full. The bake is deliberately NOT in here: the caller asked
        // for it and may well read it after this returns.
        var hostRoot = Path.Combine(Path.GetTempPath(), $"memex-host-{Environment.ProcessId}");
        try
        {
            return await RunCoreAsync(options, hostRoot, ct);
        }
        finally
        {
            await GateHost.DiscardTree(hostRoot, output);
        }
    }

    private async Task<int> RunCoreAsync(
        BuildPluginOptions options, string hostRoot, CancellationToken ct)
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

        // ── the PLATFORM image is REQUIRED, and there is no fallback to the tester's /app ────────
        // 🚨 #3113. Composing only the tester's /app is not "backwards compatible", it is the
        // defect: content that binds a portal-shipped assembly cannot compile against a strict
        // subset of the portal's reference set, and the failure reads as a CONTENT error on source
        // nobody changed. node-repo-gate.yml refuses the same way (`platform-image-digest is empty
        // and allow-unpinned is not set … There is no fallback to the tester's /app`), so this
        // refuses rather than silently compiling against the wrong reference set.
        if (options.PlatformImage is not { Length: > 0 })
        {
            await error.WriteLineAsync(
                "error: --platform-image is required — pass the PORTAL image "
                + "(e.g. meshweaver.azurecr.io/memex-portal-ai@sha256:…, pinned from the SAME CD wave "
                + "as --image). The tester image only EXECUTES; the portal supplies the reference "
                + "set, the framework identity and the runtime. There is no fallback to the tester's "
                + "/app: it is a strict subset of the portal's (88 vs 219 assemblies on "
                + "3.0.0-rc9.ci.7534), so content binding a portal-only assembly fails to "
                + "compile against it (MeshWeaver#3022 / #3113).");
            return 9;
        }
        if (GateHost.NamesTheTesterImage(options.PlatformImage))
        {
            await error.WriteLineAsync(
                $"error: --platform-image names the TESTER image ('{options.PlatformImage}') — pass "
                + "the PORTAL image (meshweaver.azurecr.io/memex-portal-ai); the tester only executes. "
                + "Accepting it would silently restore the reference-set gap this argument closes.");
            return 9;
        }

        // Pin by DIGEST once, and use it for both stages. The workflow this replaces already did
        // this (`${IMAGE%%:*}@${DIGEST}`) for a reason: a tag can move between the compile and the
        // gate, and then the bytes tested are not the bytes baked — which is the one claim the
        // two-stage split exists to make.
        var pinned = await _runner.PullImage(options.Image, ct);
        if (pinned is null) return 4;
        await output.WriteLineAsync($"tester image: {pinned}");
        var platform = await _runner.PullImage(options.PlatformImage, ct);
        if (platform is null) return 4;
        await output.WriteLineAsync($"platform image: {platform}");

        // ── the composed GATE HOST: the portal's /app with the tester CLI beside it ──────────────
        // (hostRoot is created by RunAsync, which also removes it however this returns.)
        var testerApp = Path.Combine(hostRoot, "tester-app");
        var portalApp = Path.Combine(hostRoot, "portal-app");
        var gateHost = Path.Combine(hostRoot, "gate-host");
        if (!await _runner.ExtractApp(pinned, testerApp, ct)) return 10;
        if (!await _runner.ExtractApp(platform, portalApp, ct)) return 10;
        if (!File.Exists(Path.Combine(testerApp, GateHost.TesterCli)))
        {
            await error.WriteLineAsync(
                $"error: {pinned} ships no /app/{GateHost.TesterCli} — --image must be the "
                + "mw-plugin-test image.");
            return 10;
        }
        var manifest = new FileInfo(Path.Combine(portalApp, GateHost.SurfaceManifest));
        if (!manifest.Exists || manifest.Length == 0)
        {
            await error.WriteLineAsync(
                $"error: {platform} ships no /app/{GateHost.SurfaceManifest} — --platform-image must "
                + "be the PORTAL image; a host without one resolves the fallback identity no bake may "
                + "be keyed to.");
            return 10;
        }
        await output.WriteLineAsync(
            $"tester /app: {Directory.GetFiles(testerApp, "*.dll").Length} assemblies; "
            + $"platform /app: {Directory.GetFiles(portalApp, "*.dll").Length} assemblies");

        // 🚨 ONE BUILD, ASSERTED. The bake is keyed to the PORTAL's identity and executed by the
        // TESTER's toolchain, which is honest only when the two images are one build. The tester's
        // own `framework-identity` verb reads the portal's /app as FILES and compares — naming the
        // canonical assemblies each side lacks on a mismatch (#1814) — and it never degrades to a
        // fallback identity, so this comparison cannot pass on a manifest-less directory.
        var (testerRc, testerLine) = await _runner.CaptureVerbose("docker",
            ["run", "--rm", "--init", "--entrypoint", "/app/mw-plugin-test", pinned,
             "--print-framework-identity"], ct);
        var idIdx = testerLine.IndexOf("identity=", StringComparison.Ordinal);
        var testerIdentity = testerRc == 0 && idIdx >= 0
            ? testerLine[(idIdx + "identity=".Length)..].Split(' ', '\n', '\r')[0].Trim()
            : "";
        if (testerIdentity.Length == 0)
        {
            await error.WriteLineAsync(
                $"error: could not resolve the tester image's framework identity (got: '{testerLine.Trim()}').");
            return 10;
        }
        var (identityRc, identityOut) = await _runner.CaptureVerbose("docker",
            ["run", "--rm", "--init", "-v", $"{portalApp}:/portal:ro",
             "--entrypoint", "/app/mw-plugin-test", pinned,
             "framework-identity", "/portal", "--expect", testerIdentity], ct);
        var frameworkIdentity = identityOut
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .LastOrDefault() ?? "";
        if (identityRc != 0 || frameworkIdentity.Length == 0)
        {
            await error.WriteLineAsync(
                "error: the tester image and the platform image do NOT resolve one framework identity "
                + "— they are not one build (the verb's verdict above names the canonical assemblies "
                + "each side lacks). Pin --image and --platform-image from the SAME CD wave.");
            return 10;
        }
        await output.WriteLineAsync(
            $"framework identity: {frameworkIdentity} (the platform's; the tester resolves the same)");

        // The composition rules are the LANES' rules — one implementation, extracted and run, never
        // reimplemented here. It fails closed and names the file it is missing.
        var script = GateHost.ExtractComposeScript(hostRoot);
        var composed = await _runner.Exec("bash", [script, portalApp, testerApp, gateHost], ct);
        if (composed != 0)
        {
            await error.WriteLineAsync(
                composed == 127
                    ? $"error: could not run {GateHost.ComposeScriptName} — 'bash' is not on PATH. "
                      + "Composing the gate host is not optional: without it the build has no portal "
                      + "reference set."
                    : $"error: composing the gate host failed (exit {composed}) — the refusal above "
                      + "names the file that was missing.");
            return 10;
        }
        // The two extractions are CONSUMED by the composition above (and by the identity check
        // before it); only the composed host is mounted from here on. Dropping them now keeps peak
        // disk at one copy instead of three across the compile and the gate, which are the long
        // stages — the finally in RunAsync is the backstop, not the whole answer.
        await GateHost.DiscardTree(portalApp, output);
        await GateHost.DiscardTree(testerApp, output);

        var invokingUser = await _runner.ResolveInvokingUser(ct);

        var bake = options.BakeOutput is { Length: > 0 }
            ? Path.GetFullPath(options.BakeOutput)
            : Path.Combine(Path.GetTempPath(), $"memex-bake-{Environment.ProcessId}");
        Directory.CreateDirectory(bake);
        // 🚨 The container may run as a user this host never provisioned, so the bake mount must be
        // writable by it — the `chmod 777 "$OWN_BAKE"` every docker-run gate performs. Proven the
        // hard way on the verb's first production run (Manufacturing #37): 15/15 NodeTypes compiled, then
        // `UnauthorizedAccessException: Access to the path '/bake/Manufacturing.zip' is denied`
        // writing the first bundle. Applied to a caller-supplied directory too: the caller asked
        // for a bake THERE, and a mount the container cannot write is a promise this command
        // cannot keep. Windows has no unix mode and no such non-root docker convention; skip.
        ImageRunner.MakeContainerWritable(bake);

        // ── the upstream seed comes FIRST, because it is also where the modules come from ───────
        // Round 4's lesson: sourcing module DLLs from the package index takes the LATEST version,
        // while the sealed publication's types were built against the SEALED module mvids — 10
        // upstream types were DECLINED ('MeshWeaver.AI built against mvid:699963b6…, live is
        // 813bec7e…') because the composed module and the publication disagreed. The reusable gate
        // never has this skew: with an upstream seed, compose-sealed-modules takes the modules FROM
        // the publication, identity-matched. Same here — the seed is fetched before stage 1, and the
        // requested modules come from the publication's SEALED MODULE SET
        // (`…/prebuilt/{identity}/{source}/modules`, MeshWeaver#2698/#2707). Round 5's lesson: the
        // seed's own zips are the NODE-REPO content packs — no module bundle is among them, so
        // scanning them for 'AI'/'Essentials' refuses on every run. Only a build with NO upstream
        // seed falls back to the package index's latest.
        //
        // 🚨 The address is the PLATFORM's identity, resolved above from the portal's /app — not the
        // tester's. The bake this run produces is keyed to the host it compiles on, and a seed
        // fetched under the tester's identity would address a publication this gate never adopts.
        var seedDir = bake; // the gate consumes one dir: own bake + upstream bundles
        if (options.UpstreamSeed is { Length: > 0 } upstreams)
        {
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
            var fetched = options.UpstreamSeed is { Length: > 0 } sources
                ? await ComposeSealedModules(
                    options.RegistryUrl!, options.RegistryKey!, sources, frameworkIdentity,
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
        if (extDir is not null && Directory.Exists(extDir))
            ImageRunner.MakeContainerWritable(extDir);

        // ── stage 1: PRODUCE ───────────────────────────────────────────────────────────────────
        // Module args go to BOTH stages: the compile needs the externals to RESOLVE their types,
        // the gate needs them to REGISTER them (the node-repo gate passes them to both for exactly
        // this reason, and dropping either half reintroduces one of the two failures).
        //
        // 🚨 `--app /app --shared-frameworks …` makes the reference set the PLATFORM's /app plus its
        // IMPLEMENTATION frameworks — what a portal's runtime compile sees — and keys the bake to the
        // portal's identity. Same shape as node-repo-gate.yml, on purpose: the CLI and the lanes must
        // not disagree about what a portal contains.
        var compile = await RunOnHost(platform, gateHost, invokingUser, repo, extDir,
            mounts: [$"{bake}:/bake"],
            env: [],
            args: ["compile", "/repo", "--app", "/app",
                   "--shared-frameworks", GateHost.SharedFrameworks,
                   ..AllowArgs(repo, options), ..moduleArgs, ..options.ExtraArgs,
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
        var baked = File.ReadAllText(identity).Trim();
        if (!string.Equals(baked, frameworkIdentity, StringComparison.Ordinal))
        {
            await error.WriteLineAsync(
                $"error: the run's own bake is keyed to '{baked}' but the platform's /app resolves "
                + $"'{frameworkIdentity}' — the compile did not run against the platform host. Bundles "
                + "published under an identity no portal asks for are INERT.");
            return 5;
        }

        // ── stage 2: CONSUME — stand a mesh up on the bytes stage 1 produced ───────────────────
        // `--app /app`: the gate must RUN AS the platform host (it resolves the portal's identity),
        // and is refused before a mesh boots otherwise — a gate running as another host would decline
        // every bundle and compile the tree itself, passing without judging the bytes that ship.
        return await RunOnHost(platform, gateHost, invokingUser, repo, extDir,
            mounts: [$"{bake}:/seed:ro"],
            env: ["MW_INSTALL_DIFF=1"],
            args: ["/repo", "--app", "/app", ..AllowArgs(repo, options), ..moduleArgs,
                   ..options.ExtraArgs, "--seed", "/seed"],
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

    /// <summary>
    /// Runs the tester CLI out of the composed gate host, inside the PLATFORM image, with this
    /// verb's standing mounts (<c>/repo</c> and, when there are external modules, <c>/ext</c>).
    /// </summary>
    private Task<int> RunOnHost(
        string platformImage, string hostDirectory, string? user, string repo,
        string? extDir,
        IEnumerable<string> mounts, IEnumerable<string> env, IEnumerable<string> args,
        CancellationToken ct)
    {
        var all = new List<string> { $"{repo}:/repo" };
        all.AddRange(mounts);
        if (extDir is { Length: > 0 } ext)
            all.Add($"{ext}:/ext");
        return _runner.RunOnComposedHost(platformImage, hostDirectory, user, all, env, args, ct);
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

    /// <summary>
    /// The PORTAL image — REQUIRED (#3113). It supplies the reference set, the framework identity
    /// and the runtime; <see cref="Image"/> only executes. There is deliberately no default and no
    /// fallback to the tester's <c>/app</c>: see <see cref="GateHost"/>.
    /// </summary>
    public string? PlatformImage { get; init; }

    public IReadOnlyList<string> ExtraArgs => Extra ?? [];
}
