using System.Net.Http;
using System.Reactive.Linq;
using System.Text.Json;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Mesh.Threading;
using MeshWeaver.Plugin.Packaging;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.PluginCatalog;

/// <summary>
/// Fetches a plugin's PREBUILT assemblies from the registry instance and adopts them, so installing
/// a plugin does not cost a compile.
///
/// <para>This is the consuming half of the portal's <c>/api/plugins/bundles</c> surface. It runs
/// AFTER <see cref="PackageInstaller"/> has written the plugin's nodes — <see
/// cref="PrebuiltAssemblySeeder"/> re-keys each assembly under THIS instance's own node version, so
/// the node has to exist first. Running it before is not corrupting, it is merely a no-op: every
/// seed declines because the node is not yet a <c>NodeTypeDefinition</c>.</para>
///
/// <para><b>Adoption is never assumed.</b> The framework MVID is checked twice — once against the
/// index, to avoid downloading bytes this instance is obliged to refuse, and once per assembly
/// inside the seeder, which is the gate that actually holds. A declined bundle is a normal outcome
/// (the registry runs a different framework build), and the caller simply compiles as it does
/// today.</para>
///
/// <para>🚨 Reactive end-to-end — HTTP and zip reads run on the mesh's I/O pool, never a bare
/// <c>Observable.FromAsync</c>, and the <see cref="HttpClient"/> comes from
/// <see cref="IHttpClientFactory"/> when the host registered one. Mirrors
/// <see cref="RegistryPackageSource"/>.</para>
/// </summary>
public sealed class PluginBundleClient
{
    /// <summary>The bundle route prefix on the registry instance.</summary>
    public const string RoutePrefix = "/api/plugins/bundles";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    // Shared fallback when no IHttpClientFactory is registered — HttpClient is designed to be
    // long-lived and shared; a per-call `new HttpClient()` leaks sockets. Immutable shared
    // resource, not a cache, so it does not fall under the no-static-state rule.
    private static readonly HttpClient SharedHttp = new();

    private readonly IMessageHub _hub;
    private readonly string _registryUrl;
    private readonly string _token;
    private readonly IIoPool _httpPool;
    private readonly HttpClient _http;
    private readonly ILogger<PluginBundleClient>? _logger;

    // The count that proves the lane works (#1782 gap 4). Optional: a host that registers no
    // ledger loses the counting, never the fetching.
    private readonly BundleAdoptionLedger? _ledger;
    private readonly bool _requirePrebuilt;

    // ONE index read per client, shared by every package the install pass covers. PromiseSlot, not
    // a plain cached field: concurrent first callers share the single run, and a fault EVICTS so
    // the next caller retries rather than replaying a transient failure forever (#1369).
    private readonly PromiseSlot<BundleIndex> _index = new();

    /// <summary>Creates the client.</summary>
    /// <param name="hub">Calling hub — supplies the I/O pool, the workspace the seeder writes
    /// through, and the assembly store the bytes land in.</param>
    /// <param name="registryUrl">Registry instance base URL, e.g.
    /// <c>https://memex.meshweaver.cloud</c>; a trailing slash is trimmed.</param>
    /// <param name="token">This installation's instance key (<c>mwi_…</c>), sent as
    /// <c>Authorization: Bearer</c>. The bundle routes fail closed, so an empty token gets 401.</param>
    public PluginBundleClient(IMessageHub hub, string registryUrl, string? token = null)
    {
        _hub = hub;
        _registryUrl = (registryUrl ?? "").TrimEnd('/');
        _token = (token ?? "").Trim();
        _httpPool = hub.ServiceProvider.GetService<IoPoolRegistry>()?.Get(IoPoolNames.Http)
                    ?? IoPool.Unbounded;
        // The BUNDLE client, not the shared registry one: these are megabyte transfers off a slow
        // index and they need a budget measured in minutes, which a page-rendering caller must not
        // inherit. See InstanceRegistrationClient.BundleHttpClientName.
        _http = hub.ServiceProvider.GetService<IHttpClientFactory>()
            ?.CreateClient(InstanceRegistrationClient.BundleHttpClientName) ?? SharedHttp;
        _logger = hub.ServiceProvider.GetService<ILoggerFactory>()
            ?.CreateLogger<PluginBundleClient>();
        _ledger = hub.ServiceProvider.GetService<BundleAdoptionLedger>();
        // Deployment policy, resolved once: a require-prebuilt mesh turns every miss below into a
        // named early failure instead of a compile fallback. See RequirePrebuiltConfigKey.
        _requirePrebuilt = PrebuiltAssemblySeeder.RequirePrebuilt(hub.ServiceProvider);
    }

