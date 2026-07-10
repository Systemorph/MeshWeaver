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
/// The PUBLIC plugin-registry surface — this instance acting as the distribution point for plugin
/// modules. <c>GET /api/plugins</c> lists the catalog; <c>POST /api/plugins/files</c> returns one
/// package's files. Both are backed by the registry's configured git <see cref="IPackageSource"/>
/// (<c>PluginCatalog:SourceRepoPath</c> — e.g. the plugins repo via GitSync), so a consuming instance
/// browses/installs over HTTP with NO git/GitHub credentials of its own: the registry's GitHub App
/// credential stays here and is never handed out (npm/NuGet-style encapsulation). Only curated
/// packages (folders carrying <c>package.json</c>) are exposed, so anonymous read is safe by design.
///
/// <para>Consumed by <see cref="RegistryPackageSource"/>; the wire shapes are produced by
/// <see cref="PluginRegistryPayloads"/> so producer and consumer cannot drift.</para>
/// </summary>
public static class PluginRegistryEndpoints
{
    /// <summary>Maps the anonymous <c>/api/plugins</c> registry group. Call alongside <c>MapMeshApi</c>.</summary>
    public static IEndpointRouteBuilder MapPluginRegistry(this IEndpointRouteBuilder endpoints)
    {
        // PUBLIC — no auth: a plugin registry is meant to be pulled by any installation. The registry
        // serves only curated packages from its configured source; its own credentials never leave.
        var group = endpoints.MapGroup(RegistryPackageSource.RoutePrefix).AllowAnonymous();

        group.MapGet("", (IMessageHub rootHub, IConfiguration config, CancellationToken ct) =>
            List(rootHub, config, ct));

        group.MapPost("/files", (IMessageHub rootHub, IConfiguration config, FilesBody body, CancellationToken ct) =>
            Files(rootHub, config, body, ct));

        return endpoints;
    }

    // Builds the registry's git source + the ref it serves, from PluginCatalog config. The registry is
    // authoritative on the ref (its configured SourceRef); a consumer's ref is advisory and ignored.
    private static (IPackageSource? Source, string GitRef) Source(IMessageHub hub, IConfiguration config)
    {
        var logger = hub.ServiceProvider.GetService<ILoggerFactory>()?.CreateLogger(typeof(PluginRegistryEndpoints));
        var repo = config["PluginCatalog:SourceRepoPath"] ?? "";
        var subdir = config["PluginCatalog:SourceSubdir"] ?? "catalog";
        var gitRef = config["PluginCatalog:SourceRef"] ?? "HEAD";
        return (PackageSources.FromRepo(hub, repo, subdir, logger), gitRef);
    }

    private static Task<IResult> List(IMessageHub hub, IConfiguration config, CancellationToken ct)
    {
        var logger = hub.ServiceProvider.GetService<ILoggerFactory>()?.CreateLogger(typeof(PluginRegistryEndpoints));
        var (source, gitRef) = Source(hub, config);
        if (source is null)
            return Task.FromResult(Results.Content(PluginRegistryPayloads.List([]), "application/json"));
        return source.ListPackages(gitRef)
            .Select(list => (IResult)Results.Content(PluginRegistryPayloads.List(list), "application/json"))
            .Catch((Exception ex) =>
            {
                // Surface the failure (502) rather than hide it as an empty catalog — the consumer's
                // source catches it, logs, and degrades to empty; the registry log names the cause.
                logger?.LogWarning(ex, "Plugin registry: listing packages @ {Ref} failed", gitRef);
                return Observable.Return((IResult)Results.Json(
                    new { error = ex.Message }, statusCode: StatusCodes.Status502BadGateway));
            })
            .FirstAsync().ToTask(ct);
    }

    private static Task<IResult> Files(IMessageHub hub, IConfiguration config, FilesBody body, CancellationToken ct)
    {
        var logger = hub.ServiceProvider.GetService<ILoggerFactory>()?.CreateLogger(typeof(PluginRegistryEndpoints));
        var (source, gitRef) = Source(hub, config);
        if (source is null || string.IsNullOrWhiteSpace(body.Id))
            return Task.FromResult(Results.Content(PluginRegistryPayloads.Files([]), "application/json"));
        var manifest = new PackageManifest { Id = body.Id, SourceFolder = body.SourceFolder };
        return source.FetchPackageFiles(manifest, gitRef)
            .Select(files => (IResult)Results.Content(PluginRegistryPayloads.Files(files), "application/json"))
            .Catch((Exception ex) =>
            {
                logger?.LogWarning(ex, "Plugin registry: fetching files for {Id} @ {Ref} failed", body.Id, gitRef);
                return Observable.Return((IResult)Results.Json(
                    new { error = ex.Message }, statusCode: StatusCodes.Status502BadGateway));
            })
            .FirstAsync().ToTask(ct);
    }

    /// <summary>Fetch-files request: the package <paramref name="Id"/> + its <paramref name="SourceFolder"/>
    /// (from the catalog listing). <paramref name="Ref"/> is advisory — the registry serves its own ref.</summary>
    public record FilesBody(string Id, string? SourceFolder = null, string? Ref = null);
}
