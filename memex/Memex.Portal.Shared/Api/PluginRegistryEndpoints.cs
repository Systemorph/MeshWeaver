using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Messaging;
using MeshWeaver.PluginCatalog;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Memex.Portal.Shared.Api;

/// <summary>
/// The plugin-registry surface — this instance acting as the distribution point for plugin
/// modules. <c>GET /api/plugins</c> lists the catalog; <c>POST /api/plugins/files</c> returns one
/// package's files. Both are backed by the registry's configured git <see cref="IPackageSource"/>s
/// (<c>PluginCatalog:Sources:N:*</c> — e.g. the plugins repo AND an education repo — or the legacy
/// single <c>PluginCatalog:SourceRepoPath</c>, via GitSync), so a consuming instance
/// browses/installs over HTTP with NO git/GitHub credentials of its own: the registry's GitHub App
/// credential stays here and is never handed out (npm/NuGet-style encapsulation).
///
/// <para>🚨 The surface is NOT public: it serves only <b>registered MeshWeaver instances</b>. A
/// caller presents its instance key (<c>Authorization: Bearer mwi_…</c>); the key resolves to a
/// <see cref="MeshWeaverInstance"/> node and the admin-owned <see cref="PluginGrant"/> that says
/// which (source, package) pairs it may read — see <see cref="InstanceRegistryAuthenticator"/>.
/// Requests without a valid key are 401, and an authenticated caller sees ONLY its granted packages,
/// in both the listing and the file fetch.</para>
///
/// <para>This replaced a flat <c>PluginCatalog:RegistryTokens</c> allowlist that was open when
/// unset — which is how this registry served its private sources to anonymous callers until
/// 2026-08-06. The gate now <b>fails closed</b>: <c>PluginCatalog:RequireInstanceKey</c> defaults to
/// true, and the anonymous mode must be asked for explicitly (local dev / the e2e stub) and warns on
/// every request. Consumers need no config change — the existing
/// <c>PluginCatalog:RegistryToken</c> key now carries the instance key.</para>
///
/// <para>Consumed by <see cref="RegistryPackageSource"/>; the wire shapes are produced by
/// <see cref="PluginRegistryPayloads"/> so producer and consumer cannot drift.</para>
/// </summary>
public static class PluginRegistryEndpoints
{
    /// <summary>Maps the token-gated <c>/api/plugins</c> registry group. Call alongside <c>MapMeshApi</c>.</summary>
    public static IEndpointRouteBuilder MapPluginRegistry(this IEndpointRouteBuilder endpoints)
    {
        // AllowAnonymous at the ASP.NET auth layer: callers are INSTANCES, not signed-in users, so
        // the user auth schemes don't apply. The real gate is the instance-token filter below —
        // only registered instances (their issued token in PluginCatalog:RegistryTokens) get through.
        var group = endpoints.MapGroup(RegistryPackageSource.RoutePrefix).AllowAnonymous();

        group.AddEndpointFilter(async (ctx, next) =>
        {
            var http = ctx.HttpContext;
            var config = http.RequestServices.GetRequiredService<IConfiguration>();
            var logger = http.RequestServices.GetService<ILoggerFactory>()
                ?.CreateLogger(typeof(PluginRegistryEndpoints));

            var authenticator = http.RequestServices.GetRequiredService<InstanceRegistryAuthenticator>();
            var caller = await authenticator.Authenticate(http.Request.Headers.Authorization)
                .FirstAsync().ToTask(http.RequestAborted);

            if (caller is not null)
            {
                http.Items[CallerItemKey] = caller;
                return await next(ctx);
            }

            // Fail CLOSED by default. The legacy open mode survives only for local dev / the e2e
            // stub, and only when explicitly asked for — it is the mode that served this registry's
            // private sources to anyone who knew the URL (2026-08-06).
            if (!RequiresInstanceKey(config))
            {
                logger?.LogWarning(
                    "Plugin registry: serving {Path} ANONYMOUSLY — {Key} is false. Every configured "
                    + "source is readable by any caller. Register instances and remove this setting.",
                    http.Request.Path, RequireInstanceKeyConfigKey);
                return await next(ctx);
            }

            logger?.LogWarning("Plugin registry: rejected {Path} — no valid instance key presented",
                http.Request.Path);
            return Results.Json(
                new { error = "A registered instance key is required (Authorization: Bearer mwi_…)." },
                statusCode: StatusCodes.Status401Unauthorized);
        });

        group.MapGet("", (HttpContext http, IMessageHub rootHub, IConfiguration config, CancellationToken ct) =>
            List(rootHub, config, Caller(http), ct));

        group.MapPost("/files", (HttpContext http, IMessageHub rootHub, IConfiguration config,
                FilesBody body, CancellationToken ct) =>
            Files(rootHub, config, body, Caller(http), ct));

        return endpoints;
    }

    /// <summary><see cref="HttpContext.Items"/> key holding the authenticated caller.</summary>
    private const string CallerItemKey = "PluginRegistry.Caller";

