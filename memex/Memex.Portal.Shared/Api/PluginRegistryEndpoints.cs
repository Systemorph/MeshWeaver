using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
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
/// <para>🚨 The surface is NOT public: it serves only <b>registered MeshWeaver instances</b>, each
/// holding a token issued at registration and listed in the registry's
/// <c>PluginCatalog:RegistryTokens</c> (see <see cref="PluginRegistryTokens"/>). Requests without a
/// valid <c>Authorization: Bearer</c> token are 401. Only when NO tokens are configured — the
/// local-dev / e2e-stub mode — does the registry answer anonymously; a production registry always
/// configures tokens.</para>
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
            var config = ctx.HttpContext.RequestServices.GetRequiredService<IConfiguration>();
            var issued = IssuedTokens(config);
            if (issued.Count == 0)
                return await next(ctx); // no tokens issued → the open local-dev / e2e-stub registry
            if (PluginRegistryTokens.Validate(ctx.HttpContext.Request.Headers.Authorization, issued))
                return await next(ctx);
            ctx.HttpContext.RequestServices.GetService<ILoggerFactory>()
                ?.CreateLogger(typeof(PluginRegistryEndpoints))
                .LogWarning("Plugin registry: rejected request to {Path} without a valid instance token",
                    ctx.HttpContext.Request.Path);
            return Results.Json(
                new { error = "A registered instance token is required (Authorization: Bearer …)." },
                statusCode: StatusCodes.Status401Unauthorized);
        });

        group.MapGet("", (IMessageHub rootHub, IConfiguration config, CancellationToken ct) =>
            List(rootHub, config, ct));

        group.MapPost("/files", (IMessageHub rootHub, IConfiguration config, FilesBody body, CancellationToken ct) =>
            Files(rootHub, config, body, ct));

        return endpoints;
    }

    /// <summary>The instance tokens this registry has issued — <c>PluginCatalog:RegistryTokens:N</c>
    /// (registering an instance = issuing it a token and provisioning it into this list).</summary>
    private static IReadOnlyList<string> IssuedTokens(IConfiguration config) =>
        config.GetSection(PluginRegistryTokens.SectionName).GetChildren()
            .Select(c => c.Value)
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Select(v => v!)
            .ToList();

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
    private static IObservable<IReadOnlyList<PackageManifest>> ListAll(
        IReadOnlyList<RegistrySource> sources, ILogger? logger)
        => Observable.CombineLatest(sources.Select(s => ListFrom(s, sources.Count == 1, logger)))
            .Select(lists => (IReadOnlyList<PackageManifest>)lists
                .SelectMany(l => l)
                .DistinctBy(p => p.Id, StringComparer.Ordinal)
                .ToList());

    private static Task<IResult> List(IMessageHub hub, IConfiguration config, CancellationToken ct)
    {
        var logger = hub.ServiceProvider.GetService<ILoggerFactory>()?.CreateLogger(typeof(PluginRegistryEndpoints));
        var sources = Sources(hub, config);
        if (sources.Count == 0)
            return Task.FromResult(Results.Content(PluginRegistryPayloads.List([]), "application/json"));
        return ListAll(sources, logger)
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

    private static Task<IResult> Files(IMessageHub hub, IConfiguration config, FilesBody body, CancellationToken ct)
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
        var perSource = sources.Select(s => Observable.Defer(() => ListFrom(s, sources.Count == 1, logger)
            .Select(packages => (Source: s, Package: packages.FirstOrDefault(
                p => string.Equals(p.Id, body.Id, StringComparison.Ordinal))))));
        return perSource.Concat()
            .FirstOrDefaultAsync(hit => hit.Package is not null)
            .SelectMany(hit =>
            {
                if (hit.Package is null)
                {
                    logger?.LogWarning("Plugin registry: files requested for unknown package '{Id}'", body.Id);
                    return Observable.Return((IResult)Results.Json(
                        new { error = $"Unknown package '{body.Id}'" }, statusCode: StatusCodes.Status404NotFound));
                }
                return hit.Source.Source.FetchPackageFiles(hit.Package, hit.Source.GitRef)
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
    /// registry serves its own configured ref).</summary>
    public record FilesBody(string Id, string? Ref = null);
}
