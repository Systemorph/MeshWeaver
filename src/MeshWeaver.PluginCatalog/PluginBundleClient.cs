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
        _http = hub.ServiceProvider.GetService<IHttpClientFactory>()
            ?.CreateClient(InstanceRegistrationClient.HttpClientName) ?? SharedHttp;
        _logger = hub.ServiceProvider.GetService<ILoggerFactory>()
            ?.CreateLogger<PluginBundleClient>();
    }

    /// <summary>What the registry advertises: the framework its assemblies were built against, and
    /// the bundles it can serve.</summary>
    public sealed record BundleIndex(string? FrameworkMvid, IReadOnlyList<BundleRef> Bundles);

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
                throw new InvalidOperationException(
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
                    _logger?.LogInformation(
                        "Prebuilt assemblies at {Registry} are not adoptable: {Reason} — compiling",
                        _registryUrl, reason);
                    return Observable.Return(0);
                }

                var bundle = index.Bundles?.FirstOrDefault(b =>
                    string.Equals(b.Plugin, pluginId, StringComparison.OrdinalIgnoreCase));

                return bundle is null
                    ? Observable.Return(0)
                    : Download(pluginId, bundle.Version)
                        .SelectMany(bytes => bytes is null
                            ? Observable.Return(0)
                            : SeedAll(pluginId, bytes));
            });

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

                        var verdict = ModuleUpdateDecision.Decide(
                            bundle?.Version, bundle?.MinMeshVersion,
                            ModulePlatformFloor.DeclineReason, entry, declined);

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
                            .SelectMany(bytes => bytes is null
                                ? Observable.Return(0)
                                : LandFromBundle(pluginId, moduleName, packagePath, bundle.Version, bytes));
                    })))
            .Catch((Exception ex) =>
            {
                // A distribution hiccup must not fail the install/reconcile that asked — the module
                // simply stays as it is until the next boot or a manual re-install.
                _logger?.LogWarning(ex,
                    "Module '{Module}' of {Plugin}: landing failed — the module is unchanged",
                    moduleName, pluginId);
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
                        manifest.Module?.MinMeshVersion)
                    .Select(_ => files.Count)
                    .Do(count => _logger?.LogInformation(
                        "Module '{Module}' of {Plugin} landed ({Count} file(s), version {Version}) "
                        + "— RESTART REQUIRED to load it", moduleName, pluginId, count, version));
            });

    /// <summary>
    /// Downloads the bundle, or emits null when the registry has none for this plugin/version.
    /// </summary>
    private IObservable<byte[]?> Download(string pluginId, string version) =>
        _httpPool.Invoke(async ct =>
        {
            var url = $"{_registryUrl}{RoutePrefix}/{Uri.EscapeDataString(pluginId)}"
                      + $"/{Uri.EscapeDataString(version)}";
            using var request = Request(HttpMethod.Get, url);
            using var resp = await _http.SendAsync(request, ct).ConfigureAwait(false);

            if (resp.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                _logger?.LogInformation(
                    "No prebuilt bundle for {Plugin}@{Version} at {Registry} — will compile",
                    pluginId, version, _registryUrl);
                return null;
            }

            if (!resp.IsSuccessStatusCode)
            {
                // 🚨 Not thrown: a registry that is down, rate-limiting or has revoked this
                // install's grant must not fail the INSTALL. Compiling is the correct fallback and
                // it always works; turning a distribution hiccup into an install failure trades a
                // slow success for a hard error.
                _logger?.LogWarning(
                    "Bundle fetch for {Plugin}@{Version} failed ({Status}) — will compile",
                    pluginId, version, (int)resp.StatusCode);
                return null;
            }

            return await resp.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
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
                        "Bundle for {Plugin} DECLINED whole: {Reason} — compiling instead",
                        pluginId, reason);
                    return Observable.Return(0);
                }

                if (assemblies.Count == 0)
                {
                    _logger?.LogInformation(
                        "Bundle for {Plugin} carried no assemblies — compiling instead", pluginId);
                    return Observable.Return(0);
                }

                return assemblies
                    .Select(a => PrebuiltAssemblySeeder.Seed(
                        _hub, a.NodePath, a.Assembly, a.Pdb, manifest!.FrameworkMvid, _logger))
                    .Concat()
                    .Count(adopted => adopted)
                    .Do(count => _logger?.LogInformation(
                        "Bundle for {Plugin}: adopted {Adopted}/{Total} prebuilt assemblies",
                        pluginId, count, assemblies.Count));
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