    /// <summary>Config flag gating the legacy anonymous mode. Absent ⇒ true ⇒ a key is required.</summary>
    private const string RequireInstanceKeyConfigKey = "PluginCatalog:RequireInstanceKey";

    /// <summary>Whether an instance key is required. <b>Defaults to true</b> — a registry that
    /// configures nothing refuses anonymous callers rather than serving them everything.</summary>
    private static bool RequiresInstanceKey(IConfiguration config) =>
        !bool.TryParse(config[RequireInstanceKeyConfigKey], out var required) || required;

    /// <summary>The authenticated caller, or null in the legacy anonymous mode (which sees
    /// everything — there is no instance to scope the catalog to).</summary>
    private static AuthenticatedInstance? Caller(HttpContext http) =>
        http.Items.TryGetValue(CallerItemKey, out var v) ? v as AuthenticatedInstance : null;

    /// <summary>One configured registry source: the git package source, the ref it serves, and a
    /// display name (for logs). The registry is authoritative on each source's ref (its configured
    /// <c>Ref</c>/<c>SourceRef</c>); a consumer's ref is advisory and ignored.</summary>
    private sealed record RegistrySource(IPackageSource Source, string GitRef, string Name);

    // Builds the registry's git sources from PluginCatalog config. Multi-source form:
    //   PluginCatalog:Sources:N:{RepoPath,Subdir,Ref,Format,Name}
    // (e.g. the plugins repo AND an education repo). When no Sources list is configured, the legacy
    // single-source keys (PluginCatalog:SourceRepoPath/SourceSubdir/SourceRef/SourceFormat) apply.
    private static IReadOnlyList<RegistrySource> Sources(IMessageHub hub, IConfiguration config)
    {
        var logger = hub.ServiceProvider.GetService<ILoggerFactory>()?.CreateLogger(typeof(PluginRegistryEndpoints));

        RegistrySource? Build(string? repo, string? subdir, string? gitRef, string? format, string? name)
        {
            // Default to the node-native repo format (what MeshWeaver.Plugins ships); a package.json
            // repo can opt in with Format=package-json.
            var nodeRepo = !string.Equals(format ?? "node-repo", "package-json", StringComparison.OrdinalIgnoreCase);
            var source = PackageSources.FromRepo(hub, repo, subdir, logger, nodeRepo);
            return source is null ? null : new RegistrySource(source, gitRef ?? "HEAD", name ?? repo ?? "");
        }

        var configured = config.GetSection("PluginCatalog:Sources").GetChildren()
            .Select(s => Build(s["RepoPath"], s["Subdir"] ?? "", s["Ref"], s["Format"], s["Name"]))
            .Where(s => s is not null)
            .Select(s => s!)
            .ToList();
        if (configured.Count > 0)
            return configured;

        var legacy = Build(
            config["PluginCatalog:SourceRepoPath"],
            config["PluginCatalog:SourceSubdir"] ?? "catalog",
            config["PluginCatalog:SourceRef"],
            config["PluginCatalog:SourceFormat"],
            name: "registry");
        return legacy is null ? [] : [legacy];
    }

    // Lists one source's packages. With SEVERAL sources a single failing repo must not take the whole
    // catalog down (degrade to empty + log); with exactly ONE source the failure propagates so List
    // can surface it as a 502 (the historical single-source behavior — the consumer sees the cause).
    private static IObservable<IReadOnlyList<PackageManifest>> ListFrom(
        RegistrySource s, bool soleSource, ILogger? logger)
        => soleSource
            ? s.Source.ListPackages(s.GitRef)
            : s.Source.ListPackages(s.GitRef).Catch((Exception ex) =>
            {
                logger?.LogWarning(ex, "Plugin registry: listing packages from {Name} @ {Ref} failed",
                    s.Name, s.GitRef);
                return Observable.Return((IReadOnlyList<PackageManifest>)[]);
            });

    // Merged catalog across all sources; on an id collision the FIRST configured source wins
    // (the registry curates the order).
    //
    // 🚨 Scoped to what the CALLER was granted. The grant is per (source, package) — an instance may
    // hold a whole source (Plugins/*) or a single plugin out of one (Reinsurance/UWDeepfield) — so
    // the filter runs BEFORE the merge, while each package still knows which source it came from.
    // A caller therefore cannot even learn that an ungranted package exists.
    private static IObservable<IReadOnlyList<PackageManifest>> ListAll(
        IReadOnlyList<RegistrySource> sources, AuthenticatedInstance? caller, ILogger? logger)
        => Observable.CombineLatest(sources.Select(s =>
                ListFrom(s, sources.Count == 1, logger).Select(list => (Source: s, Packages: list))))
            .Select(perSource => (IReadOnlyList<PackageManifest>)perSource
                .SelectMany(x => x.Packages.Where(p => IsGranted(caller, x.Source, p)))
                .DistinctBy(p => p.Id, StringComparer.Ordinal)
                .ToList());

    // Legacy anonymous mode has no instance to scope to and sees everything — that mode is gated
    // by PluginCatalog:RequireInstanceKey and warns on every request.
    private static bool IsGranted(AuthenticatedInstance? caller, RegistrySource source, PackageManifest package)
        => caller is null || caller.Allows(source.Name, package.Id);

