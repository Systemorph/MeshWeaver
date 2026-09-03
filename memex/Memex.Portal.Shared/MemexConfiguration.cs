using System.IdentityModel.Tokens.Jwt;
using Memex.Portal.Shared.Api;
using Memex.Portal.Shared.Authentication;
using Memex.Portal.Shared.Email;
using Memex.Portal.Shared.SelfUpdate;
using Memex.Portal.Shared.Settings;
using Memex.Portal.Shared.Social;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MeshWeaver.Hosting.AspNetCore.Portal;
using MeshWeaver.ContentCollections;
using MeshWeaver.Documentation;
using MeshWeaver.Data;
using MeshWeaver.GitSync;
using MeshWeaver.Graph;
using MeshWeaver.PluginCatalog;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting;
using MeshWeaver.Hosting.AspNetCore;
using MeshWeaver.Hosting.Persistence;
using MeshWeaver.Hosting.Security;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Mesh.Threading;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Identity.Web;
using Microsoft.Identity.Web.UI;

namespace Memex.Portal.Shared;

public static class MemexConfiguration
{
    /// <summary>
    /// Conditional fluent step: applies <paramref name="apply"/> only when
    /// <paramref name="condition"/> holds — keeps feature-flagged registrations readable inside
    /// long builder chains.
    /// </summary>
    public static T If<T>(this T value, bool condition, Func<T, T> apply)
        => condition ? apply(value) : value;


    /// <summary>
    /// Says out loud, once at startup, which ACTIVATED modules did not host-load here (#2093).
    ///
    /// <para>🚨 <b>Why this cannot be left to the module's own code.</b> A module that does not load
    /// runs nothing — including anything that would have complained. <c>MapMeshModuleEndpoints</c>
    /// scans only LOADED assemblies, so an activated endpoint provider that never made it into the
    /// process contributes no routes and its whole HTTP surface answers 404 for the pod's lifetime,
    /// with no exception, no warning and nothing to grep. On memex.systemorph that was <c>/mcp</c>,
    /// dead through two clean rolling restarts while <c>/health</c> and <c>/readyz</c> were 200 and
    /// the activation record cheerfully listed the module as installed. Absence of evidence read as
    /// evidence of absence, again.</para>
    ///
    /// <para>The two cases are reported differently because the remedies are: a module whose bytes
    /// are on the volume is one restart from working, while a module whose landed assembly is GONE
    /// is a half-completed landing that no restart repairs — that one is an ERROR naming the
    /// re-install. Same reader, same wording, as the <c>pending_module_activation</c> health check,
    /// so the pod log and the probe can never disagree.</para>
    /// </summary>
    private static void ReportUnloadedActivatedModules(WebApplication app)
    {
        var logger = app.Services.GetRequiredService<ILoggerFactory>()
            .CreateLogger("MeshWeaver.PluginCatalog.ModuleActivation");
        // 🚨 CONSTRUCTED, not resolved — deliberately. PendingModuleActivations is registered in
        // the MESH container (AddPluginCatalog), which is why every other caller reaches it through
        // `hub.ServiceProvider` and guards with GetService. Asking `app.Services` for it would
        // throw at startup on any host where the two containers differ — a diagnostic that CRASHES
        // the portal it exists to inform is worse than the silence it replaces. Constructing costs
        // nothing and cannot differ: the reader is a stateless file reader that starts nothing and
        // writes nothing, and the registration is this same one-liner over the same resolved
        // module root.
        var report = new PendingModuleActivations(app.Configuration).Read();

        if (report.IsUndetermined)
        {
            logger.LogError(
                "Module endpoint contributions mapped, but this pod cannot say whether an "
                + "activated module failed to load: {Reason}", report.Describe());
            return;
        }

        if (report.HasUnresolvable)
            logger.LogError(
                "🚨 {Count} ACTIVATED module(s) are not loaded in this process and NO RESTART will "
                + "load them — any HTTP endpoint, view or provider they contribute is silently "
                + "absent (a 404 with no error) until the package is re-installed: {Detail}",
                report.Unresolvable.Count,
                ModuleActivationStatus.DescribeUnresolvable(report.Unresolvable));

        if (report.HasPending)
            logger.LogWarning(
                "{Count} activated module(s) are landed but not loaded in this process — whatever "
                + "they contribute (endpoints included) is absent until a restart: {Detail}",
                report.Pending.Count, ModuleActivationStatus.Describe(report.Pending));
    }

    /// <summary>
    /// Fails fast on the content-storage configuration that GUARANTEES silent data loss (issue #435):
    /// a DEPLOYED (non-development) <c>FileSystem</c> content store whose <c>BasePath</c> is empty or
    /// relative. Such a path resolves against the container's ephemeral working directory (<c>/app</c>),
    /// so every uploaded collection file is written to disk that vanishes on the next pod restart or
    /// grain teardown — reads succeed for minutes, then the files are gone, with no signal to the user.
    /// We cannot verify from code that an <em>absolute</em> path is a durable mount, but we CAN reject
    /// the empty/relative footgun outright. A local <c>Development</c> run keeps the relative-to-working-
    /// tree convenience (the Monolith's <c>Storage:BasePath = "../../samples/Graph"</c>).
    /// <para>Pure decision (no I/O) so it is unit-testable without spinning a mesh.</para>
    /// </summary>
    /// <param name="contentStorageConfig">The parsed <c>Storage</c> section, or <c>null</c> when unconfigured.</param>
    /// <param name="isDevelopment"><c>true</c> for a local Development run (relative BasePath allowed).</param>
    /// <exception cref="InvalidOperationException">
    /// Thrown when a non-development FileSystem content store has an empty or relative <c>BasePath</c>.
    /// </exception>