    /// <summary>What the registry advertises: the framework its assemblies were built against, and
    /// the bundles it can serve.</summary>
    /// <param name="FrameworkMvid">The registry's resolved framework build identity — the whole
    /// compatibility proof, compared EXACTLY.</param>
    /// <param name="Bundles">What it can serve.</param>
    /// <param name="Architecture">The portable RID that identity belongs to (#1751), or null from a
    /// registry that predates the link. 🚨 Never a second gate — the identity already folds the
    /// architecture in (the amd64 and arm64 variants of one image resolve DIFFERENT identities). It
    /// exists so a decline can name the lane: without it an arm64 install can only be told "not
    /// adoptable" and #1728 stays invisible, which is precisely how it stayed invisible.</param>
    public sealed record BundleIndex(
        string? FrameworkMvid, IReadOnlyList<BundleRef> Bundles, string? Architecture = null);

    /// <summary>One servable bundle.</summary>
    /// <param name="Plugin">The plugin/package id.</param>
    /// <param name="Version">The served version.</param>
    /// <param name="Url">Absolute download URL.</param>
    /// <param name="Module">The compiled module the bundle carries (its entry-assembly name), or
    /// null for a NodeType-only bundle (#1664). Additive: an older registry simply omits it.</param>
    /// <param name="MinMeshVersion">The module's declared platform FLOOR — surfaced on the index
    /// so a consumer can skip an uninstallable bundle without downloading it. Null = none.</param>
    public sealed record BundleRef(
        string Plugin, string Version, string Url, string? Module = null,
        string? MinMeshVersion = null);

    /// <summary>
    /// Reads the registry's bundle index. Emits an empty index rather than throwing when the
    /// registry does not serve bundles (404) — an older registry is a reason to compile, not an
    /// error to surface to a user installing a plugin.
    /// </summary>
    public IObservable<BundleIndex> FetchIndex() =>
        _httpPool.Invoke(async ct =>
        {
            using var request = Request(HttpMethod.Get, $"{_registryUrl}{RoutePrefix}/index.json");
            using var resp = await _http.SendAsync(request, ct).ConfigureAwait(false);

            if (resp.StatusCode == System.Net.HttpStatusCode.NotFound)
                return new BundleIndex(null, []);

            var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
                // Carries the status (see RegistryResponseException): the index read shares the
                // registry's availability, so a caller deciding whether to re-ask must be able to
                // tell a refusal from a transient 5xx without reading the message text.
                throw new RegistryResponseException(resp.StatusCode,
                    $"Bundle index fetch failed ({(int)resp.StatusCode}): {json}");

            return JsonSerializer.Deserialize<BundleIndex>(json, Json) ?? new BundleIndex(null, []);
        });

