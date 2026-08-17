using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Text.Json;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Plugin.Packaging;
using MeshWeaver.PluginCatalog;
using MeshWeaver.Messaging;
using PackagingManifest = MeshWeaver.Plugin.Packaging.PluginManifest;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Memex.Portal.Shared.Api;

/// <summary>
/// Serves a plugin's prebuilt assemblies to another instance — the distribution half of
/// <c>Doc/Architecture/PluginPackaging</c>.
///
/// <para>A consumer needs three things to skip a compile: the bytes, the framework identity they
/// were built against, and which node each belongs to. One bundle carries exactly that, over two
/// routes — an index and a download. There is no package protocol here because nothing restores:
/// NodeType compilation runs in-process with Roslyn against <c>MetadataReference</c>s, so a service
/// index and dependency ranges would be surfaces that can drift with no client to read them.</para>
///
/// <para><b>The portal serves the bytes rather than handing out storage access.</b> The assembly
/// store is already the durable transport: <c>BlobAssemblyStore</c> keeps one blob per
/// <c>(nodeTypePath, version)</c> and hydrates it into a process-local cache on demand. Reading
/// through <see cref="IAssemblyStore"/> here means the bundle is assembled from the very bytes this
/// portal loads and runs, and it needs no second credential — a scoped SAS handed to each consumer
/// would be a second entitlement path to keep honest, and revoking it is not the same operation as
/// revoking the install's <see cref="PluginGrant"/>.</para>
///
/// <para>🚨 <b>Authorization is the instance key, not a bundle-specific scheme.</b> A caller presents
/// its <c>mwi_</c> key — as <c>Bearer</c> or <c>Basic</c> — and it resolves to the admin-owned
/// <see cref="PluginGrant"/> that says which packages that install may read. Same gate as
/// <c>/api/plugins</c>, deliberately: a second entitlement path is a second thing to get wrong, and
/// this one is already what purchases are recorded against.</para>
/// </summary>
public static class PluginBundleEndpoints
{
    /// <summary>Route the bundles are mounted at.</summary>
    public const string RoutePrefix = "/api/plugins/bundles";

    /// <summary><see cref="HttpContext.Items"/> key holding the authenticated caller.</summary>
    private const string CallerItemKey = "PluginBundle.Caller";

    /// <summary>Maps the instance-key-gated bundle routes. Call alongside <c>MapPluginRegistry</c>.</summary>
    public static IEndpointRouteBuilder MapPluginBundles(this IEndpointRouteBuilder endpoints)
    {
        // AllowAnonymous at the ASP.NET layer for the same reason the registry does it: callers are
        // INSTANCES, not signed-in users, so the user auth schemes do not apply. The filter below
        // is the real gate.
        var group = endpoints.MapGroup(RoutePrefix).AllowAnonymous();

        group.AddEndpointFilter(async (ctx, next) =>
        {
            var http = ctx.HttpContext;
            var logger = http.RequestServices.GetService<ILoggerFactory>()
                ?.CreateLogger(typeof(PluginBundleEndpoints));
            var authenticator = http.RequestServices.GetRequiredService<InstanceRegistryAuthenticator>();

            var caller = await authenticator.Authenticate(http.Request.Headers.Authorization)
                .FirstAsync().ToTask(http.RequestAborted);

            if (caller is null)
            {
                // 🚨 Fails CLOSED, with no anonymous escape hatch — unlike /api/plugins, which
                // keeps one for local dev. These are compiled assemblies for PAID modules; "open
                // when unconfigured" is how the registry served private sources to anyone who knew
                // the URL (2026-08-06), and there is no reason to repeat it here.
                logger?.LogWarning("Plugin bundles: rejected {Path} — no valid instance key presented",
                    http.Request.Path);
                return Results.Json(
                    new { error = "A registered instance key is required (Authorization: Bearer mwi_… or Basic)." },
                    statusCode: StatusCodes.Status401Unauthorized);
            }

            http.Items[CallerItemKey] = caller;
            return await next(ctx);
        });

        // 🚨 The hub is resolved from RequestServices INSIDE the handler, never bound as a
        // parameter. Minimal-API binds handler arguments BEFORE endpoint filters run, so a bound
        // IMessageHub makes an UNAUTHENTICATED request depend on the mesh being resolvable — it
        // throws (500) instead of the 401 the filter would have returned. The rejection path must
        // not need anything but the header.
        group.MapGet("/index.json", (HttpContext http, CancellationToken ct) =>
            Index(http, RootHub(http), ct));

        group.MapGet("/{plugin}/{version}",
            (HttpContext http, string plugin, string version, CancellationToken ct) =>
                Bundle(RootHub(http), plugin, version, ct));

        return endpoints;
    }