    public static void ValidateContentStorageDurability(
        ContentCollectionConfig? contentStorageConfig, bool isDevelopment)
    {
        // Development resolves a relative BasePath against a stable working tree — that's the intended
        // local convenience, not the ephemeral-container footgun. Nothing configured → nothing to guard.
        if (isDevelopment || contentStorageConfig is null)
            return;
        // Only the FileSystem store roots files on a local path. AzureBlob/other stores don't use
        // BasePath as a filesystem root (it's a blob prefix), so the durability concern doesn't apply.
        if (!string.Equals(contentStorageConfig.SourceType, "FileSystem", StringComparison.OrdinalIgnoreCase))
            return;

        var basePath = contentStorageConfig.BasePath;
        var isEmpty = string.IsNullOrWhiteSpace(basePath);
        // An absolute path is the operator's chosen mount; code can't verify it's durable, so allow it.
        if (!isEmpty && Path.IsPathRooted(basePath))
            return;

        throw new InvalidOperationException(
            "Content storage misconfiguration (issue #435): Storage:SourceType is 'FileSystem' but "
            + (isEmpty ? "Storage:BasePath is empty." : $"Storage:BasePath ('{basePath}') is relative.")
            + " A FileSystem content store with an empty or relative BasePath resolves against the "
            + "container's ephemeral working directory, so uploaded collection files are written to "
            + "storage that is SILENTLY LOST on the next pod restart or grain teardown. Set "
            + "Storage:BasePath to an ABSOLUTE path backed by a durable volume (e.g. '/mnt/content' on "
            + "a PersistentVolumeClaim), or use Storage:SourceType 'AzureBlob'. Refusing to start so the "
            + "misconfiguration surfaces now rather than after users have uploaded and lost files.");
    }