    /// <summary>
    /// Fetches <paramref name="pluginId"/>'s bundle and seeds every assembly in it.
    ///
    /// <para>🚨 The version comes from the registry's own INDEX, never from the caller's install
    /// record. The record's <c>ReleasedVersion</c> is written by the installer AFTER the module's
    /// <c>manifest.lock</c> arrives, so at the moment an install would ask for a bundle it is
    /// routinely absent — asking with it would make this a permanent silent no-op. The serving
    /// instance is authoritative about which version it can serve anyway.</para>
    ///
    /// <para>Emits how many assemblies were ADOPTED — zero is a normal, non-error outcome meaning
    /// the caller should compile. Cold: nothing is fetched until Subscribe.</para>
    /// </summary>
    public IObservable<int> Adopt(string pluginId) =>
        _index.GetOrCreate(FetchIndex)
            .Take(1)
            .SelectMany(index =>
            {
                // Compared ONCE, before any download: every assembly this registry serves came out
                // of one bake, so a framework mismatch means every bundle here would be declined.
                // Fetching them anyway would be pure waste on every boot of a consumer running a
                // different build.
                if (PrebuiltAssemblySeeder.DeclineReason(index.FrameworkMvid) is { } reason)
                {
                    // 🚨 The reason names the LANE, not only the opaque hashes (#1751/#1728). The
                    // framework identity folds the architecture in, so an arm64 install reading an
                    // amd64-baked registry sees two unrelated-looking hashes and no way to tell
                    // "incompatible framework" from "no arm64 lane was ever published" — which is
                    // exactly why the arm64 lane went unnoticed. Naming both architectures makes the
                    // miss diagnosable from one log line.
                    _logger?.LogInformation(
                        "Prebuilt assemblies at {Registry} are not adoptable: {Reason} — {Consequence}. "
                        + "The registry bakes {RegistryArchitecture}; this instance is "
                        + "{LiveArchitecture}. Adoption needs a bake published for THIS lane; "
                        + "re-publishing another lane's bytes under this identity is never the fix.",
                        _registryUrl, reason, MissConsequence("compiling"),
                        index.Architecture ?? "(an architecture it does not state)",
                        ReleaseArchitecture.Live);
                    return Miss(pluginId, BundleAdoptionKind.FrameworkDeclined, reason);
                }

                var bundle = index.Bundles?.FirstOrDefault(b =>
                    string.Equals(b.Plugin, pluginId, StringComparison.OrdinalIgnoreCase));

                if (bundle is null)
                {
                    // 🚨 THE MISS THAT WAS COMPLETELY SILENT (#1782 gap 4). This branch returned 0
                    // with no log line at all, so "the registry does not advertise this package for
                    // my lane" was indistinguishable from a healthy adoption — and the compile that
                    // followed looked like normal behaviour rather than the distribution lane being
                    // dark. It is exactly the shape of the 2026-08-20 outage, seen from the
                    // consumer: an empty index, every consumer quietly compiling, nothing in any
                    // log worth reading.
                    _logger?.LogWarning(
                        "Bundle for {Plugin}: {Registry} does not advertise it on framework "
                        + "{Identity}/{Architecture} — {Consequence}. Either that "
                        + "registry has no install record and no published module for it, or its "
                        + "index is filtered by this instance's grant. {Advertised} package(s) are "
                        + "advertised to this instance.",
                        pluginId, _registryUrl, PrebuiltAssemblySeeder.LiveFrameworkMvid,
                        ReleaseArchitecture.Live, MissConsequence("it will be COMPILED here"),
                        index.Bundles?.Count ?? 0);
                    return Miss(pluginId, BundleAdoptionKind.NotAdvertised,
                        $"{_registryUrl} advertises {index.Bundles?.Count ?? 0} package(s) to this "
                        + "instance, and this is not one of them");
                }

                return Download(pluginId, bundle.Version)
                    .SelectMany(result => result.Bytes is null
                        ? Miss(pluginId, result.Kind, result.Reason)
                        : SeedAll(pluginId, result.Bytes));
            });

    /// <summary>
    /// Records a miss and reports it as the zero every caller already handles — or, on a mesh that
    /// opted into <see cref="PrebuiltAssemblySeeder.RequirePrebuiltConfigKey"/>, FAILS with the
    /// named <see cref="PrebuiltRequiredException"/> instead: such a mesh does not compile, so a
    /// miss is not a slow success, it is the distribution lane being dark, and it must fail the
    /// install EARLY, naming what is missing and what fixes it (#2193 §A).
    ///
    /// <para>On the default path the integer return is deliberately unchanged: adoption must never
    /// fail an install there, and widening the contract would ripple through five call sites for
    /// no gain. What changed first (#1782) is that the zero is no longer the ONLY thing that
    /// happened — the reason is named in the log and counted in the ledger; the ledger records the
    /// miss in BOTH modes, so the flag never trades observability for strictness.</para>
    /// </summary>
    private IObservable<int> Miss(string pluginId, BundleAdoptionKind kind, string? reason)
    {
        _ledger?.Record(new BundleAdoptionOutcome(pluginId, kind, _registryUrl, Reason: reason));
        if (_requirePrebuilt)
            return Observable.Throw<int>(new PrebuiltRequiredException(RequiredMessage(
                pluginId, kind.ToString(), reason)));
        return Observable.Return(0);
    }