    private static IMessageHub RootHub(HttpContext http) =>
        http.RequestServices.GetRequiredService<IMessageHub>();

    /// <summary>
    /// What this instance can serve, and the framework identity it serves it for.
    ///
    /// <para>The framework MVID is at the TOP, not per-bundle: every assembly here was produced by
    /// this portal's own bake, so they all share it. A consumer compares it once and skips the whole
    /// fetch when it does not match — downloading bundles it is then obliged to decline is pure
    /// waste, and the decline itself is silent (see <see cref="PrebuiltAssemblySeeder"/>).</para>
    ///
    /// <para>Absolute URLs are built from the REQUEST rather than configuration: an instance reached
    /// through an ingress, a port-forward or a custom domain must advertise the host the caller
    /// actually used, or every follow-up request goes somewhere it cannot reach.</para>
    /// </summary>
    private static Task<IResult> Index(HttpContext http, IMessageHub rootHub, CancellationToken ct)
    {
        var baseUrl = $"{http.Request.Scheme}://{http.Request.Host}{RoutePrefix}";

        return InstalledPackages(rootHub, ct)
            .SelectMany(packages => ServableModules(rootHub, packages)
                .Select(modules => Results.Json(new
                {
                    frameworkMvid = FrameworkMvid,
                    bundles = packages.Select(p => new
                    {
                        plugin = p.PluginId,
                        version = p.Version,
                        url = $"{baseUrl}/{Uri.EscapeDataString(p.PluginId)}/{Uri.EscapeDataString(p.Version)}",
                        // The compiled module this bundle carries (#1664) — stamped ONLY when this
                        // instance can actually serve its bytes, so a consumer never downloads for
                        // a module section that will not be there. Additive: an older client's
                        // BundleRef simply ignores it.
                        module = modules.TryGetValue(p.PluginId, out var moduleName) ? moduleName : null,
                        // The module's declared platform FLOOR — the consumer's gate (a semver
                        // floor, never MVID equality; the index-level frameworkMvid above stays
                        // the NodeType lane's strict gate and, for modules, diagnostics).
                        minMeshVersion = modules.ContainsKey(p.PluginId) ? p.MinMeshVersion : null,
                    }).ToArray(),
                })))
            .FirstAsync()
            .ToTask(ct);
    }