    private static Task<IResult> List(
        IMessageHub hub, IConfiguration config, AuthenticatedInstance? caller, CancellationToken ct)
    {
        var logger = hub.ServiceProvider.GetService<ILoggerFactory>()?.CreateLogger(typeof(PluginRegistryEndpoints));
        var sources = Sources(hub, config);
        if (sources.Count == 0)
            return Task.FromResult(Results.Content(PluginRegistryPayloads.List([]), "application/json"));
        return ListAll(sources, caller, logger)
            .Select(list => (IResult)Results.Content(PluginRegistryPayloads.List(list), "application/json"))
            .Catch((Exception ex) =>
            {
                // Surface the failure (502) rather than hide it as an empty catalog — the consumer's
                // source catches it, logs, and degrades to empty; the registry log names the cause.
                logger?.LogWarning(ex, "Plugin registry: listing packages failed");
                return Observable.Return((IResult)Results.Json(
                    new { error = ex.Message }, statusCode: StatusCodes.Status502BadGateway));
            })
            .FirstAsync().ToTask(ct);
    }

    private static Task<IResult> Files(
        IMessageHub hub, IConfiguration config, FilesBody body, AuthenticatedInstance? caller, CancellationToken ct)
    {
        var logger = hub.ServiceProvider.GetService<ILoggerFactory>()?.CreateLogger(typeof(PluginRegistryEndpoints));
        // A missing id is a malformed request → 400 (don't disguise it as a valid empty package).
        if (string.IsNullOrWhiteSpace(body.Id))
            return Task.FromResult(Results.Json(
                new { error = "Field 'id' is required." }, statusCode: StatusCodes.Status400BadRequest));
        var sources = Sources(hub, config);
        if (sources.Count == 0) // registry not configured → an empty (not erroneous) response
            return Task.FromResult(Results.Content(PluginRegistryPayloads.Files([]), "application/json"));

        // 🚨 Resolve the requested package from the registry's CURATED catalog by ID and fetch what THAT
        // package ships — never a client-supplied path. The logic is "the id must match a published
        // package", independent of source format; trusting a caller-provided folder would let an
        // anonymous caller read arbitrary repo files. An unknown id is 404. A security property, not a hint.
        // Sources resolve SEQUENTIALLY in configured order (same precedence as the merged list) and
        // short-circuit on the first hit — later sources are never even listed when an earlier one
        // has the package (Concat subscribes lazily; FirstOrDefaultAsync unsubscribes on the match).
        //
        // 🚨 The caller's grant is part of the resolution, not a check bolted on after it: a package
        // the caller was not granted simply does not match, so an ungranted id can neither be
        // fetched NOR shadow a granted package of the same id in a later source.
        var perSource = sources.Select(s => Observable.Defer(() => ListFrom(s, sources.Count == 1, logger)
            .Select(packages => (Source: s, Package: packages.FirstOrDefault(
                p => string.Equals(p.Id, body.Id, StringComparison.Ordinal) && IsGranted(caller, s, p))))));
        return perSource.Concat()
            .FirstOrDefaultAsync(hit => hit.Package is not null)
            .SelectMany(hit =>
            {
                if (hit.Package is null)
                {
                    // 404, never 403 — the listing already hides ungranted packages, so answering
                    // "forbidden" here would turn /files into an oracle for what else this registry
                    // carries. The log records which instance asked, so operators can still see it.
                    logger?.LogWarning(
                        "Plugin registry: files requested for package '{Id}' — unknown, or not granted to {Instance}",
                        body.Id, caller?.Instance.InstanceId ?? "(anonymous)");
                    return Observable.Return((IResult)Results.Json(
                        new { error = $"Unknown package '{body.Id}'" }, statusCode: StatusCodes.Status404NotFound));
                }
                // The paths subset (manifest-diff fast path) only FILTERS within the resolved
                // package's own files — the curated-id resolution above stays the security gate.
                return hit.Source.Source.FetchPackageFiles(hit.Package, hit.Source.GitRef, body.Paths)
                    .Select(files => (IResult)Results.Content(PluginRegistryPayloads.Files(files), "application/json"));
            })
            .Catch((Exception ex) =>
            {
                logger?.LogWarning(ex, "Plugin registry: fetching files for {Id} failed", body.Id);
                return Observable.Return((IResult)Results.Json(
                    new { error = ex.Message }, statusCode: StatusCodes.Status502BadGateway));
            })
            .FirstAsync().ToTask(ct);
    }

    /// <summary>Fetch-files request: only the package <paramref name="Id"/> is authoritative — the
    /// registry resolves the folder from its curated catalog. <paramref name="Ref"/> is advisory (the
    /// registry serves its own configured ref). <paramref name="Paths"/> optionally narrows the
    /// response to a subset of the package's own repo-relative paths (the manifest-diff incremental
    /// fast path); null = all files. Unknown paths are simply absent from the response.</summary>
    public record FilesBody(string Id, string? Ref = null, IReadOnlyList<string>? Paths = null);
}