    /// <summary>The consequence half of a miss LOG LINE, truthful per mode: a default mesh
    /// compiles, a require-prebuilt mesh fails the adoption right after the line (#2198 review).</summary>
    private string MissConsequence(string defaultConsequence) =>
        _requirePrebuilt
            ? "the adoption FAILS here (" + PrebuiltAssemblySeeder.RequirePrebuiltConfigKey + ")"
            : defaultConsequence;

    /// <summary>The one message shape for every <see cref="PrebuiltAssemblySeeder.RequirePrebuiltConfigKey"/>
    /// refusal on this lane — package, registry, identity/architecture, miss kind, fix. Pure.</summary>
    private string RequiredMessage(string pluginId, string kind, string? reason) =>
        $"{PrebuiltAssemblySeeder.RequirePrebuiltConfigKey}: no prebuilt assemblies for "
        + $"'{pluginId}' from {_registryUrl} on framework "
        + $"{PrebuiltAssemblySeeder.LiveFrameworkMvid}/{ReleaseArchitecture.Live} "
        + $"({kind}{(string.IsNullOrWhiteSpace(reason) ? "" : $": {reason}")}). "
        + "This mesh does not compile module content — publish or rebake the bundle for this "
        + "framework identity and architecture, then retry the install (MeshWeaver#2193 §A).";

    /// <summary>
    /// Fetches <paramref name="pluginId"/>'s bundle and LANDS the compiled module it carries into
    /// this deployment's <c>modules/</c> tree (#1664 Slice C) — the module counterpart of
    /// <see cref="Adopt"/>, riding the same index, the same download route and the same MVID gate.
    ///
    /// <para>The whole decision is <see cref="ModuleUpdateDecision.Decide"/>, taken BEFORE any
    /// download: an up-to-date module, a bundle whose platform floor this deployment does not
    /// satisfy, an uninstalled module and a policy-declined unattended run each cost zero bytes.
    /// The gate is the <c>minMeshVersion</c> FLOOR (<see cref="ModulePlatformFloor"/>), never MVID
    /// equality — that strict gate is the NodeType lane's (<see cref="Adopt"/>); a module built
    /// against a different platform build lands fine as long as its floor is satisfied. Landing
    /// goes through <see cref="ModuleLandingService"/> (restart-as-activation — the sidecar's
    /// <c>PendingRestart</c> is the step-10 signal); the module LOADS at the next restart.</para>
    ///
    /// <para>Emits how many module files were landed — zero is a normal, non-error outcome (nothing
    /// to land, or the bundle is for a framework this deployment does not run yet). Like
    /// <see cref="Adopt"/>, nothing here may fail an install: every refusal is logged and absorbed.
    /// Cold: nothing is fetched until Subscribe.</para>
    /// </summary>
    /// <param name="pluginId">The package id whose bundle carries the module.</param>
    /// <param name="moduleName">The module's entry-assembly name (the package manifest's
    /// <see cref="PackageManifest.Module"/> declaration).</param>
    /// <param name="packagePath">The install record's mesh path, recorded on the activation entry.</param>
    /// <param name="unattended">True on the reconciler's background lane — gates the landing on
    /// <see cref="IModuleUpdatePolicy"/> (the deployment's existing update-policy surface; absent =
    /// allowed, the platform default). An explicit install passes false: the operator asked.</param>
    public IObservable<int> AdoptModule(
        string pluginId, string moduleName, string? packagePath = null, bool unattended = false)
    {
        var landing = _hub.ServiceProvider.GetService<ModuleLandingService>();
        if (landing is null)
        {
            _logger?.LogWarning(
                "Module bundle for {Plugin}: no ModuleLandingService on this host — nothing landed",
                pluginId);
            return Observable.Return(0);
        }

        var policy = unattended ? _hub.ServiceProvider.GetService<IModuleUpdatePolicy>() : null;
        var policyDecline = policy?.DeclineUnattendedLanding().Take(1)
                            ?? Observable.Return<string?>(null);

        return _index.GetOrCreate(FetchIndex)
            .Take(1)
            .SelectMany(index => landing.GetActivation().Take(1)
                .SelectMany(activation => policyDecline
                    .SelectMany(declined =>
                    {
                        var bundle = index.Bundles?.FirstOrDefault(b =>
                            string.Equals(b.Plugin, pluginId, StringComparison.OrdinalIgnoreCase)
                            && !string.IsNullOrWhiteSpace(b.Module));
                        var entry = activation.Entries.FirstOrDefault(e =>
                            string.Equals(e.Name, moduleName, StringComparison.OrdinalIgnoreCase));

                        // 🚨 #2417 — the presence probe, bound to the landing service's OWN base
                        // directory and resolved by ModuleActivationBoot, which is the same rule
                        // boot uses. Never MeshBuilder.ResolveModulePath: its app-closure fallback
                        // would find a same-named platform DLL and report a landed module that was
                        // never landed here.
                        var verdict = ModuleUpdateDecision.Decide(
                            bundle?.Version, bundle?.MinMeshVersion,
                            ModulePlatformFloor.DeclineReason, entry, declined,
                            e => ModuleActivationBoot.LandedModuleDllExists(landing.BaseDirectory, e));

                        if (verdict.Action != ModuleUpdateAction.Land)
                        {
                            _logger?.LogInformation(
                                "Module '{Module}' of {Plugin}: {Action} — {Reason}",
                                moduleName, pluginId, verdict.Action, verdict.Reason);
                            return Observable.Return(0);
                        }

                        _logger?.LogInformation(
                            "Module '{Module}' of {Plugin}: {Reason}",
                            moduleName, pluginId, verdict.Reason);
                        return Download(pluginId, bundle!.Version)
                            .SelectMany(result => result.Bytes is null
                                ? Miss(pluginId, result.Kind, result.Reason)
                                : LandFromBundle(
                                    pluginId, moduleName, packagePath, bundle.Version, result.Bytes));
                    })))
            .Catch((Exception ex) =>
            {
                // A distribution hiccup must not fail the install/reconcile that asked — the module
                // simply stays as it is until the next boot or a manual re-install.
                // The cause rides IN the message: the attached exception lives on separate log
                // lines that single-line pipelines (Loki greps) never pair with this one, and
                // "landing failed" with no reason made a stuck production module a night-long
                // dig (Plugins#959).
                _logger?.LogWarning(ex,
                    "Module '{Module}' of {Plugin}: landing failed — the module is unchanged. "
                    + "Cause: {Cause}",
                    moduleName, pluginId, ex.Message);
                return Observable.Return(0);
            });
    }