    /// <summary>
    /// Which of the installed packages' declared modules this instance can serve right now:
    /// plugin id → module assembly name, for exactly the entries whose bytes exist under
    /// <c>modules/&lt;name&gt;/</c> and pass the platform-floor gate (<see cref="ModuleBundleSource"/>
    /// — the same <see cref="ModulePlatformFloor"/> check boot applies, so a registry never serves
    /// a landing its own boot skips). One activation-sidecar read for the whole index.
    /// </summary>
    private static IObservable<IReadOnlyDictionary<string, string>> ServableModules(
        IMessageHub rootHub, IReadOnlyList<BundleEntry> packages)
    {
        var landing = rootHub.ServiceProvider.GetService<ModuleLandingService>();
        var declaring = packages.Where(p => !string.IsNullOrWhiteSpace(p.Module)).ToArray();
        if (landing is null || declaring.Length == 0)
            return Observable.Return<IReadOnlyDictionary<string, string>>(
                new Dictionary<string, string>());

        return landing.GetActivation().Take(1)
            .Select(activation => (IReadOnlyDictionary<string, string>)declaring
                .Where(p => ModuleBundleSource.Collect(
                        landing.BaseDirectory, p.Module!, activation,
                        ModulePlatformFloor.DeclineReason)
                    .DeclineReason is null)
                .ToDictionary(p => p.PluginId, p => p.Module!, StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>
    /// One plugin's bundle: each of its NodeTypes' compiled assemblies, plus the manifest saying
    /// which node each belongs to and what framework they were built against.
    ///
    /// <para><b>Assemblies only — deliberately no node content.</b> The consumer already installs
    /// content through the registry (<c>PackageInstaller</c>), so shipping it again would be weight
    /// on every fetch AND a second copy that can disagree with the one the installer wrote. The
    /// bundle carries the one thing the consumer cannot otherwise obtain: bytes it would have had to
    /// spend a compile producing.</para>
    ///
    /// <para>Assembled on request rather than stored, because the inputs ARE the storage — this
    /// portal has the bake's assemblies, and a second copy kept "for distribution" is a copy that
    /// can disagree with what the portal runs.</para>
    /// </summary>
    private static Task<IResult> Bundle(
        IMessageHub rootHub, string plugin, string version, CancellationToken ct) =>
        InstalledPackages(rootHub, ct)
            .SelectMany(packages =>
            {
                var match = packages.FirstOrDefault(p =>
                    string.Equals(p.PluginId, plugin, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(p.Version, version, StringComparison.OrdinalIgnoreCase));

                return match is null
                    ? Observable.Return(Results.NotFound())
                    : Assemble(rootHub, match);
            })
            .FirstAsync()
            .ToTask(ct);

    /// <summary>
    /// Reads the plugin's nodes and their assemblies, then builds the archive.
    /// </summary>
    private static IObservable<IResult> Assemble(IMessageHub rootHub, BundleEntry package)
    {
        var meshService = rootHub.ServiceProvider.GetRequiredService<IMeshService>();
        var store = rootHub.ServiceProvider.GetService<IAssemblyStore>() ?? NullAssemblyStore.Instance;
        var options = rootHub.JsonSerializerOptions;

        return meshService
            .Query<MeshNode>(MeshQueryRequest.FromQuery(
                $"namespace:{package.PluginId} scope:subtree"))
            .Where(c => c.ChangeType == QueryChangeType.Initial)
            .Take(1)
            .SelectMany(change =>
            {
                var nodes = change.Items.ToArray();

                // Each NodeType contributes the assembly the bake produced for it. A type with no
                // usable build contributes nothing rather than failing the bundle: a plugin whose
                // types are still compiling is incomplete, not broken, and the caller can retry.
                // The manifest lists what IS here, so a consumer can tell the difference.
                var lookups = nodes
                    .Select(n => (Node: n, Definition: n.ContentAs<NodeTypeDefinition>(options)))
                    .Where(x => x.Definition?.LastCompiledVersion is not null)
                    .Select(x => store
                        .TryGetAssemblyPath(x.Node.Path, x.Definition!.LastCompiledVersion!.Value)
                        .Take(1)
                        .Catch<string?, Exception>(_ => Observable.Return<string?>(null))
                        .Select(path => (x.Node.Path, Path: path,
                            Dependencies: (IReadOnlyDictionary<string, string>?)x.Definition!.CompiledDependencies)))
                    .ToArray();

                var assemblies = lookups.Length == 0
                    ? Observable.Return(
                        Array.Empty<(string NodePath, string? Path, IReadOnlyDictionary<string, string>? Dependencies)>())
                    : lookups.CombineLatest().Select(x => x.ToArray());

                return assemblies.SelectMany(found => ModuleFiles(rootHub, package)
                    .Select(moduleFiles => BuildResult(package, found, moduleFiles)));
            });
    }

    /// <summary>
    /// The MODULE closure files this bundle carries (#1664) — the instance's own
    /// <c>modules/&lt;name&gt;/</c> bytes, resolved through <see cref="ModuleBundleSource"/> (which
    /// refuses uninstalled and framework-stale landings). Empty for a package that declares no
    /// module or whose bytes this instance cannot serve — the bundle then simply has no module
    /// section, which a consumer reads as "nothing to land".
    /// </summary>
    private static IObservable<IReadOnlyList<string>> ModuleFiles(
        IMessageHub rootHub, BundleEntry package)
    {
        var landing = rootHub.ServiceProvider.GetService<ModuleLandingService>();
        if (landing is null || string.IsNullOrWhiteSpace(package.Module))
            return Observable.Return<IReadOnlyList<string>>([]);

        var logger = rootHub.ServiceProvider.GetService<ILoggerFactory>()
            ?.CreateLogger(typeof(PluginBundleEndpoints));

        return landing.GetActivation().Take(1)
            .Select(activation =>
            {
                var (files, decline) = ModuleBundleSource.Collect(
                    landing.BaseDirectory, package.Module!, activation,
                    ModulePlatformFloor.DeclineReason);
                if (decline is not null)
                    logger?.LogInformation(
                        "Plugin bundles: {Plugin} declares module '{Module}' but it is not served: {Reason}",
                        package.PluginId, package.Module, decline);
                return files;
            });
    }

    /// <summary>
    /// The framework identity these assemblies are bound to — the RESOLVED framework build
    /// identity of this process (<see cref="PrebuiltAssemblySeeder.LiveFrameworkMvid"/>: surface
    /// hash on manifest-bearing portals, not a raw assembly MVID).
    ///
    /// <para>🚨 This used to derive MeshWeaver.Graph's raw MVID "because FrameworkVersion is
    /// internal" — correct while the identity WAS that MVID, silently broken by #1696: portals
    /// resolve the s&lt;hash&gt; surface identity, so every registry-served bundle recorded a
    /// mismatched identity and the consumer's gate DECLINED it (adoption quietly regressed to
    /// compile-everything). LiveFrameworkMvid is the public reading of the one resolution —
    /// producer and gate can no longer disagree.</para>
    /// </summary>
    private static string FrameworkMvid => PrebuiltAssemblySeeder.LiveFrameworkMvid;

    /// <summary>
    /// 🚨 Buffered, not streamed straight to the response: <see cref="NuGetPackageWriter"/> writes a
    /// ZIP central directory at the END, so a half-written archive cannot be un-sent once bytes have
    /// gone out. A failure mid-assembly must become a clean 500, not a truncated archive the caller
    /// caches as valid.
    /// </summary>
    private static IResult BuildResult(
        BundleEntry package,
        IReadOnlyList<(string NodePath, string? Path, IReadOnlyDictionary<string, string>? Dependencies)> assemblies,
        IReadOnlyList<string> moduleFiles)
    {
        var entries = new List<NuGetPackageWriter.Entry>();
        var assemblyRecords = new List<object>();

        foreach (var (nodePath, path, dependencies) in assemblies.Where(a => a.Path is not null))
        {
            // EntryPathFor states the naming rule (and why it is not slash-replaced) once, shared
            // with the reader. The manifest still carries the mapping — the consumer must read the
            // node path the producer wrote, never recover it from a file name.
            var local = path!;
            entries.Add(new NuGetPackageWriter.Entry(
                NuGetPackageWriter.EntryPathFor(nodePath), () => File.OpenRead(local)));
            // The per-type dependency record (#1707 slice 2) rides the manifest so the consumer
            // validates module/toolchain bindings before adopting and stamps them on adopt.
            assemblyRecords.Add(new { nodePath, assembly = $"{nodePath}.dll", dependencies });
        }

        // The module closure, under its own folder (#1664): these bytes land beside the consumer's
        // app via ModuleLandingService, a different lane than the assembly store above — the two
        // must never mix (a module DLL seeded as a NodeType assembly fails only at activation).
        foreach (var moduleFile in moduleFiles)
        {
            var local = moduleFile;
            entries.Add(new NuGetPackageWriter.Entry(
                NuGetPackageWriter.ModuleEntryPathFor(Path.GetFileName(local)),
                () => File.OpenRead(local)));
        }

        var manifest = new PackagingManifest(
            package.PluginId, package.PackageId, package.Version, package.PluginId, null, []);

        var manifestJson = JsonSerializer.Serialize(
            new
            {
                plugin = package.PluginId,
                version = package.Version,
                // The framework this instance RUNS — these assemblies came out of its bake (and its
                // own modules/ tree), so that is the only framework they are known good against.
                frameworkMvid = FrameworkMvid,
                assemblies = assemblyRecords,
                // The module section — the manifest names every closure file; a consumer reads
                // this list, never the folder (BundleReader.ReadModule). Null (omitted) when the
                // bundle carries no module. minMeshVersion is the consumer's landing gate (a
                // semver floor — MVID equality is the NodeType lane's gate, and the frameworkMvid
                // above is, for the module, diagnostics).
                module = moduleFiles.Count == 0
                    ? null
                    : new
                    {
                        assemblyName = package.Module,
                        assemblies = moduleFiles.Select(Path.GetFileName).ToArray(),
                        minMeshVersion = package.MinMeshVersion,
                    },
            },
            new JsonSerializerOptions
            {
                WriteIndented = true,
                DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
            });

        var buffer = new MemoryStream();
        NuGetPackageWriter.Write(buffer, manifest, "3.0.0", entries, manifestJson);
        buffer.Position = 0;

        return Results.File(buffer, "application/octet-stream",
            $"{package.PackageId}.{package.Version}.nupkg");
    }

    /// <summary>
    /// The plugins this instance can serve, from the install records in the <c>Plugins</c>
    /// partition — the same records the catalog reads.
    ///
    /// <para>The version is <c>ReleasedVersion</c>, off the module's <c>manifest.lock</c>. Neither
    /// neighbour substitutes: <c>Version</c> is the whole-repo commit sha and <c>ModuleVersion</c>
    /// is a content hash, which is exact but unordered — it cannot answer "which is newer", the one
    /// question a distribution index is asked.</para>
    /// </summary>
    private static IObservable<IReadOnlyList<BundleEntry>> InstalledPackages(
        IMessageHub rootHub, CancellationToken ct) =>
        rootHub.ServiceProvider.GetRequiredService<IMeshService>()
            .Query<MeshNode>(MeshQueryRequest.FromQuery(
                $"namespace:{PackageInstaller.InstalledPartition} "
                + $"nodeType:{PackageInstaller.PackageNodeType}"))
            .Where(c => c.ChangeType == QueryChangeType.Initial)
            .Select(c => (IReadOnlyList<BundleEntry>)c.Items
                .Select(node => node.ContentAs<PackageManifest>(
                    rootHub.JsonSerializerOptions))
                .Where(m => m is not null && !string.IsNullOrWhiteSpace(m.ReleasedVersion))
                .Select(m => new BundleEntry(
                    PackagingManifest.IdPrefix + m!.Id, m.ReleasedVersion!, m.Id, m.Module,
                    m.MinMeshVersion))
                .OrderBy(e => e.PluginId, StringComparer.OrdinalIgnoreCase)
                .ToArray());

    /// <summary>One servable bundle: its package id, its released version, the plugin it is, the
    /// compiled module the plugin's install record declares (null for content-only plugins), and
    /// that module's declared platform floor.</summary>
    private sealed record BundleEntry(
        string PackageId, string Version, string PluginId, string? Module = null,
        string? MinMeshVersion = null);
}