    extension<TBuilder>(TBuilder builder) where TBuilder : MeshBuilder
    {
        /// <summary>
        /// Configures the mesh with Graph domain only.
        ///
        /// Configuration is read from appsettings:
        /// - Graph:Storage:Type - Storage type: "FileSystem", "AzureBlob", "PostgreSql", "Cosmos"
        ///   or "Snowflake". Cosmos and Snowflake are BOOT PACKS: their factories register only
        ///   when the matching DLL (MeshWeaver.Hosting.Cosmos / .Snowflake) is listed under
        ///   Modules:Assemblies — installation runs before this selection, so ordering is safe.
        /// - Graph:Storage:BasePath - Base path for FileSystem storage
        /// - Graph:Storage:ConnectionString - Connection string for AzureBlob/Cosmos
        /// - storage - Content collection configuration (Name, SourceType, BasePath)
        /// </summary>
        public TBuilder ConfigureMemexMesh(IConfiguration configuration, bool isDevelopment = false)
        {
            // Boot-time module packs: DLL paths listed under Modules:Assemblies are loaded into
            // the default ALC BEFORE the container builds, and their MeshNodeProviderAttribute
            // registrations (services + nodes + hub configuration) fold into this mesh — the
            // per-deployment "which packs does this instance run" knob (Doc/Architecture/
            // UiExtensibility). Empty/absent = no-op; a listed path that fails to load should
            // fail loudly at startup, never silently run without the pack.
            //
            // #1664 step 9 — the effective set is the appsettings baseline ∪ the ENABLED entries
            // of the modules/activation.json sidecar (store-installed modules landed by
            // ModuleLandingService), deduped by name. Sidecar entries are guarded: a declared
            // minMeshVersion FLOOR the running platform no longer satisfies (a rollback below the
            // module's requirement) or a missing DLL SKIPS the entry with a loud stderr line —
            // never a crash, the deployment must boot; the entry stays for when the platform
            // moves forward again. A landed module's built-against framework identity is not a
            // LOAD gate HERE — modules bind by simple name across platform builds, and the strict
            // identity gate is the NodeType bake lane's. (It is not merely diagnostic either, since
            // #3154: ModuleUpdateDecision compares it to tell a rebuild from a no-op, and #3211
            // makes a bundle that states none unpublishable. That is the UPDATE question, not this
            // one.) Pre-DI, so diagnostics go to stderr (pod stdout/stderr ship
            // to Loki regardless).
            var moduleAssemblies = configuration.GetSection("Modules:Assemblies").Get<string[]>();
            // 🚨 The SAME root ModuleLandingService writes (ModuleRoot) — never
            // AppContext.BaseDirectory directly. They must name one directory: a landed module
            // read from somewhere else is simply invisible, and on a deployment whose /app is
            // read-only the writer cannot use AppContext.BaseDirectory at all.
            var moduleRoot = ModuleRoot.Resolve(configuration);

            // The instance manifest (#2550) — read BEFORE anything else needs it, on the same
            // writable root the module activation sidecar lives on, and for the same reason it is
            // a file: at this point there is no storage provider, hub or connection string, and
            // WHICH STORAGE TO USE is precisely what it answers.
            //
            // A corrupt manifest resolves to InstanceManifest.Unreadable rather than null: it
            // EXISTS, so the instance is not treated as never-configured, and it answers nothing,
            // so it cannot leave setup. Offering a fresh setup over a database that already holds
            // data is the failure that distinction prevents.
            var setupManifest = InstanceManifest.Read(moduleRoot,
                msg => Console.Error.WriteLine($"[InstanceSetup] {msg}"));
            // Generations GC — delete only what NO activation entry references and nothing holds
            // open (skip-on-locked). Landing never deletes anything on a shared volume; this is
            // the one reclaim point. See ModuleLandingService.CollectGarbage.
            //
            // 🚨 Registered, NEVER run here (#2684). It used to be a synchronous call at this exact
            // spot — before the host listened — and on an Azure Files (CIFS) /data the
            // rename-then-recursive-delete of orphaned generations is one SMB round-trip per file:
            // minutes of uninterruptible IO (PID 1 in `Dsl` at wchan=wait_for_response), no
            // listener on :8080, so the 300 s startup probe killed a pod the kernel could not even
            // deliver the kill to, and the roll looped (memex-cloud, ci.6559). Housekeeping must
            // not gate readiness: the hosted service runs the SAME pass, with the SAME semantics,
            // after ApplicationStarted, through the file-system IIoPool. It stays ahead of the
            // awaiting-setup early return below on purpose — an instance parked in setup still
            // reclaims its garbage, exactly as the synchronous call did.
            builder.ConfigureServices(services => services.AddModuleGenerationsGc(moduleRoot));
            var persistedActivation = ModuleActivationSidecar.Read(moduleRoot,
                msg => Console.Error.WriteLine($"[ModuleActivation] {msg}"));
            var effectiveModules = ModuleActivationBoot.ComputeEffectiveModuleEntries(
                moduleAssemblies,
                persistedActivation,
                // The ONE module platform gate (ModulePlatformFloor) — never a second notion of
                // the module platform requirement.
                ModulePlatformFloor.DeclineReason,
                // 🚨 The entry's OWN landed directory SPECIFICALLY — modules/<Directory ?? name>/
                // <name>.dll — never ResolveModulePath, whose BaseDirectory fallback would let a
                // sidecar entry with a lost modules/ folder silently bind a same-named app-closure
                // DLL instead of being skipped. Baseline entries below keep ResolveModulePath (both
                // locations are legitimate for them). Passing the ENTRY rather than the name is
                // what makes this gate agree with the resolver below: landing writes generations
                // (modules/<name>@<gen>/) and moves the entry pointer, so a name-only check found
                // nothing for ANY generation-landed module and boot skipped every store module on
                // the deployment while its bytes sat correctly on disk (#1949).
                entry => ModuleActivationBoot.LandedModuleDllExists(moduleRoot, entry),
                (module, reason) => Console.Error.WriteLine(
                    $"[ModuleActivation] SKIPPED store-installed module '{module}': {reason}"));
            // 🚨 A LISTED-BUT-ABSENT module must never crash boot. `InstallAssemblies` does
            // `Assembly.LoadFrom`, which throws FileNotFoundException, so one stale line in
            // `Modules:Assemblies` takes the whole portal down before anything is serving —
            // observed on 3.0.0-rc5, whose image no longer ships the fourteen extracted modules
            // while appsettings still listed them: every boot died on
            // `Could not load file or assembly '/app/MeshWeaver.AI.OpenAI.dll'`.
            //
            // The sidecar half already skips a missing DLL loudly (LandedModuleDllExists above);
            // the appsettings BASELINE did not, and a baseline entry is exactly the one a platform
            // change can invalidate without touching the deployment. So the same rule applies to
            // both: skip, say so on stderr, and boot. A module that is genuinely required makes
            // itself known as a missing FEATURE, which is diagnosable — a portal that will not
            // start is not.
            var loadableModules = effectiveModules
                // 🚨 ONE resolution, shared with the existence gate above
                // (ModuleActivationBoot.ResolveLoadPath): a store-landed module resolves to the
                // directory ITS activation entry points at — the generation the landing wrote —
                // and a baseline entry keeps ResolveModulePath's probes. The provenance comes from
                // the union itself rather than being re-derived here; the gate and the resolver
                // each deciding for themselves where a module's bytes live is exactly #1949.
                //
                // 🚨 #2509: a store-landed generation is then PINNED — copied to process-local
                // storage and loaded from there. The shared modules/ tree has reference-set
                // lifetime: an auto-update that lands a newer generation makes THIS pod's loaded
                // one unreferenced, and a sibling pod's boot GC reclaims it while this process
                // still lazily loads dependency DLLs from it (first chat after that was
                // FileNotFoundException 'OpenAI' — the 2026-08-27 outage). Baseline entries stay
                // un-pinned: they resolve into the image's own immutable closure.
                .Select(module => (
                    Module: module,
                    Path: module.Landed is not null
                        ? ModuleGenerationPin.PinnedLoadPath(moduleRoot, module.Landed,
                            onWarn: msg => Console.Error.WriteLine($"[ModuleActivation] {msg}"))
                        : ModuleActivationBoot.ResolveLoadPath(moduleRoot, module)))
                .Where(candidate =>
                {
                    if (File.Exists(candidate.Path))
                        return true;
                    Console.Error.WriteLine(
                        $"[ModuleActivation] SKIPPED module '{candidate.Module.Entry}': no assembly at "
                        + $"'{candidate.Path}'. It is listed in Modules:Assemblies but this image "
                        + "does not ship it — delist it, or install it as a module. Booting without "
                        + "it; whatever it provided is absent.");
                    return false;
                })
                .ToArray();
            var resolvedModules = loadableModules.Select(candidate => candidate.Path).ToArray();

            // 🚨 #2223 — SAY WHICH COPY IS BEING LOADED. A view-pack fix can merge, build, land in
            // the module store and still not run, because a baseline Modules:Assemblies entry
            // resolves to the IMAGE copy and dedupes the store entry away by name. Every lane
            // reported green; the only evidence lived in /proc/1/maps on a prod pod. This reports
            // the paths that are about to be loaded — the SAME array, so the line and the load
            // cannot disagree — and warns when the store holds a newer, different copy. It warns
            // and boots: a pod that refuses to start cannot be given the fix for what is wrong
            // with it.
            ModuleLoadReport.Write(
                ModuleLoadReport.Describe(moduleRoot, loadableModules),
                Console.WriteLine,
                Console.Error.WriteLine);

            // 🚨 #2507 — HAND THE CONFIGURATION TO THE BUILDER BEFORE ANYTHING INSTALLS. A module
            // attribute's BuilderConfigurations run inside InstallAssemblies and read
            // builder.Configuration for the deployment's answers; this method had every answer in
            // its `configuration` parameter and never passed it on, so every module folding on a
            // PORTAL saw null — AiMeshModuleAttribute.ServeFromPartitions(null) put the whole AI
            // catalog on the in-memory path and skipped the AI content sources on both prods,
            // while the deployed config plainly listed the partitions. InstallConfiguredModules
            // (the tester/LocalMesh path) already hands it over, which is exactly why the defect
            // was portal-specific. Unconditional: modules are not the only readers, and a null
            // Configuration must mean "the deployment supplied nothing", never "the host forgot".
            builder.WithConfiguration(configuration);
            if (resolvedModules.Length > 0)
                builder.InstallAssemblies(resolvedModules);
            // Restart-as-activation: this boot IS the restart the sidecar was waiting for —
            // consume the pending flag so the step-10 signal reads current. Best-effort: on a
            // read-only app filesystem the flag simply stays set (cosmetic), and boot proceeds.
            // 🚨 Clears the MARKER, never rewrites the activation record (#2090). Rewriting the
            // whole list here meant every replica's boot read-modify-wrote a file the other
            // replicas were reading — on a rolling restart that is several writers and several
            // readers on one SMB file at once, which is how a boot read came back
            // FileNotFoundException and the pod started with NONE of its store modules (#2189).
            if (persistedActivation.PendingRestart)
                try
                {
                    ModuleActivationSidecar.SetPendingRestart(moduleRoot, false);
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine(
                        $"[ModuleActivation] could not reset PendingRestart ({ex.GetType().Name}: "
                        + $"{ex.Message}) — the flag stays set; activation itself is unaffected.");
                }

            // The first-run setup surface's three inputs, registered HERE — after
            // InstallAssemblies (so a module's assembly attribute has already registered its keyed
            // storage factory) and BEFORE the storage decision below, whose "awaiting setup" branch
            // returns early. A registration after that return would exist on a configured instance
            // and be missing on precisely the instance that needs it.
            builder = (TBuilder)builder.ConfigureServices(services =>
            {
                // Read off the collection NOW: an IServiceProvider can resolve a keyed service you
                // name but cannot enumerate the keys, so this set is unrecoverable later.
                services.TryAddSingleton(Setup.StorageBackendCatalog.Discover(services));
                // A factory, so IsAwaitingSetup is read after the whole builder has run — the two
                // hosts call this method on opposite sides of their portal configuration.
                services.TryAddSingleton(_ => Setup.InstanceSetupStatusAccessor.For(builder));
                services.TryAddSingleton<Setup.SetupAccessToken>();
                return services;
            });

            // Read graph storage config — from this host's configuration, or from the INSTANCE
            // MANIFEST an earlier setup wrote (#2550).
            //
            // 🚨 Configuration WINS. The manifest answers what configuration has not already said;
            // it never overrides a host that stated its own storage. Every deployment that exists
            // today is appsettings-configured and has no manifest, and this path must leave all of
            // them byte-identical — an additive mechanism that changed a configured instance's
            // storage would be a data-loss bug, not a feature.
            var graphStorageConfig = configuration.GetSection("Graph:Storage").Get<GraphStorageConfig>();
            // 🚨 COMPLETE, and with a real backend named (Copilot review). A manifest that exists
            // is not a manifest that ANSWERS: the wizard's own starting point is
            // State=AwaitingStorage with a pre-filled type, and an Unreadable one answers nothing
            // at all. Treating either as configured storage would boot past setup and fail later,
            // deeper, on an unknown backend or a missing connection string — a worse failure than
            // the setup surface, and further from its cause.
            if (graphStorageConfig is null
                && setupManifest is { State: InstanceSetupState.Complete } complete
                && complete.HasStorage
                && complete.Storage is { } chosen)
            {
                graphStorageConfig = new GraphStorageConfig
                {
                    Type = chosen.Type,
                    BasePath = chosen.BasePath,
                    ConnectionString = chosen.ConnectionString,
                };
                Console.WriteLine(
                    $"[InstanceSetup] storage '{chosen.Type}' comes from the instance manifest at "
                    + $"{InstanceManifest.PathFor(moduleRoot)} (set up "
                    + $"{setupManifest.SetUpAt?.ToString("u") ?? "at an unrecorded time"}"
                    + $"{(setupManifest.SetUpBy is { } who ? $" by {who}" : "")}).");
            }

            if (graphStorageConfig == null)
            {
                // 🚨 NOT a throw any more (#2550). An image with no storage configured is an
                // instance AWAITING SETUP, and the whole point of the setup wizard is that such an
                // image comes up far enough to ask which database to use. Throwing here is what
                // made "install the empty image, then configure it" impossible: the process died
                // before anything could serve, so there was nowhere to ask the question.
                //
                // The caller decides what to do with this state; it is reported, never guessed at.
                // A SETUP mesh serves the wizard and nothing else — it must never quietly invent a
                // storage backend, because a wrong guess writes real data somewhere nobody chose
                // (an ephemeral container path, silently lost on the next roll — issue #435's
                // shape).
                // Say WHICH of the three states this is — "no manifest" would be a lie for an
                // unreadable or half-answered one, and setup/boot diagnostics are exactly where a
                // misleading message costs the most (Copilot review).
                var manifestState = setupManifest switch
                {
                    null => "no instance manifest exists there",
                    { State: InstanceSetupState.Unreadable } =>
                        "the instance manifest there could NOT BE READ (see the error above) — "
                        + "repair or delete it to re-run setup",
                    { State: var state } =>
                        $"the instance manifest there is INCOMPLETE (state {state}"
                        + $"{(setupManifest.HasStorage ? "" : ", no storage chosen")}) — finish the "
                        + "setup wizard",
                };
                Console.Error.WriteLine(
                    "[InstanceSetup] No Graph:Storage configuration, and "
                    + $"{manifestState} ({InstanceManifest.PathFor(moduleRoot)}). This instance is "
                    + "AWAITING SETUP. Configure Graph:Storage in appsettings, or complete the "
                    + "setup wizard, which writes the manifest and restarts into a configured mesh.");
                builder.MarkAwaitingSetup();
                return builder;
            }

            // Resolve relative BasePath to absolute
            var basePath = graphStorageConfig.BasePath;
            if (!string.IsNullOrEmpty(basePath) && !Path.IsPathRooted(basePath))
            {
                basePath = Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), basePath));
                graphStorageConfig = graphStorageConfig with { BasePath = basePath };
            }

            // In development, format JSON for readability
            if (isDevelopment)
            {
                var settings = graphStorageConfig.Settings != null
                    ? new Dictionary<string, string>(graphStorageConfig.Settings)
                    : new Dictionary<string, string>();
                settings["FormatJson"] = "true";
                graphStorageConfig = graphStorageConfig with { Settings = settings };
            }

            // Read content collection storage config from appsettings
            var contentStorageConfig = configuration.GetSection("Storage").Get<ContentCollectionConfig>();
            // 🚨 Fail fast on the guaranteed-silent-data-loss footgun (issue #435) BEFORE the
            // relative→absolute resolution below would MASK it: a deployed FileSystem content store
            // with an empty/relative BasePath resolves against the ephemeral container CWD (/app),
            // so uploaded collection files vanish on the next pod restart / grain teardown.
            ValidateContentStorageDurability(contentStorageConfig, isDevelopment);
            // 🚨 Fail fast on a Microsoft tenant that cannot form an OIDC authority (#2621). An
            // env var cannot be null, only empty, so a blank key legitimately means "unset" and
            // passes; a value that is not a single authority segment composes
            // login.microsoftonline.com//... — a URL Entra never serves — and used to surface as an
            // unhandled 500 on the FIRST sign-in, naming the URL instead of the key to fix. Named
            // here at boot instead.
            MicrosoftTenant.Validate(configuration[MicrosoftTenant.ConfigurationKey]);
            // 🚨 Fail fast on an install that CLAIMS mail it cannot send (#2636, #2637):
            // Email:Enabled=true with the section incomplete. Same two rules as the tenant guard
            // above — a blank/disabled section is "never configured" and passes (aborting on an
            // unconfigured optional integration is the #2510 failure), while enabled-but-incomplete
            // is a real misconfiguration and is named here. On memex it produced a portal that
            // served perfectly, reported mail as ON, and dropped every invitation and notification
            // into a queue nobody watches. Inert configuration data only — nothing is resolved and
            // no credential is constructed, which is the property #2510 turned on.
            EmailConfigurationGuard.Validate(configuration);
            if (contentStorageConfig != null)
            {
                // Resolve relative path to absolute
                if (!string.IsNullOrEmpty(contentStorageConfig.BasePath) && !Path.IsPathRooted(contentStorageConfig.BasePath))
                {
                    contentStorageConfig = contentStorageConfig with
                    {
                        BasePath = Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), contentStorageConfig.BasePath))
                    };
                }

                // Ensure Settings are populated for AzureBlob source type
                if (contentStorageConfig.SourceType == "AzureBlob")
                {
                    var settings = contentStorageConfig.Settings is { } existing
                        ? new Dictionary<string, string>(existing)
                        : new Dictionary<string, string>();
                    if (!settings.ContainsKey("ContainerName"))
                        settings["ContainerName"] = "content";
                    if (!settings.ContainsKey("ClientName"))
                        settings["ClientName"] = contentStorageConfig.Name;
                    contentStorageConfig = contentStorageConfig with { Settings = settings };
                }
            }

            // Use partitioned persistence for FileSystem to support per-org partitions
            var usePartitioned = string.Equals(graphStorageConfig.Type, "FileSystem", StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrEmpty(graphStorageConfig.BasePath);

            // Deploy-time feature flags (symmetric with ConfigureMemexServices).
            var features = configuration
                .GetSection(MemexFeatureOptions.SectionName)
                .Get<MemexFeatureOptions>() ?? new MemexFeatureOptions();

            // Static-repo → DB sync: partitions to materialize into + serve from the DB. For a
            // synced partition the read-only in-memory static provider is skipped (PG serves it)
            // and the import runs on boot. Empty (default) = in-memory serving everywhere, no
            // import — no regression. Default Helm sets ["Doc","Agent","Provider","Harness","Skill"].
            var syncPartitions = features.StaticRepoSync.Partitions
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            // NB: the AI partitions are expanded to the whole bundle by the AI module itself
            // (AiMeshModuleAttribute.ServeFromPartitions), next to the sources that rule gates.
            IReadOnlySet<string> serveFromPartition = syncPartitions;

            MeshBuilder mb = builder
                // Configure persistence from Graph:Storage section.
                // Skip if any IPartitionStorageProvider was already registered upstream
                // (e.g., AddPartitionedPostgreSqlPersistence in Memex.Portal.Distributed/Program.cs).
                .ConfigureServices(services =>
                {
                    if (services.Any(sd => sd.ServiceType == typeof(IPartitionStorageProvider)))
                        return services;

                    return usePartitioned
                        ? services.AddPartitionedFileSystemPersistence(graphStorageConfig.BasePath!)
                        : services.AddPersistence(graphStorageConfig);
                })
                // Enable Row-Level Security for access control
                .AddRowLevelSecurity()
                // Configure graph from the same base path
                .AddGraph()
                // Plugin catalog: registers the Package/PluginCatalog content types + (below) the
                // platform-admin "Plugin Catalog" settings tab — NOT a browsable Plugins Space. This
                // instance ALSO acts as the registry: /api/plugins serves its configured source
                // (PluginCatalog:SourceRepoPath — the plugins repo) to other installations, and the
                // admin tab installs from PluginCatalog:RegistryUrl. The options carry the consumer's
                // registry URL/ref (empty RegistryUrl -> the tab shows a "not configured" note).
                .AddPluginCatalog()
                // Red-log ticketing rides the MeshWeaver.Observability MODULE
                // (ObservabilityProviderAttribute → AddLogWatch(); LogWatchOptions binds through
                // the options pipeline). The DETECTOR is not here either way — it is a separate
                // service in the cluster's monitoring namespace that polls Loki and POSTs to
                // /api/log-incidents (Doc/Architecture/LogWatchTriage.md); the compiled endpoint
                // resolves the ILogIncidentIngest Contract seam optionally.
                // Bind the whole section so the multi-registry list (PluginCatalog:Registries:N:*)
                // binds alongside the legacy single RegistryUrl/RegistryRef pair.
                .ConfigureServices(pcs => pcs.AddSingleton(
                    configuration.GetSection(PluginCatalogOptions.SectionName).Get<PluginCatalogOptions>()
                    ?? new PluginCatalogOptions()))
                // Register GitHub-sync content types (GitHubCredential / GitHubSyncConfig)
                // on the mesh + per-node hubs so their config nodes (de)serialize.
                .AddGitHubSyncTypes()
                // The instance-sync content type ({space}/_Sync/{sourceId}) and the SignalR
                // mesh-transport services are registered by the portal composition in
                // MeshWeaver.Plugins (ConfigureMemexPortal) — host business, composed where the
                // hosts live.
                // Register the OAuthCode NodeType + AuthorizationCode content type so the
                // MCP OAuth server (OAuthCodeStore) can persist pending authorization codes
                // as Admin/OAuthCode/{hashPrefix} mesh nodes — the replica-safe store every
                // pod shares (the /token exchange may land on a different replica than the
                // /authorize that minted the code). Without this the create fails with
                // "NodeType 'OAuthCode' is not registered" and no MCP client can connect.
                .AddOAuthCodeType()
                // Seed root-scope Admin AccessAssignments for users listed under
                // `Auth:GlobalAdmins` so configured admins bypass per-partition
                // RLS for cross-partition operations (list Spaces, create
                // a new Space, etc.). Empty / missing section = no-op.
                .AddMeshNodes(Authentication.GlobalAdminSeed.Build(configuration))
                // The platform's settings-tab menu entries (What's New / About / Privacy and the
                // admin tabs Invitations / Inbox / Updates / Published / Token Usage) as seeded
                // UiContribution nodes — the WS7 lane a plugin's own settings tab arrives through.
                .AddPlatformSettingsTabContributions()
                // The AI menu's navigation entries (Threads/Models/Tiers/Providers/Agents/Skills)
                // as seeded UiContribution nodes — same lane a plugin's AI-menu entry (or a whole
                // TopBar-declared menu) arrives through. Only the imperative "New thread" stays
                // compiled (AiMenuItems).
                .AddSpaceType()
                // Generic webhook inbox: the WebhookEvent node type behind
                // POST /api/hooks/{target} (allowlisted via WebhookInbox:Targets).
                .AddWebhookInbox();
                // Courses are fully node-native: the Edu pack owns the types (Edu/Lesson,
                // Edu/Module, Edu/Exercise, Edu/Quiz, Edu/CourseInvite, Edu/CourseCatalog) AND
                // the whole-course navigation (EduCourseNavigationProvider, registered per-hub
                // by the type configuration lambdas). The compiled MeshWeaver.Courses types had
                // zero instances in any repo or reachable mesh and are deleted.

            // The gRPC mesh transport is a MODULE (MeshWeaver.Hosting.Grpc.dll under
            // Modules:Assemblies — GrpcMeshModuleAttribute folds AddGrpcHub over this builder:
            // the transport services + the py/node stream-routed participant address types; its
            // GrpcModuleAttribute maps the meshweaver.v1.Mesh endpoint via
            // MapMeshModuleEndpoints). 🚨 DEFAULT-ON in every deployment: the endpoint is the
            // React GUI's browser data plane (grpc-web Connect+Deliver at the origin root), not
            // just the foreign-participant (py/*, node/*) transport — delist only where there is
            // no React GUI and no foreign participant. The former Features:Grpc flag is gone;
            // the module listing IS the switch. Only the pipeline-order-bound gRPC-web
            // middleware stays compiled (UseMeshWeaverGrpcWebWhenInstalled, below).

            // Each AI provider self-registers everything (catalog source +
            // IOptions binding + IChatClientFactory) via one builder extension.
            // The Models settings tab + the ModelProviderService read these out
            // of the live LanguageModelCatalogOptions — no central registry.
            // Gated by deploy-time feature flags (symmetric with the services-tier
            // AddCopilot/AddClaudeCode in ConfigureMemexServices). A disabled flag
            // drops the catalog source → the provider vanishes from the model
            // picker and its Model/<id> nodes never seed.
            // Language-model providers + CLI harnesses register via boot-loaded module packs
            // (Modules:Assemblies -> each pack's MeshNodeProviderAttribute). The composition root
            // carries NO provider type references any more; a deployment picks providers by
            // editing its module list. Features:Ai flags remain only for the portal-side blocks
            // that co-host CLI processes (Connect, skills sync).

            // Content → vector index is a MODULE now (MeshWeaver.ContentCollections.Indexing.
            // PostgreSql in Modules:Assemblies — PostgresContentIndexingModuleAttribute). Its
            // activation is decided at RESOLVE time on the same conditions this block used to
            // check at compose time (mesh Postgres connection + Embedding:Endpoint/ApiKey + a
            // registered IEmbeddingProvider); unconfigured deployments stay inert exactly as
            // before. The image describer rides the AI package (AddAgentChatServices TryAdds the
            // optional IImageDescriber off the default multimodal model).

            return (TBuilder)mb
                .AddSelfRegistry()
                .AddDocumentation(serveFromPartition)
                .AddStaticRepoSync(serveFromPartition, features.StaticRepoSync.Modes)
                // Ship compiled releases WHEREVER we ship code NodeTypes — Doc AND the sample
                // partitions (ACME, FutuRe, Northwind, Cornerstone, MeshWeaver). Pre-build every
                // shipped code NodeType's release at boot, as System, so the runtime path is a
                // cache hit and no user navigation ever triggers an on-demand compile (the prod
                // 2026-06-18 phantom _Activity/compile-* storm). Idempotent (skips already-built
                // types); off the thread pool so it never blocks startup.
                .ConfigureServices(services =>
                    services.AddHostedService<ShippedReleaseSeedHostedService>())
                // Markdown export (PDF/DOCX/HTML + share-by-email) rides the
                // MeshWeaver.Markdown.Export MODULE (MarkdownExportProviderAttribute →
                // AddMarkdownExport(); node seeding is IfAbsent so the lane switch is idempotent).
                // Azure Blob support (the stream-provider factory, the blob assembly cache, the
                // blob NuGet cache) RELOCATED to the MeshWeaver.Azure.Blob MODULE — its assembly
                // attribute registers the stream-provider factory when landed, and the
                // Azure-backend branches reach the store types by probe-and-delegate. Nothing to
                // register here: a filesystem deployment carries no Azure SDK at all now.
                // Shared NodeType assembly cache (versioned, cross-replica consistent): the
                // TryAdd below yields to the Distributed app's filesystem store on the
                // self-host branch, exactly as the compiled-in registration always did.
                .ConfigureServices(services =>
                {
                    Microsoft.Extensions.DependencyInjection.Extensions.ServiceCollectionDescriptorExtensions
                        .TryAddSingleton<MeshWeaver.Mesh.Services.IAssemblyStore>(services, sp =>
                    {
                        var type = Type.GetType(
                            "MeshWeaver.Azure.Blob.BlobAssemblyStore, MeshWeaver.Azure.Blob",
                            throwOnError: false)
                            ?? throw new InvalidOperationException(
                                "No IAssemblyStore is registered and the MeshWeaver.Azure.Blob "
                                + "module is not landed — register AddFileSystemAssemblyStore "
                                + "(self-host) or land the AzureBlob package (Azure backend).");
                        var cacheDir = System.IO.Path.Combine(
                            System.IO.Path.GetTempPath(), "meshweaver-assembly-cache");
                        // The client type reflects too — this project no longer references the
                        // Azure SDK; the module's assembly (already probed above) carries it.
                        var clientType = Type.GetType(
                            "Azure.Storage.Blobs.BlobServiceClient, Azure.Storage.Blobs",
                            throwOnError: true)!;
                        return (MeshWeaver.Mesh.Services.IAssemblyStore)Activator.CreateInstance(
                            type,
                            Microsoft.Extensions.DependencyInjection.ServiceProviderKeyedServiceExtensions
                                .GetRequiredKeyedService(sp, clientType, "nodetype-cache"),
                            "nodetype-cache",
                            cacheDir,
                            sp.GetRequiredService(
                                typeof(Microsoft.Extensions.Logging.ILogger<>).MakeGenericType(type)))!;
                    });
                    return services;
                })
                // Register the mesh catalog and its public interfaces
                .ConfigureServices(services => services.AddMeshCatalog())
                // Configure default views and content collections for each node hub
                // Each hub gets its own "content" collection pointing to a subdirectory
                .ConfigureDefaultNodeHub(config =>
                {
                    // Declared before the if-block so it's available for both the "content"
                    // collection mapping below and the "attachments" mapping further down.
                    var nodePath = config.Address.ToString();

                    // Content lives ONCE per Space (partition root), NOT on every node. A child-node
                    // path (e.g. "AgenticPension/Dokument") must not get its own content collection —
                    // it inherits the Space's via ExposeInChildren below. Mounting per-child created
                    // overlapping/orphaned collections (content/{space}/{child}/…) and node-level content
                    // refs; indexing is likewise per-Space (one content_chunks table per partition schema).
                    // Gate on the partition root: a single-segment node path (no '/').
                    if (contentStorageConfig != null && !nodePath.Contains('/'))
                    {
                        // Scope static media (SVG, PNG, JPG) to the Space's content subdirectory.
                        var contentSubdir = $"content/{nodePath}";
                        // Combine with original BasePath for FileSystem; for AzureBlob, subdirectory is the blob prefix
                        var basePath = string.IsNullOrEmpty(contentStorageConfig.BasePath)
                            ? contentSubdir
                            : Path.Combine(contentStorageConfig.BasePath, contentSubdir);
                        var nodeContentConfig = contentStorageConfig with
                        {
                            Name = "content",
                            IsEditable = true,
                            ExposeInChildren = true,
                            // isStatic: PUBLISHED on the access-controlled content route — a Space's
                            // images, thumbnails, PDFs and videos are fetched as
                            // /api/content/{node}/{file} and /api/content/{Space}/content/{file}.
                            // Publishing decides REACHABLE, never READABLE: every request is still
                            // gated on Read of the owning node (issue #587).
                            IsStatic = true,
                            BasePath = basePath,
                            Settings = contentStorageConfig.Settings is { } src
                                ? new Dictionary<string, string>(src) { ["BasePath"] = basePath }
                                : new Dictionary<string, string> { ["BasePath"] = basePath }
                        };
                        config = config.AddContentCollection(_ => nodeContentConfig);
                    }

                    // Map "attachments" to "storage" with per-node subdirectory
                    // (needed by FutuRe and other samples that store datacube.csv, etc.).
                    // isStatic: the file browser's download links are
                    // /api/content/{node}/attachments/… — access-controlled, gated on Read of the node.
                    config = config.MapContentCollection(
                        "attachments", "storage", $"attachments/{nodePath}", isStatic: true);

                    // Shared large static assets (e.g. the on-device Whisper models the native client
                    // downloads) live in a FileSystem content collection on the MeshWeaver space, backed
                    // by a read-only AKS file-share mount (StaticAssets:Path). This is the framework-native
                    // way — it gives the upload UI + get/list + content serving for free, and the native
                    // VoiceModelCatalog downloads from the content URL (…/MeshWeaver/static/Speech/…). It's
                    // a no-op when the mount isn't configured (local dev, tests).
                    var staticAssetsMount = configuration["StaticAssets:Path"];
                    if (!string.IsNullOrWhiteSpace(staticAssetsMount) && nodePath == "MeshWeaver")
                        config = config.AddContentCollection(_ => new ContentCollectionConfig
                        {
                            Name = "static",
                            SourceType = "FileSystem",
                            BasePath = staticAssetsMount,
                            Address = config.Address,
                            IsEditable = true,
                            ExposeInChildren = true,
                            // isStatic: the native client downloads these over
                            // /api/content/MeshWeaver/static/Speech/… — a real publication, still
                            // gated on Read of the MeshWeaver node.
                            IsStatic = true,
                            Settings = new Dictionary<string, string> { ["BasePath"] = staticAssetsMount },
                        });

                    return config
                        .WithHeartBeatHandler() // silently ack heartbeats on every per-node hub
                        .AddDefaultLayoutAreas()
                        // The course-shell areas (StartExercise / GoToMyCopy / CourseNav / Learn)
                        .AddApiTokensSettingsTab()
                        // Register your own MeshWeaver installation and get it an instance key.
                        .AddInstancesSettingsTab()
                        // Per-user "Notifications" tab: choose bell/email per notification category.
                        .AddNotificationsSettingsTab()
                        // The AI top-bar menu entry ("New thread") is GUI: its action is a click-time
                        // sentinel resolved in the circuit, so the portal GUI module registers it via
                        // its MeshNodeProviderAttribute rather than core naming a Blazor type.
                        // (The platform-admin Instances overview — live cluster query, Grafana links,
                        // create-instance plan generator — rides the MeshWeaver.SelfUpdate.Aks module,
                        // which registers its own settings tab on the per-node hub.)
                        // The platform's global settings tabs ride the UiContribution lane (WS7):
                        // What's New / About / Privacy (slice 2) plus the Administration tabs —
                        // Invitations + Inbox (invitation-only onboarding, non-user mail), Updates
                        // (the Admin/UpdatePolicy auto-update strategy), Published to the web (the
                        // /sitemap.xml enumeration, rendered) and Token Usage (per-model _Usage
                        // analytics) — all in slice 4. Content stays compiled, exposed here as
                        // layout areas; the menu entries are seeded UiContribution nodes
                        // (AddPlatformSettingsTabContributions on the mesh builder above).
                        .AddPlatformSettingsTabAreas()
                        // GitHub Sync tab — shows only on Space nodes (self-filtered).
                        .AddGitHubSyncSettingsTab()
                        // GitHub Issues & PRs tab — browse/act on the repo's issues + pull requests.
                        .AddGitHubIssuesTab()
                        // NO Plugin Catalog tab. Browsing and provisioning packages is the STORE's
                        // job (/Store → the package card → Provision), and it is the only surface
                        // that runs the install under the System identity via SystemInstall. A
                        // second admin-only page onto the same registry duplicated that flow while
                        // bypassing the funnel it exists to enforce.
                        // Coupons tab (platform admins only) — the Store's typed coupon codes at
                        // Admin/Coupons: live list, redemption tallies, create/open.
                        .AddCouponAdminSettingsTab()
                        // Instance grants (platform admins only) — which plugins each registered
                        // MeshWeaver installation may pull. Registration is self-service; granting
                        // is not, and the grants live in the Admin partition out of the owner's reach.
                        .AddInstanceGrantAdminSettingsTab()
                        // Composition (platform admins only) — WHY this environment carries what it
                        // carries: the deployment's feature flags (Features:Flags:*, which also
                        // decide what it pre-installs) and the parameters its installed packages
                        // declare, with the exact env var to provision for any this environment does
                        // not supply. Read-only: composition arrives through the values file, and a
                        // browser edit would be reverted by the next helm upgrade.
                        .AddCompositionAdminSettingsTab()
                        // Instance Sync lives in the "Synchronizations" NODE-menu item (not a
                        // settings tab) — composed by the portal composition in MeshWeaver.Plugins (ConfigureMemexPortal → AddInstanceSyncTypes).
                        // Code workspace tab — on-disk working-tree editor (checkout/edit/commit/push).
                        .AddWorkingTreeTab()
                        // Git history tab — read-only git browser (commit log + changes + diffs) over the same working tree.
                        .AddGitHistoryTab();
                    // The Content Indexing tab rides the indexing MODULE
                    // (PostgresContentIndexingModuleAttribute's default-node-hub hook) — it
                    // appears exactly when the deployment lists the pipeline.
                })
                // MemexClient node type — per-installation client config under {user}/Client/{id}.
                .AddMemexClientType()
                // Platform self-update: the Admin/UpdatePolicy node + the poller that watches ACR and
                // (on Kubernetes) patches the portal+migration deployments to the newest version per
                // policy. On a non-k8s host it degrades to detect-and-notify. See ReleaseStrategy.md.
                .AddSelfUpdate()
                ;
                // (The platform-admin Instances feature — InstancesOptions plus the live cluster-query
                // service — rides the MeshWeaver.SelfUpdate.Aks module: it is AKS-specific, while the
                // self-update POLLER above stays here because self-update is how a deployment receives
                // new bits, modules included.)
        }

    }


}