    /// <summary>
    /// Reads the downloaded bundle's module section and lands it. The platform FLOOR is verified
    /// AGAIN here, against the manifest inside the archive — the index said what the registry
    /// advertises, the manifest says what these bytes require, and only the second is the gate
    /// that holds (<see cref="ModuleLandingService"/> re-checks it a third time at placement;
    /// declining twice is cheaper than debugging a MissingMethodException once). The MVID the
    /// bundle records is logged as DIAGNOSTIC metadata, never refused.
    /// </summary>
    // Internal for the ModuleFunnelTest pin (InternalsVisibleTo): the land half without HTTP.
    internal IObservable<int> LandFromBundle(
        string pluginId, string moduleName, string? packagePath, string version, byte[] bundleBytes) =>
        _httpPool.InvokeBlocking(_ => BundleReader.ReadModule(bundleBytes))
            .SelectMany(payload =>
            {
                var (manifest, files) = payload;

                if (ModulePlatformFloor.DeclineReason(manifest?.Module?.MinMeshVersion) is { } reason)
                {
                    _logger?.LogInformation(
                        "Module bundle for {Plugin} DECLINED: {Reason} — nothing landed",
                        pluginId, reason);
                    return Observable.Return(0);
                }

                if (files.Count == 0)
                {
                    _logger?.LogInformation(
                        "Module bundle for {Plugin} carries no (complete) module payload — nothing landed",
                        pluginId);
                    return Observable.Return(0);
                }

                // The bundle must be the module the PACKAGE declared — a producer/catalog drift
                // here would land bytes under an identity the boot union never asks for.
                if (manifest?.Module?.AssemblyName is { Length: > 0 } declared
                    && !string.Equals(declared, moduleName, StringComparison.OrdinalIgnoreCase))
                {
                    _logger?.LogWarning(
                        "Module bundle for {Plugin} declares module '{Declared}' but the package "
                        + "declares '{Expected}' — nothing landed", pluginId, declared, moduleName);
                    return Observable.Return(0);
                }

                var landing = _hub.ServiceProvider.GetRequiredService<ModuleLandingService>();
                return landing
                    .LandModule(
                        moduleName,
                        files.Select(f => (f.FileName, f.Bytes)).ToArray(),
                        // Diagnostic metadata (which exact platform build produced these bytes),
                        // recorded on the activation entry and logged at landing — never a gate.
                        manifest!.FrameworkMvid,
                        packagePath,
                        version,
                        manifest.Module?.MinMeshVersion,
                        // A view pack's wwwroot rides the bundle (#1724's provider serves it from
                        // the module folder); without this the pack lands unstyled and its
                        // collocated JS 404s.
                        BundleReader.ReadModuleAssets(bundleBytes) is { Count: > 0 } assets
                            ? [.. assets.Select(a => (a.RelativePath, a.Bytes))]
                            : null)
                    .Select(_ => files.Count)
                    .Do(count => _logger?.LogInformation(
                        "Module '{Module}' of {Plugin} landed ({Count} file(s), version {Version}) "
                        + "— RESTART REQUIRED to load it", moduleName, pluginId, count, version));
            });

