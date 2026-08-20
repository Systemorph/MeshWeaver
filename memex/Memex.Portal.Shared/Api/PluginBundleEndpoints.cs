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
using Microsoft.Extensions.Configuration;
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
///
/// <para>🚨 <b>The key is TWO decisions, and for a long time only the first one ran (#1772).</b> The
/// filter below authenticates — a valid <c>mwi_</c> key or 401 — and <see cref="IsGranted"/>
/// authorizes, per package, on <b>both</b> routes. Until #1772 the authenticated caller was written
/// into <see cref="HttpContext.Items"/> and never read back, so any registered instance could
/// download every installed package's bundle, paid courses included, while this very paragraph said
/// otherwise. An instance key is provisioned to every registered installation; it is identity, never
/// entitlement. The grant model is <see cref="PluginGrantEntry"/> matched against the install
/// record's <see cref="PackageManifest.Source"/> — the same match <c>InstallByDefault</c> and
/// <c>/api/plugins</c> make, so there is exactly one thing to keep honest.</para>
///
/// <para>🚨 <b>A refusal is byte-identical to "no such bundle"</b> (<see cref="NoSuchBundle"/>), and
/// an ungranted package is absent from the index. The URL scheme is fully predictable, so a
/// distinguishable refusal would be an inventory oracle over the whole catalogue — the same reasoning
/// that made <c>/api/content</c>'s refusal identical to its not-found (#587).</para>
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
            Index(http, RootHub(http), Caller(http), ct));

        // 🚨 `identity`/`arch` are the CONSUMER's lane (#1751), not a filter the caller invents: they
        // say which framework build identity and which architecture the caller can actually run, and
        // the assemblies are resolved through each NodeType's Release node for exactly that pair.
        // Absent ⇒ this instance's own lane, which is what every pre-#1751 client asks for, so the
        // route's behaviour for them is unchanged.
        group.MapGet("/{plugin}/{version}",
            (HttpContext http, string plugin, string version, CancellationToken ct) =>
                Bundle(
                    RootHub(http), plugin, version,
                    Requested(http, "identity", FrameworkMvid),
                    Requested(http, "arch", ReleaseArchitecture.Live),
                    Caller(http),
                    ct));

        MapPublish(endpoints);
        return endpoints;
    }

    /// <summary>
    /// The PUBLISH route — the registry's acquisition half (#1664 step 13).
    ///
    /// <para>A registry serves the module bytes it itself runs, and that folder is written by the
    /// platform image's publish layout. So a module whose source has LEFT the platform repo could
    /// be packed, declared and installed — and still reach nobody. This is how the repo that owns
    /// it hands the bytes over: <c>POST /api/plugins/bundles/{plugin}</c> with the packed
    /// <c>.module.nupkg</c>, after which the existing serve → fetch → floor-gate → land chain
    /// carries it to every consumer unchanged.</para>
    ///
    /// <para>🚨 <b>Mapped only when a publish token is configured</b>
    /// (<see cref="ModulePublish.TokenConfigKey"/>) — an unconfigured registry has no publish
    /// surface at all, rather than one that answers 401. Same shape as the log-ingest route, and
    /// for the same reason: the caller is a build, not a signed-in user, so this is deliberately
    /// NOT the instance-key group above — a read grant says which packages an instance may PULL,
    /// while publishing writes bytes every consumer will then load.</para>
    /// </summary>
    private static void MapPublish(IEndpointRouteBuilder endpoints)
    {
        var config = endpoints.ServiceProvider.GetService<IConfiguration>();
        var token = config?[ModulePublish.TokenConfigKey];
        if (string.IsNullOrWhiteSpace(token))
            return;

        endpoints.MapPost($"{RoutePrefix}/{{plugin}}", async (
                HttpContext http, string plugin, CancellationToken ct) =>
            {
                var logger = http.RequestServices.GetService<ILoggerFactory>()
                    ?.CreateLogger(typeof(PluginBundleEndpoints));

                if (ModulePublish.DeclineAuthorization(token, http.Request.Headers.Authorization) is { } denied)
                {
                    logger?.LogWarning("Module publish for {Plugin} REJECTED: {Reason}", plugin, denied);
                    return Results.Json(new { error = denied }, statusCode: StatusCodes.Status401Unauthorized);
                }

                var landing = http.RequestServices.GetService<ModuleLandingService>()
                              ?? RootHub(http).ServiceProvider.GetService<ModuleLandingService>();
                if (landing is null)
                    return Results.Json(
                        new { error = "this instance cannot land modules (no ModuleLandingService)" },
                        statusCode: StatusCodes.Status503ServiceUnavailable);

                using var buffer = new MemoryStream();
                await http.Request.Body.CopyToAsync(buffer, ct);
                var bytes = buffer.ToArray();
                if (bytes.Length == 0)
                    return Results.Json(new { error = "the request carried no bundle" },
                        statusCode: StatusCodes.Status400BadRequest);

                BundleReader.Manifest? manifest;
                IReadOnlyList<BundleReader.ModuleFile> files;
                try
                {
                    (manifest, files) = BundleReader.ReadModule(bytes);
                }
                catch (Exception exception)
                {
                    logger?.LogWarning(exception, "Module publish for {Plugin}: unreadable bundle", plugin);
                    return Results.Json(new { error = "the upload is not a readable module bundle" },
                        statusCode: StatusCodes.Status400BadRequest);
                }

                var (accepted, decline) = ModulePublish.Validate(
                    plugin, manifest, files, http.Request.Query["version"],
                    http.Request.Query["packagePath"]);
                if (accepted is null)
                {
                    logger?.LogWarning("Module publish for {Plugin} REFUSED: {Reason}", plugin, decline);
                    return Results.Json(new { error = decline }, statusCode: StatusCodes.Status400BadRequest);
                }

                try
                {
                    // The floor gate and the same-identity trap-door hold HERE, at placement —
                    // one owner for each rule. A refusal surfaces as the observable's error.
                    await landing.LandModule(
                        accepted.Module, accepted.Files,
                        frameworkMvid: accepted.FrameworkMvid,
                        packagePath: accepted.PackagePath,
                        version: accepted.Version,
                        minMeshVersion: accepted.MinMeshVersion)
                        .FirstAsync().ToTask(ct);
                }
                catch (Exception exception)
                {
                    logger?.LogWarning(exception,
                        "Module publish for {Plugin}: landing refused '{Module}'", plugin, accepted.Module);
                    return Results.Json(new { error = exception.Message },
                        statusCode: StatusCodes.Status409Conflict);
                }

                logger?.LogInformation(
                    "Module publish: landed '{Module}' for {Plugin} ({Files} file(s), version {Version}, "
                    + "floor {Floor}) — it serves from this registry and loads here on the next restart",
                    accepted.Module, plugin, accepted.Files.Count,
                    accepted.Version ?? "(unversioned)", accepted.MinMeshVersion ?? "(none)");

                return Results.Json(new
                {
                    plugin,
                    module = accepted.Module,
                    version = accepted.Version,
                    files = accepted.Files.Count,
                    pendingRestart = true,
                });
            })
            .AllowAnonymous();
    }

    /// <summary>
    /// The authenticated caller the filter stamped, or <c>null</c>.
    ///
    /// <para>🚨 Null can only mean the filter did not run — it has no anonymous branch, unlike
    /// <c>/api/plugins</c>. It is therefore treated as "granted nothing" rather than "unscoped", so a
    /// route accidentally mapped outside the group serves an empty index and a 404, never the whole
    /// catalogue. The absence of an answer is never a yes (#1772).</para>
    /// </summary>
    private static AuthenticatedInstance? Caller(HttpContext http) =>
        http.Items.TryGetValue(CallerItemKey, out var value) ? value as AuthenticatedInstance : null;

    /// <summary>
    /// 🚨 <b>THE PER-PACKAGE AUTHORIZATION</b> (#1772) — whether <paramref name="caller"/> may pull
    /// <paramref name="package"/>, decided by the admin-owned <see cref="PluginGrant"/> its key
    /// resolved to.
    ///
    /// <para>The grant is a set of <c>(source, package)</c> pairs, so the install record's
    /// <see cref="PackageManifest.Source"/> — the registry source it was installed FROM — is what the
    /// entry is matched against. Exactly the reading <c>PluginRegistryEndpoints</c> applies to its
    /// listing and <c>InstanceAutoRegistrationService</c> applies to <c>InstallByDefault</c>; a
    /// second, bundle-specific notion of entitlement would be a second thing to keep in step with
    /// what purchases are recorded against.</para>
    ///
    /// <para>🚨 <b>An unstamped source fails CLOSED.</b> A record whose <c>Source</c> is null (one
    /// written before the field existed, or installed from something that is not a configured
    /// registry source) matches no entry at all — <see cref="PluginGrantEntry.Matches"/> compares the
    /// source name, and <c>""</c> equals none of them. "Cannot determine" is a refusal, never a pass.
    /// The consumer's own <c>PluginBundleClient</c> reads the resulting 404 as "no prebuilt bundle —
    /// will compile", so the cost of that refusal is a compile, never a failed install.</para>
    /// </summary>
    private static bool IsGranted(AuthenticatedInstance? caller, BundleEntry package) =>
        caller is not null && caller.Allows(package.Source ?? "", package.PluginId);

    /// <summary>
    /// 🚨 The ONE answer for a bundle this caller cannot have — used for "no such package", "no such
    /// version" and "not granted to you" alike, so the three are byte-identical: same status, same
    /// (empty) body, same headers.
    ///
    /// <para>The URL is <c>/{plugin}/{version}</c> and every plugin id in the catalogue is public
    /// knowledge, so a distinguishable refusal (403, or a 404 with a different body) would let any
    /// registered instance enumerate what this registry carries and at which versions — the exact
    /// existence oracle <c>/api/content</c> closed in #587 by making its refusal identical to its
    /// not-found. WHICH of the three it was is written to the LOG, where the caller cannot read
    /// it.</para>
    /// </summary>
    private static IResult NoSuchBundle() => Results.NotFound();

    /// <summary>One query-string value, or the serving instance's own value when the caller did not
    /// state one. Blank is treated as absent — an empty <c>?identity=</c> is a client bug, and
    /// answering it with "nothing resolves" would look identical to an incompatible lane.</summary>
    private static string Requested(HttpContext http, string key, string fallback) =>
        http.Request.Query[key].ToString() is { Length: > 0 } value && !string.IsNullOrWhiteSpace(value)
            ? value
            : fallback;

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
    ///
    /// <para>🚨 <b>Scoped to what the caller was GRANTED</b> (#1772, <see cref="IsGranted"/>) — an
    /// ungranted package is simply not listed, so a caller cannot even learn it is installed here.
    /// That is what makes the download route's refusal non-informative: with the index filtered, "not
    /// in your index" and "404 on fetch" agree, and neither confirms existence. A caller granted
    /// nothing gets an empty <c>bundles</c> array, indistinguishable from a registry with nothing
    /// installed.</para>
    /// </summary>
    private static Task<IResult> Index(
        HttpContext http, IMessageHub rootHub, AuthenticatedInstance? caller, CancellationToken ct)
    {
        var baseUrl = $"{http.Request.Scheme}://{http.Request.Host}{RoutePrefix}";

        return ServableEntries(rootHub, ct)
            .Select(servable =>
            {
                var granted = (IReadOnlyList<BundleEntry>)servable
                    .Where(p => IsGranted(caller, p)).ToArray();
                WarnAboutAnEmptyIndex(rootHub, caller, servable, granted);
                return granted;
            })
            .SelectMany(packages => ServableModules(rootHub, packages)
                .Select(modules => Results.Json(new
                {
                    frameworkMvid = FrameworkMvid,
                    // The architecture that identity belongs to (#1751). The identity already FOLDS
                    // the architecture in — the amd64 and arm64 variants of one image resolve
                    // different identities — but it is opaque, so a consumer on the other variant
                    // sees only "not adoptable" with no way to tell an incompatible framework from
                    // the wrong lane. Stating the architecture makes that miss diagnosable. Additive:
                    // a pre-#1751 client ignores it, and its BundleIndex simply reads null.
                    architecture = ReleaseArchitecture.Live,
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
    /// 🚨 Names, on the index request, every install record with NO <see cref="BundleEntry.Source"/>
    /// — the records <see cref="IsGranted"/> can never match, and which are therefore servable to
    /// nobody.
    ///
    /// <para>It belongs HERE rather than only on the download path, because the filtered index means
    /// that path is never reached for such a record: a consumer polls the index, does not see the
    /// package, and never asks for it. Without this line the whole distribution lane could go dark
    /// with nothing in the log at all — every consumer just quietly compiling, which looks exactly
    /// like a healthy day. Normally this logs nothing, since a record installed through any registry
    /// lane carries its source.</para>
    /// </summary>
    /// <summary>
    /// Everything this registry can serve: its own INSTALL RECORDS (the catalog lane) UNIONED
    /// with the modules PUBLISHED onto it (the activation sidecar — entries whose
    /// <c>PackagePath</c> stamps the source a <c>PluginGrant</c> matches on). The union is what
    /// makes a GitSync-native registry serve at all: memex-cloud provisions its packages as
    /// Spaces and never runs the catalog install, so it has NO install records — with a
    /// record-only index its bundle feed was permanently empty and every consumer read
    /// SkipNoBundle (found live, 2026-08-20, the first real remote consumer). Records win on a
    /// PluginId collision — a deliberate install is more intentional than a publish.
    /// </summary>
    private static IObservable<IReadOnlyList<BundleEntry>> ServableEntries(
        IMessageHub rootHub, CancellationToken ct) =>
        InstalledPackages(rootHub, ct)
            .Do(packages => WarnAboutUnstampedRecords(rootHub, packages))
            .SelectMany(records =>
            {
                var landing = rootHub.ServiceProvider.GetService<ModuleLandingService>();
                if (landing is null)
                    return Observable.Return(records);
                return landing.GetActivation().Take(1).Select(activation =>
                {
                    var recorded = records.Select(r => r.PluginId)
                        .ToHashSet(StringComparer.OrdinalIgnoreCase);
                    var published = activation.Entries
                        .Where(e => e.Enabled
                            && !string.IsNullOrWhiteSpace(e.Version)
                            && e.PackagePath?.Split('/') is { Length: 2 } segments
                            && !recorded.Contains(segments[1]))
                        .Select(e =>
                        {
                            var segments = e.PackagePath!.Split('/');
                            return new BundleEntry(
                                segments[1], e.Version!, segments[1],
                                Module: e.Name,
                                MinMeshVersion: e.MinMeshVersion,
                                Source: segments[0]);
                        });
                    return (IReadOnlyList<BundleEntry>)records.Concat(published).ToArray();
                });
            });

    /// <summary>
    /// 🚨 Says so when an index request serves NOTHING — the one outcome that is silent on both
    /// sides and looks exactly like a healthy day.
    ///
    /// <para>A consumer that receives <c>{"bundles": []}</c> concludes <c>SkipNoBundle</c> for every
    /// module, lands nothing, and logs nothing worth reading; the registry logs nothing at all. That
    /// is how the whole distribution lane went dark on 2026-08-20 with every consumer quietly
    /// compiling: the <c>Plugins</c> partition was invisible to the record query, so
    /// <see cref="InstalledPackages"/> returned an empty list and the emptiness had no author
    /// (#1950). The two empties need different responses, so they get different lines: nothing
    /// SERVABLE at all points at this instance's own state, while entries filtered down to zero
    /// points at the caller's grant.</para>
    /// </summary>
    private static void WarnAboutAnEmptyIndex(
        IMessageHub rootHub, AuthenticatedInstance? caller,
        IReadOnlyList<BundleEntry> servable, IReadOnlyList<BundleEntry> granted)
    {
        if (granted.Count > 0)
            return;

        var logger = rootHub.ServiceProvider.GetService<ILoggerFactory>()
            ?.CreateLogger(typeof(PluginBundleEndpoints));
        if (logger is null)
            return;

        if (servable.Count == 0)
            logger.LogWarning(
                "Plugin bundles: serving an EMPTY index to {Instance} — this instance has nothing "
                + "servable at all (no install records in the '{Partition}' partition and no "
                + "published module entries). Every consumer will read SkipNoBundle for every "
                + "package. If packages ARE installed here, the records partition is not readable "
                + "by the query — check that its '_Policy' exists as a DURABLE node (#1950).",
                caller?.Instance.InstanceId ?? "an unauthenticated caller",
                PackageInstaller.InstalledPartition);
        else
            logger.LogWarning(
                "Plugin bundles: serving an EMPTY index to {Instance} — {Count} servable "
                + "package(s) exist here, but none is covered by that instance's grant, so it will "
                + "read SkipNoBundle for every package. Grant it the sources it needs.",
                caller?.Instance.InstanceId ?? "an unauthenticated caller", servable.Count);
    }

    private static void WarnAboutUnstampedRecords(
        IMessageHub rootHub, IReadOnlyList<BundleEntry> packages)
    {
        var unstamped = packages.Where(p => string.IsNullOrWhiteSpace(p.Source)).ToArray();
        if (unstamped.Length == 0)
            return;

        rootHub.ServiceProvider.GetService<ILoggerFactory>()
            ?.CreateLogger(typeof(PluginBundleEndpoints))
            .LogWarning(
                "Plugin bundles: {Count} install record(s) carry no registry source, so no "
                + "PluginGrant can match them and their bundles are servable to NO instance: "
                + "{Packages}. Re-install them through the registry to stamp the source (#1772).",
                unstamped.Length,
                string.Join(", ", unstamped.Select(p => p.PluginId).Take(MissesReported)));
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
    ///
    /// <para>🚨 <b>The caller's grant is part of the RESOLUTION, not a check bolted on after it</b>
    /// (#1772) — an ungranted package does not match, so there is no branch in which bytes are
    /// assembled first and refused later, and nothing downstream has to remember to ask. Every miss
    /// answers <see cref="NoSuchBundle"/>; which of the three it was goes to the log only.</para>
    /// </summary>
    private static Task<IResult> Bundle(
        IMessageHub rootHub, string plugin, string version, string identity, string architecture,
        AuthenticatedInstance? caller, CancellationToken ct) =>
        ServableEntries(rootHub, ct)
            .SelectMany(packages =>
            {
                // Named apart from the query-string reader Requested(HttpContext, …) above — one
                // shadowing the other reads as a call to the wrong thing.
                bool IsAsked(BundleEntry p) =>
                    string.Equals(p.PluginId, plugin, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(p.Version, version, StringComparison.OrdinalIgnoreCase);

                var match = packages.FirstOrDefault(p => IsAsked(p) && IsGranted(caller, p));
                if (match is not null)
                    return Assemble(rootHub, match, identity, architecture);

                // The refusal is uniform on the wire; the LOG is where it is diagnosable, naming
                // which instance asked and whether the record exists at all — including the
                // fail-closed case an operator would otherwise chase for hours: an install record
                // with no Source stamped can be granted by nobody (see IsGranted).
                var installed = packages.FirstOrDefault(IsAsked);
                rootHub.ServiceProvider.GetService<ILoggerFactory>()
                    ?.CreateLogger(typeof(PluginBundleEndpoints))
                    .LogWarning(
                        "Plugin bundles: {Plugin}@{Version} refused for instance {Instance} — {Reason}",
                        plugin, version, caller?.Instance.InstanceId ?? "(none)",
                        installed is null
                            ? "no such install record on this instance"
                            : installed.Source is { Length: > 0 } source
                                ? $"installed from source '{source}', which this instance's grant does not cover"
                                : "the install record carries NO source, so no grant entry can match it "
                                  + "(re-install it through the registry to stamp one)");
                return Observable.Return(NoSuchBundle());
            })
            .FirstAsync()
            .ToTask(ct);

    /// <summary>
    /// Reads the plugin's nodes and their assemblies, then builds the archive.
    ///
    /// <para>🚨 <b>A caller on ANOTHER lane is answered by resolving through each NodeType's
    /// <c>Release</c> node (#1751)</b> — the release records, per framework identity and per
    /// architecture, which assembly-store version holds bytes PROVEN built for that lane. That is
    /// what lets this route serve at all across lanes: the amd64 and arm64 variants of one image
    /// resolve different framework identities, so before the link existed an arm64 caller could only
    /// be told "not adoptable" and never "here is yours". The <c>Release/</c> nodes arrive on the
    /// very same subtree read the NodeTypes do, so resolving through them costs no extra query.</para>
    ///
    /// <para>🚨 <b>The instance's OWN lane is still served from <c>LastCompiledVersion</c>, and is
    /// checked first.</b> Not a legacy fallback — the correct answer. A Release record can legitimately
    /// LAG the current build (adoption stamps <c>LastCompiledVersion</c> without minting a release at
    /// all), so resolving the own-lane case through releases would quietly ship an older assembly than
    /// this portal itself runs. On its own lane the identity claim is true by construction; off it,
    /// only a record written beside the bytes can make that claim, and a type without one contributes
    /// nothing and is COUNTED as a miss.</para>
    /// </summary>
    private static IObservable<IResult> Assemble(
        IMessageHub rootHub, BundleEntry package, string identity, string architecture)
    {
        var meshService = rootHub.ServiceProvider.GetRequiredService<IMeshService>();
        var store = rootHub.ServiceProvider.GetService<IAssemblyStore>() ?? NullAssemblyStore.Instance;
        var options = rootHub.JsonSerializerOptions;
        var logger = rootHub.ServiceProvider.GetService<ILoggerFactory>()
            ?.CreateLogger(typeof(PluginBundleEndpoints));
        var servesOwnLane =
            string.Equals(identity, FrameworkMvid, StringComparison.Ordinal)
            && ReleaseArchitecture.Matches(ReleaseArchitecture.Live, architecture);

        return meshService
            .Query<MeshNode>(MeshQueryRequest.FromQuery(
                $"namespace:{package.PluginId} scope:subtree"))
            .Where(c => c.ChangeType == QueryChangeType.Initial)
            .Take(1)
            .SelectMany(change =>
            {
                var nodes = change.Items.ToArray();
                var releases = ReleasesByType(nodes, options);
                var misses = new List<string>();

                // Each NodeType contributes the assembly the bake produced for it. A type with no
                // usable build contributes nothing rather than failing the bundle: a plugin whose
                // types are still compiling is incomplete, not broken, and the caller can retry.
                // The manifest lists what IS here, so a consumer can tell the difference.
                var lookups = nodes
                    .Select(n => (Node: n, Definition: n.ContentAs<NodeTypeDefinition>(options)))
                    .Where(x => x.Definition?.LastCompiledVersion is not null)
                    .Select(x => (x.Node, x.Definition, Version: ResolveStoreVersion(
                        x.Node.Path, x.Definition!, releases, identity, architecture, servesOwnLane,
                        misses)))
                    .Where(x => x.Version is not null)
                    .Select(x => store
                        .TryGetAssemblyPath(x.Node.Path, x.Version!.Value)
                        .Take(1)
                        .Catch<string?, Exception>(_ => Observable.Return<string?>(null))
                        .Select(path => (x.Node.Path, Path: path,
                            Dependencies: (IReadOnlyDictionary<string, string>?)x.Definition!.CompiledDependencies)))
                    .ToArray();

                var assemblies = lookups.Length == 0
                    ? Observable.Return(
                        Array.Empty<(string NodePath, string? Path, IReadOnlyDictionary<string, string>? Dependencies)>())
                    : lookups.CombineLatest().Select(x => x.ToArray());

                if (misses.Count > 0)
                    // 🚨 LOUD and COUNTABLE. A fetch that quietly yields nothing is indistinguishable
                    // from one that yielded everything, and the adopted-vs-compiled count is the only
                    // evidence the distribution lane works at all.
                    logger?.LogWarning(
                        "Plugin bundles: {Plugin} could resolve NO artifact for {Missed} of its "
                        + "NodeType(s) on framework {Identity}/{Architecture} — those types are NOT "
                        + "in the bundle and the consumer will compile them: {Misses}",
                        package.PluginId, misses.Count, identity, architecture,
                        string.Join(" | ", misses.Take(MissesReported)));

                return assemblies.SelectMany(found => ModuleFiles(rootHub, package)
                    .Select(moduleFiles =>
                        BuildResult(package, found, moduleFiles, identity, architecture, misses)));
            });
    }

    /// <summary>How many per-type misses the warning names before it truncates — enough to diagnose,
    /// bounded so a wholesale lane mismatch cannot write a log line per type.</summary>
    private const int MissesReported = 10;

    /// <summary>
    /// The <c>Release</c> nodes from a package's subtree, grouped by the NodeType they belong to.
    /// Keyed off the release CONTENT's <c>NodeTypePath</c> rather than the node's path, because the
    /// content is what the resolver reasons about and a path-derived key would silently disagree with
    /// it if the release were ever moved.
    /// </summary>
    private static IReadOnlyDictionary<string, IReadOnlyList<NodeTypeRelease>> ReleasesByType(
        IReadOnlyList<MeshNode> nodes, System.Text.Json.JsonSerializerOptions options) =>
        nodes
            .Where(n => string.Equals(
                n.NodeType, ReleaseNodeType.NodeType, StringComparison.OrdinalIgnoreCase))
            .Select(n => n.ContentAs<NodeTypeRelease>(options))
            .Where(r => r is { NodeTypePath.Length: > 0 })
            .GroupBy(r => r!.NodeTypePath, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                g => g.Key,
                g => (IReadOnlyList<NodeTypeRelease>)g.Select(r => r!).ToArray(),
                StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Which assembly-store version to serve for one NodeType in the caller's lane, or null when
    /// nothing may be served for it (the miss is appended to <paramref name="misses"/>).
    /// </summary>
    /// <remarks>Internal rather than private for <c>PluginBundleLaneResolutionTest</c>: this is the
    /// whole distribution decision and both of its branches fail SILENTLY when wrong — serving a
    /// lagging build looks healthy, and serving a foreign lane's build fails only at activation.
    /// Reaching it through an HTTP round trip would need a live portal to pin a pure choice.</remarks>
    internal static long? ResolveStoreVersion(
        string nodePath,
        NodeTypeDefinition definition,
        IReadOnlyDictionary<string, IReadOnlyList<NodeTypeRelease>> releases,
        string identity,
        string architecture,
        bool servesOwnLane,
        List<string> misses)
    {
        // 🚨 OWN LANE FIRST, and deliberately not through the releases. The bytes this portal runs
        // were produced (or adopted) under its own live identity, so the claim is true by
        // construction — and they are the CURRENT build, which a Release record can legitimately lag:
        // PrebuiltAssemblySeeder stamps LastCompiledVersion without minting a release at all, so a
        // release-first rule would ship the older assembly of the two while looking perfectly
        // healthy. This branch is byte-for-byte the pre-#1751 behaviour.
        if (servesOwnLane)
            return definition.LastCompiledVersion;

        var candidates = releases.TryGetValue(nodePath, out var forType) ? forType : [];
        var match = ReleaseArtifactResolver.Resolve(candidates, identity, architecture);

        // Off this instance's lane, ONLY a record written beside the bytes can prove them. The
        // artifact's own store version, and never LastCompiledVersion as a fallback: that is a
        // DIFFERENT lane's build, and handing it over under the requested identity is exactly the
        // unprovable adoption the whole gate exists to prevent.
        if (match.IsResolved && match.Artifact!.AssemblyStoreVersion is { } storeVersion)
            return storeVersion;

        misses.Add($"{nodePath}: {(match.IsResolved
            ? "an artifact was recorded for this lane but without an assembly-store version, so its "
              + "bytes cannot be located"
            : match.DeclineReason)}");
        return null;
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
        IReadOnlyList<string> moduleFiles,
        string identity,
        string architecture,
        IReadOnlyList<string> misses)
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
                // 🚨 The framework identity these bytes were RESOLVED for (#1751) — every assembly
                // above either carries a Release artifact recorded under exactly this identity, or
                // was served on this instance's own lane, where this equals FrameworkMvid. It is
                // never inferred: a lane that could not be proven contributed no bytes at all, so
                // the claim is always backed by the record the producer wrote beside them.
                frameworkMvid = identity,
                // The architecture that identity belongs to, so a consumer's decline can name the
                // lane instead of only the opaque hash.
                architecture,
                assemblies = assemblyRecords,
                // NodeTypes this bundle could NOT cover for the requested lane, each with its reason.
                // Carried in the manifest — not merely logged on the server — so the CONSUMER can
                // count and surface the miss too: a fetch that silently returns fewer assemblies
                // than the package has types is exactly how adoption regresses unnoticed.
                misses = misses.Count == 0 ? null : misses.ToArray(),
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
    ///
    /// <para>🚨 <b>Unscoped by design — every caller of this method must apply
    /// <see cref="IsGranted"/>.</b> It is the full inventory, which is precisely what no caller may
    /// see; the grant filter lives at the two route handlers because the download route also needs
    /// the unfiltered list to write a diagnosable log line. The <see cref="BundleEntry.Source"/> each
    /// entry carries is the only input that decision needs.</para>
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
                    m.MinMeshVersion, m.Source))
                .OrderBy(e => e.PluginId, StringComparer.OrdinalIgnoreCase)
                .ToArray());

    /// <summary>One servable bundle: its package id, its released version, the plugin it is, the
    /// compiled module the plugin's install record declares (null for content-only plugins), that
    /// module's declared platform floor, and the registry SOURCE the record was installed from.</summary>
    /// <param name="PackageId">The NuGet-shaped package id the archive is named after.</param>
    /// <param name="Version">The released SemVer this bundle is served at.</param>
    /// <param name="PluginId">The plugin's catalog id — also the id a grant entry names.</param>
    /// <param name="Module">The compiled module the install record declares, or null.</param>
    /// <param name="MinMeshVersion">The module's declared platform floor, or null.</param>
    /// <param name="Source">🚨 The registry source name the package was installed from
    /// (<see cref="PackageManifest.Source"/>) — the half of the grant pair that is NOT the package
    /// id, and therefore the input to <see cref="IsGranted"/>. Null on a record written before the
    /// field existed or installed from something that is not a configured source: it then matches no
    /// grant entry, which is the fail-closed answer (#1772).</param>
    private sealed record BundleEntry(
        string PackageId, string Version, string PluginId, string? Module = null,
        string? MinMeshVersion = null, string? Source = null);
}
