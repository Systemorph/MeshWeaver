using System.Reactive.Linq;
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
/// filter below authenticates — a valid <c>mwi_</c> key or 401 — and <see cref="Decide"/>
/// authorizes, per package, on <b>both</b> routes. Until #1772 the authenticated caller was written
/// into <see cref="HttpContext.Items"/> and never read back, so any registered instance could
/// download every installed package's bundle, paid courses included, while this very paragraph said
/// otherwise. An instance key is provisioned to every registered installation; it is identity, never
/// entitlement. The grant model is <see cref="PluginGrantEntry"/> — the same match
/// <c>InstallByDefault</c> and <c>/api/plugins</c> make, so there is exactly one thing to keep
/// honest.</para>
///
/// <para>🚨 <b>The (source, package) binding the grant is matched against is anchored on the
/// REGISTRY (#1782 gap 2), not on this instance's install records.</b> It used to come from the
/// record's <see cref="PackageManifest.Source"/> and from nowhere else, which made two things true
/// that should not have been: a package this instance had not itself installed could not be served
/// at all (however plainly its content sat here), and "I cannot tell which source this is from" was
/// answered as "you are not entitled to it". The record is now read as what it is — a CACHE of the
/// registry's binding — and its absence sends the question upstream. See
/// <see cref="PackageEntitlementAnchor"/> for the three outcomes and
/// <see cref="PackageOriginAnchor"/> for the read; the GRANT match itself is untouched.</para>
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

    /// <summary><see cref="HttpContext.Items"/> key holding the authenticated BUILD principal — a
    /// different caller class from an installation, so a different slot (#2483).</summary>
    private const string BuildCallerItemKey = "PluginBundle.BuildCaller";

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

            var outcome = (await authenticator.AuthenticateOutcome(http.Request.Headers.Authorization)
                .FirstAsync()
                .ObserveCompletion(
                    ex => logger?.LogWarning(ex,
                        "Plugin bundles: instance authentication for {Path} faulted after the request had already been answered",
                        http.Request.Path),
                    http.RequestAborted))!;

            if (outcome.IsUnavailable)
                return InstanceAuthResponses.Unavailable(http, outcome.UnavailableReason, logger);

            var caller = outcome.Instance;
            var build = outcome.Build;
            // 🚨 A BUILD principal (#2483) is admitted on the PREBUILT routes ONLY. It is a CI run,
            // not an installation: it has no instance record, no plan and no PluginGrant, so every
            // other bundle route — which decides per package against exactly those — keeps refusing
            // it with the same 401 as before. Narrowing here rather than inside each handler means
            // a route added later is refused by default rather than by remembering to.
            if (caller is null && (build is null || !IsPrebuiltRoute(http.Request.Path)))
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

            if (caller is not null)
                http.Items[CallerItemKey] = caller;
            if (build is not null)
                http.Items[BuildCallerItemKey] = build;
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
                    http, RootHub(http), plugin, version,
                    Requested(http, "identity", FrameworkMvid),
                    Requested(http, "arch", ReleaseArchitecture.Live),
                    Caller(http),
                    ct));

        MapPrebuilt(group);
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
    // ─────────────────── prebuilt: the registry SERVES sealed publications ───────────────────
    //
    // Step 2 of the plugin build contract (Doc/Architecture/PluginBuildContract): a downstream
    // repo INSTALLS its upstream from the upstream's sealed publication. Until this route existed
    // the only reader of `prebuilt-bundles/<identity>/<source>/` was an Azure OIDC identity whose
    // federated credentials live in the Entra tenant — none of them for `pull_request`, so a gate
    // could not fetch on the one event it exists for (AADSTS700213, measured 2026-08-27). The
    // portal already mounts the share (PreWarm:PrebuiltBundleRoot); serving it here puts the read
    // behind the SAME authenticator and grant model as every other bundle route, so who may fetch
    // which source is a `PluginGrantEntry` on a mesh node — auditable, listable, revocable — and
    // never a rule in a cloud tenant nobody can query.
    //
    // Authority: a whole-source grant (`<source>/*`) on the caller. The identity that PUBLISHES a
    // source is the one that may fetch what it depends on, scoped per source — a satellite holds
    // `plugins/*` to fetch, never a grant to publish AS plugins.
    //
    // 🚨 Serves ONLY what the `_complete` seal lists (PublishedBundleCatalogue.SealedBundlesOf):
    // a torn publication — no sentinel, or a listed bundle absent — is refused wholesale, exactly
    // as the boot seeder refuses it. A consumer must never be handed a set the portal itself
    // would decline.
    private const string PrebuiltSegment = "prebuilt";

    private static void MapPrebuilt(RouteGroupBuilder group)
    {
        group.MapGet($"/{PrebuiltSegment}/{{identity}}/{{source}}",
            (HttpContext http, string identity, string source) =>
                PrebuiltIndex(http, identity, source, Caller(http), BuildCaller(http)));
        group.MapGet($"/{PrebuiltSegment}/{{identity}}/{{source}}/{{bundle}}",
            (HttpContext http, string identity, string source, string bundle) =>
                PrebuiltBundle(http, identity, source, bundle, Caller(http), BuildCaller(http)));
        // The module set a publication was sealed against (MeshWeaver#2698) — a consumer pinned to
        // this identity composes THESE bytes, never the package endpoint's. The literal segment wins
        // over {bundle} above, so a NodeType bundle named "modules" is not served here.
        group.MapGet($"/{PrebuiltSegment}/{{identity}}/{{source}}/{PublishedBundleCatalogue.ModulesDirectoryName}",
            (HttpContext http, string identity, string source) =>
                PrebuiltModuleSet(http, identity, source, Caller(http), BuildCaller(http)));
        group.MapGet($"/{PrebuiltSegment}/{{identity}}/{{source}}/{PublishedBundleCatalogue.ModulesDirectoryName}/{{bundle}}",
            (HttpContext http, string identity, string source, string bundle) =>
                PrebuiltModule(http, identity, source, bundle, Caller(http), BuildCaller(http)));
    }

    /// <summary>The sealed publication's bundle names, or 404 when none is sealed for that
    /// identity/source, or 403 when the caller holds no whole-source grant on it.</summary>
    private static IResult PrebuiltIndex(
        HttpContext http, string identity, string source,
        AuthenticatedInstance? caller, AuthenticatedBuild? build)
    {
        if (PrebuiltDecision(http, identity, source, caller, build) is { } refused)
            return refused;
        var directory = PrebuiltDirectory(http, identity, source);
        var sealed_ = directory is null ? null : PublishedBundleCatalogue.SealedBundlesOf(directory, Log(http));
        if (sealed_ is null)
            return Results.Json(
                new { error = $"no sealed publication for source '{source}' under framework identity '{identity}'" },
                statusCode: StatusCodes.Status404NotFound);
        return Results.Json(new { identity, source, bundles = sealed_ });
    }

    /// <summary>One sealed bundle's bytes. A name the seal does not list is 404 even if the file
    /// exists — an unsealed file is not part of the publication.</summary>
    private static IResult PrebuiltBundle(
        HttpContext http, string identity, string source, string bundle,
        AuthenticatedInstance? caller, AuthenticatedBuild? build)
    {
        if (PrebuiltDecision(http, identity, source, caller, build) is { } refused)
            return refused;
        var directory = PrebuiltDirectory(http, identity, source);
        var sealed_ = directory is null ? null : PublishedBundleCatalogue.SealedBundlesOf(directory, Log(http));
        if (sealed_ is null || !sealed_.Contains(bundle, StringComparer.OrdinalIgnoreCase))
            return NoSuchBundle();
        var path = Path.Combine(directory!, bundle);
        return Results.File(path, "application/zip", fileDownloadName: bundle);
    }

    /// <summary>The module bundles the sealed publication composed — its NodeType assemblies'
    /// dependency records point at exactly these bytes. 404 with the reason when the publication
    /// is torn, or predates module sealing (republish it); an EMPTY list when the bake composed
    /// nothing. Same grant as the bundle index: a whole-source grant on the source.</summary>
    private static IResult PrebuiltModuleSet(
        HttpContext http, string identity, string source,
        AuthenticatedInstance? caller, AuthenticatedBuild? build)
    {
        if (PrebuiltDecision(http, identity, source, caller, build) is { } refused)
            return refused;
        var directory = PrebuiltDirectory(http, identity, source);
        var reading = directory is null
            ? new ModuleSetReading(null, "no sealed publication")
            : PublishedBundleCatalogue.SealedModulesOf(directory, Log(http));
        if (reading.Modules is null)
            return Results.Json(
                new { error = $"{reading.Refusal} — source '{source}', framework identity '{identity}'" },
                statusCode: StatusCodes.Status404NotFound);
        return Results.Json(new { identity, source, modules = reading.Modules });
    }

    /// <summary>One sealed module bundle's bytes. A name the module index does not list is 404
    /// even if the file exists — an unlisted file is not part of the sealed set.</summary>
    private static IResult PrebuiltModule(
        HttpContext http, string identity, string source, string bundle,
        AuthenticatedInstance? caller, AuthenticatedBuild? build)
    {
        if (PrebuiltDecision(http, identity, source, caller, build) is { } refused)
            return refused;
        if (!IsBareName(bundle))
            return Results.Json(new { error = "bundle must be a bare name" },
                statusCode: StatusCodes.Status400BadRequest);
        var directory = PrebuiltDirectory(http, identity, source);
        var reading = directory is null
            ? new ModuleSetReading(null, "no sealed publication")
            : PublishedBundleCatalogue.SealedModulesOf(directory, Log(http));
        if (reading.Modules is null || !reading.Modules.Contains(bundle, StringComparer.OrdinalIgnoreCase))
            return NoSuchBundle();
        var path = Path.Combine(directory!, PublishedBundleCatalogue.ModulesDirectoryName, bundle);
        return Results.File(path, "application/zip", fileDownloadName: bundle);
    }

    /// <summary>
    /// The three refusals, in the order that leaks least: no caller (401, the group filter's
    /// message), a path segment that is not a bare name (400 — `..` or a separator would walk
    /// the share), a caller without a whole-source grant (403). Records the decision on the
    /// ledger like every other bundle route, so a refused fetch is as visible as a refused serve.
    /// </summary>
    private static IResult? PrebuiltDecision(
        HttpContext http, string identity, string source,
        AuthenticatedInstance? caller, AuthenticatedBuild? build)
    {
        if (caller is null && build is null)
            return Results.Json(new { error = "a registered instance key is required" },
                statusCode: StatusCodes.Status401Unauthorized);
        if (!IsBareName(identity) || !IsBareName(source))
            return Results.Json(new { error = "identity and source must be bare names" },
                statusCode: StatusCodes.Status400BadRequest);

        // 🚨 The BUILD leg (#2483). A verified GitHub Actions token establishes WHICH repository
        // asked; the admin-owned BuildPrincipal node decides whether it may fetch THIS source, on
        // THIS event. It is the same shape as the instance leg one line below — credential resolves
        // to identity, admin-owned node decides — so there is one thing to keep honest, not two.
        if (caller is null)
            return BuildDecision(http, source, build!);

        // A PLAN-LESS whole-source entry, specifically: a sealed publication carries every plan's
        // bundles, so a plan-scoped `<source>/*@pro` — which licenses that source's packages one by
        // one, by tier — must never fetch the publication whole.
        var allowed = caller.Grant.AllowsWholeSource(source);
        Ledger(http)?.Record(new EntitlementDecision(
            $"{PrebuiltSegment}/{source}",
            allowed ? EntitlementOutcome.Granted : EntitlementOutcome.Denied,
            EntitlementAnchorKind.Registry,
            source,
            true,
            allowed
                ? $"whole-source grant '{source}/*' held by {caller.Instance.InstanceId}"
                : $"{caller.Instance.InstanceId} holds no '{source}/*' grant — a fetch needs the whole source"));
        return allowed
            ? null
            : Results.Json(
                new { error = $"instance '{caller.Instance.InstanceId}' is not granted source '{source}'" },
                statusCode: StatusCodes.Status403Forbidden);
    }

    /// <summary>
    /// The build principal's half of <see cref="PrebuiltDecision"/>: <c>fetch:&lt;source&gt;</c> on
    /// the repository's own <see cref="BuildPrincipal"/>, decided against the run's <c>event_name</c>
    /// and <c>ref</c> (#2483).
    ///
    /// <para>🚨 The refusal BODY names the repository and the source and nothing else. The reason —
    /// which check failed — goes to the log and the entitlement ledger, because the URL space here
    /// is fully predictable and a reason that distinguishes "wrong event" from "no scope" from "no
    /// such publication" is an inventory oracle over every source this registry holds.</para>
    /// </summary>
    private static IResult? BuildDecision(HttpContext http, string source, AuthenticatedBuild build)
    {
        var refusal = build.Refuse(BuildVerbs.Fetch, source, DateTimeOffset.UtcNow);
        var allowed = refusal is null;
        Ledger(http)?.Record(new EntitlementDecision(
            $"{PrebuiltSegment}/{source}",
            allowed ? EntitlementOutcome.Granted : EntitlementOutcome.Denied,
            EntitlementAnchorKind.Registry,
            source,
            true,
            allowed
                ? $"build principal {build.PrincipalPath} holds '{BuildPrincipal.Scope(BuildVerbs.Fetch, source)}' "
                  + $"for {build.Repository} on event '{build.Claims.EventName}'"
                : $"build principal {build.PrincipalPath} ({build.Repository}, event "
                  + $"'{build.Claims.EventName}'): {refusal}"));
        if (allowed)
            return null;

        Log(http)?.LogWarning(
            "Plugin bundles: build principal {Path} refused for source {Source} — {Reason}",
            build.PrincipalPath, source, refusal);
        return Results.Json(
            new { error = $"repository '{build.Repository}' may not fetch source '{source}'" },
            statusCode: StatusCodes.Status403Forbidden);
    }

    private static string? PrebuiltDirectory(HttpContext http, string identity, string source)
    {
        var root = http.RequestServices.GetService<IConfiguration>()?[PublishedBundleCatalogue.PublishedRootConfigKey];
        if (string.IsNullOrWhiteSpace(root))
            return null;
        var directory = Path.Combine(root, identity, source);
        return Directory.Exists(directory) ? directory : null;
    }

    private static bool IsBareName(string segment) =>
        segment.Length > 0
        && segment.IndexOfAny(['/', '\\', ':']) < 0
        && segment != "." && segment != "..";

    private static ILogger? Log(HttpContext http) =>
        http.RequestServices.GetService<ILoggerFactory>()?.CreateLogger(typeof(PluginBundleEndpoints));

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
                IReadOnlyList<BundleReader.ModuleAsset> assets;
                try
                {
                    (manifest, files) = BundleReader.ReadModule(bytes);
                    // Inside the SAME guard: a bundle whose assemblies read cleanly can still carry
                    // a corrupt asset entry, and reading that outside here would throw out of the
                    // handler as a 500 — telling the publisher "the server broke" for what is
                    // simply an unreadable upload, the case this catch already classifies.
                    assets = BundleReader.ReadModuleAssets(bytes);
                }
                catch (Exception exception)
                {
                    logger?.LogWarning(exception, "Module publish for {Plugin}: unreadable bundle", plugin);
                    return Results.Json(new { error = "the upload is not a readable module bundle" },
                        statusCode: StatusCodes.Status400BadRequest);
                }

                var (accepted, decline) = ModulePublish.Validate(
                    plugin, manifest, files, http.Request.Query["version"],
                    http.Request.Query["packagePath"],
                    // 🚨 THE SHELF MUST CARRY THE ASSETS TOO. A consumer never reads this upload's
                    // archive — it fetches what the shelf holds — so an asset dropped at the
                    // warehouse door is unreachable for every instance downstream, however complete
                    // the packed bundle was. Both halves of this were already built (Validate takes
                    // them, Accepted carries them, ShelveModule lands them); only these two
                    // arguments were missing, and that is why every landed view pack held
                    // assemblies and no wwwroot: DefaultViews' bundle declares 254 static assets
                    // and its landed generation had 11 files with no wwwroot, EntityViews' scoped
                    // CSS 404'd in production from #2188 on, and the Views/Graph image copies were
                    // the only reason a portal was styled at all (#2221). Same read the consumer's
                    // own landing does (PluginBundleClient.LandFromBundle), so shelf and adopt
                    // place byte-identical content.
                    assets);
                if (accepted is null)
                {
                    logger?.LogWarning("Module publish for {Plugin} REFUSED: {Reason}", plugin, decline);
                    return Results.Json(new { error = decline }, statusCode: StatusCodes.Status400BadRequest);
                }

                // 🚨 #3211 — the arming signal for the LAST refusal, named per publisher.
                // A bundle stating no framework identity shelves a null, the index advertises a
                // null, and ModuleUpdateDecision (#3154) then answers "already landed — the
                // identity could not be checked" for this module on every reconcile of every
                // installation, forever: an unknown on the SERVED side is the one landing cannot
                // heal. The producer's own lane already refuses to pack or POST such a bundle, so
                // reaching here means a publisher on a pin older than #3211. This line is what says
                // WHICH — the measurement the registry-side 400 waits on, rather than a refusal
                // armed on faith that would take the fleet's publishes down with it.
                if (string.IsNullOrWhiteSpace(accepted.FrameworkMvid))
                    logger?.LogWarning(
                        "Module publish for {Plugin}: '{Module}' version {Version} states NO "
                        + "framework identity, so it shelves a null and every consumer of it will "
                        + "answer 'up to date — identity could not be checked' on every reconcile "
                        + "(#3154). Its producer packs on a lane older than MeshWeaver#3211; bump "
                        + "that repo's node-repo-module-pack.yml pin. This becomes a 400.",
                        plugin, accepted.Module, accepted.Version ?? "(unversioned)");

                ModuleLandingOutcome outcome;
                try
                {
                    // 🚨 The SHELF landing, not the adopt one (2026-08-22): publishing stocks the
                    // registry's warehouse, and a warehouse may carry modules for platforms NEWER
                    // than itself. An above-floor upload therefore lands as HELD — bytes on the
                    // shelf, served to consumers (whose own install path applies the floor
                    // against THEIR platform), excluded from this instance's boot until a
                    // platform update satisfies the floor — instead of the 409 that deadlocked
                    // extracted modules against the very platform update that needed them
                    // (rc6→rc7, 2026-08-22). Real refusals (the app-closure same-identity
                    // trap-door, malformed names) still surface as the observable's error.
                    outcome = (await landing.ShelveModule(
                        accepted.Module, accepted.Files,
                        frameworkMvid: accepted.FrameworkMvid,
                        packagePath: accepted.PackagePath,
                        version: accepted.Version,
                        minMeshVersion: accepted.MinMeshVersion,
                        staticAssets: accepted.StaticAssets)
                        .FirstAsync()
                        .ObserveCompletion(
                            ex => logger?.LogWarning(ex,
                                "Module publish for {Plugin}: landing '{Module}' faulted after the "
                                + "publish had already been answered", plugin, accepted.Module),
                            ct))!;
                }
                catch (Exception exception)
                {
                    logger?.LogWarning(exception,
                        "Module publish for {Plugin}: landing refused '{Module}'", plugin, accepted.Module);
                    return Results.Json(new { error = exception.Message },
                        statusCode: StatusCodes.Status409Conflict);
                }

                if (outcome.Held)
                    logger?.LogInformation(
                        "Module publish: SHELVED '{Module}' for {Plugin} ({Files} file(s), version "
                        + "{Version}) — HELD from local activation ({Reason}); it serves from this "
                        + "registry, and this instance loads it once its platform satisfies the floor",
                        accepted.Module, plugin, accepted.Files.Count,
                        accepted.Version ?? "(unversioned)", outcome.HoldReason);
                else
                    logger?.LogInformation(
                        "Module publish: landed '{Module}' for {Plugin} ({Files} file(s), version {Version}, "
                        + "floor {Floor}) — it serves from this registry and loads here on the next restart",
                        accepted.Module, plugin, accepted.Files.Count,
                        accepted.Version ?? "(unversioned)", accepted.MinMeshVersion ?? "(none)");

                // held/holdReason let the publisher tell "shelved, will serve" apart from
                // "activated here"; pendingRestart is honest for the held case — a restart of
                // THIS instance would not load a held module, so nothing is pending on one.
                return Results.Json(new
                {
                    plugin,
                    module = accepted.Module,
                    version = accepted.Version,
                    files = accepted.Files.Count,
                    held = outcome.Held,
                    holdReason = outcome.HoldReason,
                    pendingRestart = !outcome.Held,
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

    /// <summary>The authenticated BUILD principal the filter stamped, or <c>null</c> (#2483). Same
    /// contract as <see cref="Caller"/>: null is "granted nothing", never "unscoped".</summary>
    private static AuthenticatedBuild? BuildCaller(HttpContext http) =>
        http.Items.TryGetValue(BuildCallerItemKey, out var value) ? value as AuthenticatedBuild : null;

    /// <summary>Whether <paramref name="path"/> is one of the prebuilt-publication routes — the only
    /// ones a build principal may reach.</summary>
    private static bool IsPrebuiltRoute(PathString path) =>
        path.StartsWithSegments($"{RoutePrefix}/{PrebuiltSegment}");

    /// <summary>
    /// 🚨 <b>THE PER-PACKAGE AUTHORIZATION</b> (#1772), now anchored on the REGISTRY (#1782 gap 2) —
    /// whether <paramref name="caller"/> may pull <paramref name="package"/>, decided by the
    /// admin-owned <see cref="PluginGrant"/> its key resolved to.
    ///
    /// <para>The grant is a set of <c>(source, package)</c> pairs, and the whole question is where
    /// the <c>source</c> half comes from. It used to come from the install record's
    /// <see cref="PackageManifest.Source"/> and nowhere else — so a package this instance had not
    /// itself installed had NO binding, and "I cannot tell which source this is from" came out as
    /// "you are not entitled to it". <see cref="PackageEntitlementAnchor"/> inverts that: the
    /// registry's own catalog is the authority, the install record is a cache of it, and a cache
    /// miss asks upstream. The GRANT match is untouched — the same
    /// <see cref="AuthenticatedInstance.Allows(string,string)"/> <c>/api/plugins</c> and <c>InstallByDefault</c>
    /// make.</para>
    ///
    /// <para>🚨 <b>An unstamped record no longer fails dark.</b> A record whose <c>Source</c> is null
    /// used to match no grant entry at all and was servable to nobody, silently. Now it simply
    /// carries no CACHED binding, so the anchor answers for it — from the registry's catalog, never
    /// from an invented source. If the anchor cannot be consulted either, the outcome is
    /// <see cref="EntitlementOutcome.Indeterminate"/>: the bytes are still withheld (the consumer's
    /// <c>PluginBundleClient</c> reads that 404 as "no prebuilt bundle — will compile", so the cost
    /// is a compile), but it is recorded as UNKNOWN rather than asserted as a denial.</para>
    ///
    /// <para>🚨 <b>A caller the filter did not stamp is granted NOTHING</b>, and that is a real
    /// negative rather than an unanswerable one: there is no anonymous branch on these routes, so a
    /// null caller can only mean a route mapped outside the group (#1772).</para>
    /// </summary>
    private static EntitlementDecision Decide(
        AuthenticatedInstance? caller, BundleEntry package, PackageOriginSnapshot anchor) =>
        caller is null
            ? new EntitlementDecision(
                package.PluginId, EntitlementOutcome.Denied, EntitlementAnchorKind.None, null, true,
                "no authenticated caller was stamped — these routes have no anonymous branch")
            : PackageEntitlementAnchor.Resolve(
                package.PluginId,
                anchor.SourceOf(package.PluginId),
                package.Source,
                anchor.IsComplete,
                // The package's PLAN, registry-first like its source: the anchor's catalog says
                // which tier a package declares today, the install record's stamp is the cached
                // observation for a package the anchor does not carry.
                source => caller.Allows(
                    source, package.PluginId, anchor.TierOf(package.PluginId) ?? package.Tier));

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
    /// 🚨 The ENTITLEMENT ANCHOR (#1782 gap 2) — the registry's own catalog, read as the authority
    /// on which source carries which package.
    ///
    /// <para>Resolved from the REQUEST's services first, then the mesh's, exactly like
    /// <c>ModuleLandingService</c>: it lets a host wire a different anchor (a test, an instance
    /// serving a curated subset) without a second resolution rule. An instance with no anchor
    /// registered at all is an authority on nothing — <see cref="AnchorState.Unconfigured"/>, which
    /// falls back to the local cache and never to a denial.</para>
    /// </summary>
    private static PackageOriginAnchor? Anchor(HttpContext http) =>
        http.RequestServices.GetService<PackageOriginAnchor>()
        ?? RootHub(http).ServiceProvider.GetService<PackageOriginAnchor>();

    /// <summary>The record that makes a degraded entitlement answer legible (#1782 gap 2). Same
    /// two-step resolution as <see cref="Anchor"/>.</summary>
    private static PackageEntitlementLedger? Ledger(HttpContext http) =>
        http.RequestServices.GetService<PackageEntitlementLedger>()
        ?? RootHub(http).ServiceProvider.GetService<PackageEntitlementLedger>();

    /// <summary>Reads the anchor, or reports it unconfigured when this instance registers none.</summary>
    private static IObservable<PackageOriginSnapshot> ReadAnchor(PackageOriginAnchor? anchor) =>
        anchor?.Read()
        ?? Observable.Return(
            PackageOriginSnapshot.Empty(AnchorState.Unconfigured, DateTimeOffset.UtcNow));

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
    /// <para>🚨 <b>Scoped to what the caller was GRANTED</b> (#1772, <see cref="Decide"/>) — an
    /// ungranted package is simply not listed, so a caller cannot even learn it is installed here.
    /// That is what makes the download route's refusal non-informative: with the index filtered, "not
    /// in your index" and "404 on fetch" agree, and neither confirms existence. A caller granted
    /// nothing gets an empty <c>bundles</c> array, indistinguishable from a registry with nothing
    /// installed.</para>
    ///
    /// <para>🚨 <b>Only the DEGRADED decisions are recorded here</b> (#1782 gap 2). An index request
    /// resolves every servable package at once and is polled by every consumer, so recording all of
    /// them would fill a bounded ledger with routine grants and evict the answers worth keeping. The
    /// download route records every decision it makes — one per actual fetch.</para>
    /// </summary>
    private static Task<IResult> Index(
        HttpContext http, IMessageHub rootHub, AuthenticatedInstance? caller, CancellationToken ct)
    {
        var baseUrl = $"{http.Request.Scheme}://{http.Request.Host}{RoutePrefix}";
        var ledger = Ledger(http);
        // Resolved NOW, while the request scope is alive: a late fault arrives after
        // HttpContext.RequestServices has been disposed, so resolving inside the lambda would throw
        // exactly when the report is needed.
        var lateFaultLogger = Log(http);

        return Servable(rootHub, Anchor(http), ct)
            .Select(state =>
            {
                var decisions = state.Entries
                    .Select(entry => (Entry: entry, Decision: Decide(caller, entry, state.Anchor)))
                    .ToArray();
                foreach (var (_, decision) in decisions.Where(d => d.Decision.IsDegraded))
                    ledger?.Record(decision);
                WarnAboutDegradedResolution(rootHub, caller, state.Anchor,
                    decisions.Select(d => d.Decision).ToArray());
                var granted = (IReadOnlyList<BundleEntry>)decisions
                    .Where(d => d.Decision.Serves).Select(d => d.Entry).ToArray();
                WarnAboutAnEmptyIndex(rootHub, caller, state.Entries, granted);
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
                        module = modules.TryGetValue(p.PluginId, out var servable)
                            ? servable.Name : null,
                        // The module's declared platform FLOOR — the consumer's gate (a semver
                        // floor, never MVID equality; the index-level frameworkMvid above stays
                        // the NodeType lane's strict gate).
                        minMeshVersion = servable is null ? null : p.MinMeshVersion,
                        // 🚨 The framework identity of THIS bundle's MODULE bytes, as their
                        // producer recorded it at publish (Plugins#931). Not the index-level
                        // identity above: that is this portal's own bake, and a module is not
                        // baked here. A consumer compares it against what it has landed, so a
                        // rebuild of unchanged source against a new platform — which republishes
                        // under the SAME version — is visible without downloading a byte
                        // (Plugins#723: without it the updater goes quiet and the fleet cannot
                        // converge). Additive: a pre-#931 client ignores it, and a pre-#931
                        // registry simply omits it, which reads as unknown rather than as a match.
                        frameworkMvid = servable?.FrameworkMvid,
                    }).ToArray(),
                })))
            .FirstAsync()
            .ObserveCompletion(
                ex => lateFaultLogger?.LogWarning(ex,
                    "Plugin bundles: the index faulted after the response had already been sent"),
                ct)!;
    }

    /// <summary>
    /// 🚨 Names, on the index request, every install record with NO <see cref="BundleEntry.Source"/>
    /// — the records <see cref="Decide"/> can never match, and which are therefore servable to
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
    /// Everything this registry can serve, and the state of the anchor the entitlement decision
    /// will be made against.
    ///
    /// <para>THREE contributors, in precedence order:</para>
    /// <list type="number">
    ///   <item>its own INSTALL RECORDS (the catalog lane) — a deliberate install is the most
    ///     intentional statement, so it wins a <c>PluginId</c> collision;</item>
    ///   <item>the modules PUBLISHED onto it (the activation sidecar). The union with (1) is what
    ///     makes a GitSync-native registry serve at all: memex-cloud provisions its packages as
    ///     Spaces and never runs the catalog install, so it has NO install records — with a
    ///     record-only index its bundle feed was permanently empty and every consumer read
    ///     SkipNoBundle (found live, 2026-08-20, the first real remote consumer);</item>
    ///   <item>🚨 the packages the REGISTRY ANCHOR advertises whose content this instance actually
    ///     holds (#1782 gap 2). This is the half that could not exist before the anchor: a package
    ///     provisioned as a Space, with compiled NodeTypes and no install record and no published
    ///     module, had nothing to bind a grant to and was therefore unreachable through the index
    ///     however plainly it was installed.</item>
    /// </list>
    ///
    /// <para>🚨 The sidecar's <c>PackagePath</c> source in (2) is now read as a CACHED claim rather
    /// than as authority — <see cref="Decide"/> lets the anchor override it whenever the registry
    /// carries the package. It is the publisher's own assertion about which source it belongs to,
    /// and believing an uploader's assertion outright is exactly the invented anchor #1782 warned
    /// would weaken #1777.</para>
    /// </summary>
    private static IObservable<(IReadOnlyList<BundleEntry> Entries, PackageOriginSnapshot Anchor)> Servable(
        IMessageHub rootHub, PackageOriginAnchor? anchor, CancellationToken ct) =>
        InstalledPackages(rootHub, ct)
            .Do(packages => WarnAboutUnstampedRecords(rootHub, packages))
            .SelectMany(records => WithPublishedModules(rootHub, records))
            .SelectMany(local => ReadAnchor(anchor)
                .SelectMany(snapshot => WithAnchoredPackages(rootHub, local, snapshot)
                    .Select(entries => (Entries: entries, Anchor: snapshot))));

    /// <summary>Contributor (2): the modules published onto this instance that no install record
    /// already covers.</summary>
    private static IObservable<IReadOnlyList<BundleEntry>> WithPublishedModules(
        IMessageHub rootHub, IReadOnlyList<BundleEntry> records)
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
    }

    /// <summary>
    /// Contributor (3): the packages the anchor advertises that nothing local already covers — the
    /// gap-2 half.
    ///
    /// <para>🚨 <b>Gated on this instance actually HOLDING the package's partition</b>, because the
    /// bundle is assembled from the NodeType assemblies under it. Advertising a package whose
    /// content is not here would hand every consumer an empty archive to download and then count as
    /// a miss — an index full of bundles that cannot carry anything is a worse answer than a
    /// filtered one.</para>
    ///
    /// <para>🚨 …and that gate FAILS OPEN. If the partition list comes back empty — the query could
    /// not answer, or this mesh does not register partitions the way it is read here — the
    /// candidates are kept rather than dropped. A gate whose inability to answer removes entries is
    /// the very shape this change exists to remove; it is an optimization, and an optimization must
    /// never be the thing that denies.</para>
    /// </summary>
    private static IObservable<IReadOnlyList<BundleEntry>> WithAnchoredPackages(
        IMessageHub rootHub, IReadOnlyList<BundleEntry> local, PackageOriginSnapshot anchor)
    {
        var covered = local.Select(e => e.PluginId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var candidates = anchor.Origins.Values
            .Where(origin => !covered.Contains(origin.PackageId)
                && !string.IsNullOrWhiteSpace(origin.ReleasedVersion))
            .ToArray();
        if (candidates.Length == 0)
            return Observable.Return(local);

        return HeldPartitions(rootHub).Select(held =>
        {
            var servable = held.Count == 0
                ? candidates
                : candidates.Where(origin => held.Contains(origin.Partition)).ToArray();
            return (IReadOnlyList<BundleEntry>)local
                .Concat(servable.Select(origin => new BundleEntry(
                    PackagingManifest.IdPrefix + origin.PackageId,
                    origin.ReleasedVersion!,
                    origin.PackageId,
                    Module: origin.Module,
                    MinMeshVersion: origin.MinMeshVersion,
                    // No CACHED binding: this entry exists only because the anchor advertises it,
                    // and Decide reads that binding straight off the snapshot.
                    Source: null,
                    Tier: origin.Tier)))
                .ToArray();
        });
    }

    /// <summary>
    /// The partitions this mesh holds, from the <c>Admin/Partition</c> registry every Space writes.
    /// An empty set means "could not answer" as much as "none", and both callers treat it that way
    /// (see <see cref="WithAnchoredPackages"/>) — never as evidence of absence.
    /// </summary>
    private static IObservable<IReadOnlySet<string>> HeldPartitions(IMessageHub rootHub) =>
        rootHub.ServiceProvider.GetRequiredService<IMeshService>()
            .Query<MeshNode>(MeshQueryRequest.FromQuery(
                $"namespace:{PartitionNodeType.Namespace} nodeType:{PartitionNodeType.NodeType}"))
            .Where(c => c.ChangeType == QueryChangeType.Initial)
            .Take(1)
            .Select(c => (IReadOnlySet<string>)c.Items
                .Select(node => node.Id)
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .ToHashSet(StringComparer.OrdinalIgnoreCase))
            .Catch((Exception _) =>
                Observable.Return((IReadOnlySet<string>)new HashSet<string>(StringComparer.OrdinalIgnoreCase)));

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

    /// <summary>
    /// 🚨 Says so when entitlement was resolved WITHOUT the anchor (#1782 gap 2) — the state that
    /// otherwise looks exactly like a normal day from both sides.
    ///
    /// <para>A registry that cannot list its sources still answers every request: granted packages
    /// keep flowing from the cached bindings, and the packages nothing ever observed simply do not
    /// appear. Nothing is red, nothing 500s, and the one difference — that some answers were
    /// guesses the system declined to make — is invisible unless it is said out loud. The two
    /// degradations get different lines because they need different responses: an anchor that
    /// cannot be read at all is an operational failure to go and fix, while an UNKNOWN package is
    /// the consumer-visible consequence of it.</para>
    /// </summary>
    private static void WarnAboutDegradedResolution(
        IMessageHub rootHub, AuthenticatedInstance? caller, PackageOriginSnapshot anchor,
        IReadOnlyList<EntitlementDecision> decisions)
    {
        if (anchor.IsComplete)
            return;

        var logger = rootHub.ServiceProvider.GetService<ILoggerFactory>()
            ?.CreateLogger(typeof(PluginBundleEndpoints));
        if (logger is null)
            return;

        var unknown = decisions
            .Where(d => d.Outcome == EntitlementOutcome.Indeterminate).ToArray();
        logger.LogWarning(
            "Plugin bundles: entitlement for {Instance} was resolved WITHOUT a complete registry "
            + "answer — {Anchor}. {Cached} package(s) were answered from a previously observed "
            + "binding (served as before, deliberately: an unreachable registry is not evidence of "
            + "a missing entitlement) and {Unknown} could not be answered at all — those are "
            + "UNKNOWN, not denials: {Names}",
            caller?.Instance.InstanceId ?? "an unauthenticated caller",
            anchor.Describe(),
            decisions.Count(d => d.Anchor == EntitlementAnchorKind.Cache),
            unknown.Length,
            unknown.Length == 0
                ? "(none)"
                : string.Join(", ", unknown.Select(d => d.PackageId).Take(MissesReported)));
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
                "Plugin bundles: {Count} install record(s) carry no registry source, so they have "
                + "no CACHED binding and their entitlement can only be answered by the registry "
                + "anchor: {Packages}. Before #1782 gap 2 that made them servable to NO instance, "
                + "silently; they are now resolved upstream, and become UNKNOWN only when the "
                + "anchor is unreachable too. Re-install them through the registry to stamp the "
                + "source (#1772) and the answer stops depending on the registry being up.",
                unstamped.Length,
                string.Join(", ", unstamped.Select(p => p.PluginId).Take(MissesReported)));
    }

    /// <summary>
    /// Which of the installed packages' declared modules this instance can serve right now:
    /// plugin id → module assembly name, for exactly the entries whose bytes exist under
    /// <c>modules/&lt;name&gt;/</c> and were not uninstalled (<see cref="ModuleBundleSource"/>).
    /// A HELD landing — floor above THIS instance's platform, the registry-shelf state (2026-08-22) —
    /// is listed too, deliberately: the index surfaces its <c>minMeshVersion</c> and each
    /// consumer's own gate decides loadability THERE, before a byte travels. One
    /// activation-sidecar read for the whole index.
    ///
    /// <para>🚨 Each entry also carries the framework identity the SHELF recorded for those module
    /// bytes (Plugins#931). It is the producer's value, written when the owning repo's CI published
    /// them (<c>ModulePublish.Accepted.FrameworkMvid</c> → <c>ShelveModule</c>) — never this
    /// portal's own bake identity, which the index states separately and which says nothing about a
    /// module the registry did not build. Without it a consumer cannot tell a REBUILD of the same
    /// source against a new platform from a no-op, because such a rebuild republishes under the
    /// same version; that is the whole defect. Null when the producer recorded none, or for a
    /// module that rides the image and has no sidecar entry — read downstream as "unknown", never
    /// as "matches", and never inferred from anything.</para>
    /// </summary>
    private static IObservable<IReadOnlyDictionary<string, ServableModule>> ServableModules(
        IMessageHub rootHub, IReadOnlyList<BundleEntry> packages)
    {
        var landing = rootHub.ServiceProvider.GetService<ModuleLandingService>();
        var declaring = packages.Where(p => !string.IsNullOrWhiteSpace(p.Module)).ToArray();
        if (landing is null || declaring.Length == 0)
            return Observable.Return<IReadOnlyDictionary<string, ServableModule>>(
                new Dictionary<string, ServableModule>());

        return landing.GetActivation().Take(1)
            .Select(activation => (IReadOnlyDictionary<string, ServableModule>)declaring
                .Where(p => ModuleBundleSource.Collect(
                        landing.BaseDirectory, p.Module!, activation)
                    .DeclineReason is null)
                .ToDictionary(
                    p => p.PluginId,
                    p => new ServableModule(
                        p.Module!,
                        activation.Entries
                            .FirstOrDefault(e => string.Equals(
                                e.Name, p.Module, StringComparison.OrdinalIgnoreCase))
                            ?.FrameworkMvid),
                    StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>One servable module on the index: its assembly name and the framework identity the
    /// shelf recorded for its bytes (null = the producer stated none).</summary>
    private sealed record ServableModule(string Name, string? FrameworkMvid);

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
        HttpContext http, IMessageHub rootHub, string plugin, string version, string identity,
        string architecture, AuthenticatedInstance? caller, CancellationToken ct)
    {
        var anchorService = Anchor(http);
        var ledger = Ledger(http);
        // Resolved NOW — see Index: the request scope is gone by the time a late fault lands.
        var lateFaultLogger = Log(http);
        // The published bundle root this registry mounts (#3244) — read here, in the request scope,
        // for the same reason the logger is. Null on a deployment that consumes no CI bakes, which
        // leaves the module section exactly as it was before this existed.
        var publishedRoot =
            http.RequestServices.GetService<IConfiguration>()
                ?[PublishedBundleCatalogue.PublishedRootConfigKey];
        return Servable(rootHub, anchorService, ct)
            .SelectMany(state =>
            {
                // Named apart from the query-string reader Requested(HttpContext, …) above — one
                // shadowing the other reads as a call to the wrong thing.
                bool IsAsked(BundleEntry p) =>
                    string.Equals(p.PluginId, plugin, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(p.Version, version, StringComparison.OrdinalIgnoreCase);

                var asked = state.Entries.FirstOrDefault(IsAsked);

                // 🚨 The entitlement question is answered even when this instance holds nothing that
                // matches, and it is answered the SAME way — because "I hold no bytes for it" and
                // "you may not have it" are different facts and the second must never be inferred
                // from the first. A package with no local entry has no cached binding, so the
                // decision falls to the anchor: granted, denied, or UNKNOWN.
                var decision = Decide(
                    caller,
                    asked ?? new BundleEntry(plugin, version, plugin),
                    state.Anchor);
                // Every download decision is recorded — one per actual fetch, which is the rate a
                // bounded diagnostic can carry and the granularity an operator asks about.
                ledger?.Record(decision);

                if (asked is not null && decision.Serves)
                    return Assemble(rootHub, asked, identity, architecture, publishedRoot);

                // The refusal is uniform on the wire; the LOG is where it is diagnosable, naming
                // which instance asked, what the entitlement answer actually was, and whether this
                // instance holds anything to serve. 🚨 The two halves are reported separately: an
                // Indeterminate outcome is NOT a refusal of entitlement, and reporting it as one is
                // how an unreachable registry would come to read as an unpaid customer.
                rootHub.ServiceProvider.GetService<ILoggerFactory>()
                    ?.CreateLogger(typeof(PluginBundleEndpoints))
                    .LogWarning(
                        "Plugin bundles: {Plugin}@{Version} not served to instance {Instance} — "
                        + "entitlement: {Decision}; bytes: {Bytes}. Anchor: {Anchor}",
                        plugin, version, caller?.Instance.InstanceId ?? "(none)",
                        decision.Describe(),
                        asked is null
                            ? "this instance holds no servable entry at that id and version"
                            : "held",
                        state.Anchor.Describe());
                return Observable.Return(NoSuchBundle());
            })
            .FirstAsync()
            .ObserveCompletion(
                ex => lateFaultLogger?.LogWarning(ex,
                    "Plugin bundles: {Plugin}@{Version} faulted after the response had already been sent",
                    plugin, version),
                ct)!;
    }

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
        IMessageHub rootHub, BundleEntry package, string identity, string architecture,
        string? publishedRoot)
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

                return assemblies.SelectMany(found =>
                {
                    // What the bytes THIS archive is about to carry were compiled against — the
                    // evidence that decides which build of the module goes with them (#3244).
                    // 🚨 The SAME filter BuildResult writes by: a type whose assembly could not be
                    // located contributes no bytes, so its record must not vote on which module
                    // rides beside bytes that are not there.
                    var recorded = ServedModuleBytes.RecordedFor(
                        found.Where(a => a.Path is not null).Select(a => a.Dependencies),
                        package.Module ?? "");
                    return ModuleFiles(rootHub, package, recorded, publishedRoot, identity)
                        .Select(module =>
                        {
                            IReadOnlyList<string> reported = module.Divergence is null
                                ? misses
                                : misses.Append(
                                        $"module '{package.Module}': {module.Divergence}")
                                    .ToArray();
                            return BuildResult(package, found, module, identity, architecture, reported);
                        });
                });
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
    /// refuses uninstalled landings; a HELD landing — floor above this instance's platform, the
    /// registry-shelf state, 2026-08-22 — is served, its floor riding the manifest for the CONSUMER's
    /// gate). Empty for a package that declares no module or whose bytes this instance cannot
    /// serve — the bundle then simply has no module section, which a consumer reads as "nothing
    /// to land".
    /// </summary>
    private static IObservable<ServedModule> ModuleFiles(
        IMessageHub rootHub, BundleEntry package, RecordedModuleId recorded,
        string? publishedRoot, string identity)
    {
        var landing = rootHub.ServiceProvider.GetService<ModuleLandingService>();
        if (landing is null || string.IsNullOrWhiteSpace(package.Module))
            return Observable.Return(
                new ServedModule([], [], "no module section", null));

        var logger = rootHub.ServiceProvider.GetService<ILoggerFactory>()
            ?.CreateLogger(typeof(PluginBundleEndpoints));

        return landing.GetActivation().Take(1)
            .Select(activation =>
            {
                var (files, assets, decline) = ModuleBundleSource.Collect(
                    landing.BaseDirectory, package.Module!, activation);
                if (decline is not null)
                    logger?.LogInformation(
                        "Plugin bundles: {Plugin} declares module '{Module}' but it is not served: {Reason}",
                        package.PluginId, package.Module, decline);

                // 🚨 ONE PRODUCER IN TIME (#3244). The shelf is content-versioned and identity-blind
                // — it serves whatever the module's own lane published last — while the assemblies
                // above are resolved for the CALLER's identity and record exactly which build of
                // this module they were compiled against. Hand over the bytes they record, which is
                // the shelf whenever the shelf IS that build and otherwise the module bundle the
                // publication sealed for this identity.
                var served = ServedModuleBytes.Resolve(
                    package.Module, package.PluginId, files, assets, recorded,
                    publishedRoot, identity, logger);
                if (served.Divergence is not null)
                    logger?.LogWarning(
                        "Plugin bundles: {Plugin} on framework {Identity} — {Divergence}",
                        package.PluginId, identity, served.Divergence);
                else if (files.Count > 0 && recorded.Mvid is not null)
                    logger?.LogInformation(
                        "Plugin bundles: {Plugin} serves module '{Module}' {Mvid} from {Provenance}",
                        package.PluginId, package.Module, recorded.Mvid, served.Provenance);
                return served;
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
        ServedModule module,
        string identity,
        string architecture,
        IReadOnlyList<string> misses)
    {
        var moduleFiles = module.Files;
        var moduleAssets = module.Assets;
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
                NuGetPackageWriter.ModuleEntryPathFor(local.FileName), local.Open));
        }

        // The module's STATIC WEB ASSETS ride under their own folder, keeping the relative path a
        // component's _content/<pack>/… URL asks for. Without this the serve path re-creates the
        // defect the publish path was just fixed for (#2221): the shelf would hold a complete pack
        // and hand consumers an assemblies-only bundle, so every downstream portal renders
        // unstyled while the registry's own copy looks fine.
        foreach (var moduleAsset in moduleAssets)
        {
            var local = moduleAsset;
            entries.Add(new NuGetPackageWriter.Entry(
                NuGetPackageWriter.ModuleAssetEntryPathFor(local.RelativePath), local.Open));
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
                        assemblies = moduleFiles.Select(f => f.FileName).ToArray(),
                        minMeshVersion = package.MinMeshVersion,
                        // Declared, never inferred from the archive — BundleReader.ReadModuleAssets
                        // reads THIS list and treats a declared-but-absent entry as an incomplete
                        // bundle, the same all-or-nothing rule the assemblies follow.
                        staticAssets = moduleAssets.Count == 0
                            ? null
                            : moduleAssets.Select(a => a.RelativePath).ToArray(),
                        // 🚨 Where these bytes came from, and — when the registry could not supply
                        // the build this bundle's own assemblies record — what disagreed (#3244).
                        // On the wire, not merely in a log, for the same reason `misses` is: a
                        // consumer that will decline every NodeType binding this module must be
                        // able to say WHY without correlating against the registry's logs.
                        provenance = module.Provenance,
                        divergence = module.Divergence,
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
    /// <see cref="Decide"/>.</b> It is the full inventory, which is precisely what no caller may
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
                    m.MinMeshVersion, m.Source, m.Tier))
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
    /// <param name="Source">🚨 The CACHED registry-source binding — what a local observation says
    /// this package came from (<see cref="PackageManifest.Source"/> on an install record, the
    /// declared package path on a published module). It is the half of the grant pair that is not
    /// the package id, but it is no longer the authority on it: <see cref="Decide"/> prefers the
    /// registry anchor and uses this when the anchor does not carry the package or cannot be
    /// consulted (#1782 gap 2). Null on a record written before the field existed, on something
    /// installed from a source that is not configured, and on an entry that exists only because the
    /// anchor advertises it — in every one of those cases the absence sends the question upstream
    /// rather than answering it.</param>
    /// <param name="Tier">The plan the package declares (<see cref="PackageManifest.Tier"/>), or
    /// null for a baseline package — the cached observation; <see cref="Decide"/> prefers the
    /// anchor's, exactly as it does for <paramref name="Source"/>.</param>
    private sealed record BundleEntry(
        string PackageId, string Version, string PluginId, string? Module = null,
        string? MinMeshVersion = null, string? Source = null, string? Tier = null);
}