    /// <summary>
    /// Downloads the bundle, or emits null when the registry has none for this plugin/version.
    /// </summary>
    /// <summary>
    /// A fetch's outcome: the bytes, or the NAMED reason there are none. A bare <c>byte[]?</c>
    /// collapsed "404 for this lane" and "the registry is down" into the same null, and the caller
    /// then collapsed that into the same 0 as a successful adoption.
    /// </summary>
    private sealed record FetchResult(byte[]? Bytes, BundleAdoptionKind Kind, string? Reason = null);

    private IObservable<FetchResult> Download(string pluginId, string version) =>
        _httpPool.Invoke(async ct =>
        {
            // 🚨 The consumer asks IN ITS OWN LANE (#1751): the registry resolves each NodeType's
            // assembly through that type's Release node for exactly this (identity, architecture)
            // pair, so a deployment whose lane is not the registry's own can still be served the
            // moment a bake for its lane is recorded. Omitting them would make the registry answer
            // for ITS lane and silently hand back bytes this instance must then decline.
            var url = $"{_registryUrl}{RoutePrefix}/{Uri.EscapeDataString(pluginId)}"
                      + $"/{Uri.EscapeDataString(version)}"
                      + $"?identity={Uri.EscapeDataString(PrebuiltAssemblySeeder.LiveFrameworkMvid)}"
                      + $"&arch={Uri.EscapeDataString(ReleaseArchitecture.Live)}";
            using var request = Request(HttpMethod.Get, url);
            using var resp = await _http.SendAsync(request, ct).ConfigureAwait(false);

            if (resp.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                _logger?.LogInformation(
                    "No prebuilt bundle for {Plugin}@{Version} at {Registry} — {Consequence}",
                    pluginId, version, _registryUrl, MissConsequence("will compile"));
                return new FetchResult(null, BundleAdoptionKind.NotServed,
                    $"the registry advertises {pluginId}@{version} but serves no bytes for "
                    + $"{PrebuiltAssemblySeeder.LiveFrameworkMvid}/{ReleaseArchitecture.Live}");
            }

            if (!resp.IsSuccessStatusCode)
            {
                // 🚨 Not thrown: a registry that is down, rate-limiting or has revoked this
                // install's grant must not fail the INSTALL. Compiling is the correct fallback and
                // it always works; turning a distribution hiccup into an install failure trades a
                // slow success for a hard error.
                _logger?.LogWarning(
                    "Bundle fetch for {Plugin}@{Version} failed ({Status}) — {Consequence}",
                    pluginId, version, (int)resp.StatusCode, MissConsequence("will compile"));
                return new FetchResult(null, BundleAdoptionKind.FetchFailed,
                    $"HTTP {(int)resp.StatusCode} from {_registryUrl}");
            }

            return new FetchResult(
                await resp.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false),
                BundleAdoptionKind.Adopted);
        });

    /// <summary>
    /// Reads the archive and seeds each assembly, one after another.
    ///
    /// <para><see cref="Observable.Concat{TSource}(IObservable{IObservable{TSource}})"/> rather than
    /// Merge: each seed writes the NodeType node through its owning hub, and letting them overlap
    /// buys nothing (the writes serialise there anyway) while making the log order meaningless.</para>
    /// </summary>
    private IObservable<int> SeedAll(string pluginId, byte[] bundleBytes) =>
        _httpPool.InvokeBlocking(_ => BundleReader.Read(bundleBytes))
            .SelectMany(payload =>
            {
                var (manifest, assemblies) = payload;

                if (PrebuiltAssemblySeeder.DeclineReason(manifest?.FrameworkMvid) is { } reason)
                {
                    _logger?.LogInformation(
                        "Bundle for {Plugin} DECLINED whole: {Reason} — {Consequence}",
                        pluginId, reason, MissConsequence("compiling instead"));
                    return Miss(pluginId, BundleAdoptionKind.BundleDeclined, reason);
                }

                // 🚨 The producer's per-type MISSES (#1751), surfaced here rather than only in the
                // registry's log: this is the consumer side of "a fetch miss must stay loud and
                // countable". Without it a bundle that resolved nothing for this lane looks exactly
                // like a package that has no NodeTypes, and the compile that follows looks like
                // normal behaviour rather than a regression in the distribution lane.
                if (manifest?.Misses is { Count: > 0 } misses)
                {
                    // The consequence half of the line must tell the truth PER MODE: on a default
                    // mesh the unresolved types compile here; on a require-prebuilt mesh the very
                    // next statement fails the adoption instead (Copilot on #2198).
                    _logger?.LogWarning(
                        "Bundle for {Plugin}: the registry could resolve NO artifact for {Missed} "
                        + "NodeType(s) on framework {Identity}/{Architecture} — {Consequence}: {Misses}",
                        pluginId, misses.Count, PrebuiltAssemblySeeder.LiveFrameworkMvid,
                        ReleaseArchitecture.Live,
                        MissConsequence("they will be compiled here"),
                        string.Join(" | ", misses));
                    // 🚨 PARTIAL coverage fails a require-prebuilt mesh just like a whole-bundle
                    // miss, and it fails BEFORE any of the resolved assemblies are seeded: the
                    // unresolved types would otherwise compile at first access — the exact silent
                    // fallback the flag forbids — and a half-seeded package would make the retry
                    // after the rebake harder to reason about, not easier.
                    if (_requirePrebuilt)
                        return Miss(pluginId, BundleAdoptionKind.NoAssemblies,
                            $"the registry resolved no artifact for {misses.Count} NodeType(s): "
                            + string.Join(" | ", misses));
                }

                if (assemblies.Count == 0)
                {
                    _logger?.LogInformation(
                        "Bundle for {Plugin} carried no assemblies — {Consequence}", pluginId,
                        MissConsequence("compiling instead"));
                    return Miss(pluginId, BundleAdoptionKind.NoAssemblies,
                        "the bundle carried no assemblies");
                }

                return assemblies
                    .Select(a => PrebuiltAssemblySeeder.Seed(
                        _hub, a.NodePath, a.Assembly, a.Pdb, manifest!.FrameworkMvid, _logger,
                        a.Dependencies))
                    .Concat()
                    .Count(adopted => adopted)
                    .Do(count =>
                    {
                        _logger?.LogInformation(
                            "Bundle for {Plugin}: adopted {Adopted}/{Total} prebuilt assemblies",
                            pluginId, count, assemblies.Count);
                        // 🚨 A PARTIAL adoption is recorded as the partial thing it is. Rounding
                        // "adopted 3 of 12" up to "adopted" is how a regression hides inside a
                        // success — the other nine are compiled here, which is precisely the cost
                        // this lane exists to remove.
                        _ledger?.Record(new BundleAdoptionOutcome(
                            pluginId, BundleAdoptionKind.Adopted, _registryUrl,
                            Adopted: count, Offered: assemblies.Count));
                    });
            });

    // Per-REQUEST auth header — never on the client: _http can be the process-wide SharedHttp, and
    // mutating its DefaultRequestHeaders would leak this registry's token to every other registry.
    private HttpRequestMessage Request(HttpMethod method, string url)
    {
        var request = new HttpRequestMessage(method, url);
        if (_token.Length > 0)
            request.Headers.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue(
                    PluginRegistryTokens.Scheme, _token);
        return request;
    }
}
