using System.Reactive.Linq;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Hosting.Persistence.Http;

/// <summary>
/// Registers the <see cref="MirrorRequest"/> hub handler. Wire this into the
/// mesh hub's config so any caller (UI click action, MCP tool, hub message)
/// can <c>hub.Post(new MirrorRequest {…}, o =&gt; o.WithTarget(meshAddr))</c>
/// and observe the response.
///
/// <para>The handler creates a <see cref="MirrorOperations"/> per request,
/// runs the recursive copy, and posts a <see cref="MirrorResult"/> back as
/// the response. No DI of orchestrator types needed.</para>
/// </summary>
public static class MirrorHubExtensions
{
    /// <summary>
    /// Adds the <see cref="MirrorRequest"/> handler to a hub configuration,
    /// registers the default <see cref="IRemoteMeshClientFactory"/>
    /// (production: <see cref="McpRemoteMeshClientFactory"/>; tests can
    /// override via <c>WithServices</c> before calling this), and pins the
    /// <see cref="MirrorRequest"/> / <see cref="MirrorResult"/> types on the
    /// hub's TypeRegistry so cross-silo deserialisation works.
    /// </summary>
    public static MessageHubConfiguration AddMirrorHandler(this MessageHubConfiguration config)
    {
        config.TypeRegistry.WithType(typeof(MirrorRequest), nameof(MirrorRequest));
        config.TypeRegistry.WithType(typeof(MirrorResult), nameof(MirrorResult));
        return config
            .WithServices(services =>
            {
                services.TryAddSingleton<IRemoteMeshClientFactory, McpRemoteMeshClientFactory>();
                return services;
            })
            .WithHandler<MirrorRequest>(HandleMirror);
    }

    /// <summary>
    /// Synchronous-shape handler per the canonical pattern: returns
    /// <c>request.Processed()</c> immediately; the actual work (HTTP I/O +
    /// recursive copy) runs on the observable chain and posts the response
    /// to the original sender via <see cref="PostOptions.ResponseFor"/> when
    /// it's done.
    /// </summary>
    internal static IMessageDelivery HandleMirror(IMessageHub hub, IMessageDelivery<MirrorRequest> request)
    {
        var logger = hub.ServiceProvider.GetService<ILoggerFactory>()
            ?.CreateLogger("MeshWeaver.Mirror")
            ?? Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;

        var local = hub.ServiceProvider.GetService<IStorageAdapter>();
        var factory = hub.ServiceProvider.GetService<IRemoteMeshClientFactory>();

        if (local is null || factory is null)
        {
            hub.Post(new MirrorResult
            {
                Status = "Error",
                Direction = request.Message.Direction,
                SourcePath = request.Message.SourcePath,
                TargetPath = request.Message.TargetPath ?? request.Message.SourcePath,
                Error = local is null
                    ? "No IStorageAdapter registered — mirroring requires a local persistence backend."
                    : "No IRemoteMeshClientFactory registered — call AddMirrorHandler() in your hub config.",
            }, o => o.ResponseFor(request));
            return request.Processed();
        }

        var ops = new MirrorOperations(local, factory, logger, hub.JsonSerializerOptions);

        ops.Run(request.Message)
            .Take(1)
            .Subscribe(
                result => hub.Post(result, o => o.ResponseFor(request)),
                ex =>
                {
                    logger.LogError(ex, "Mirror failed: {Source} {Direction} {Url}",
                        request.Message.SourcePath, request.Message.Direction, request.Message.RemoteBaseUrl);
                    hub.Post(new MirrorResult
                    {
                        Status = "Error",
                        Direction = request.Message.Direction,
                        SourcePath = request.Message.SourcePath,
                        TargetPath = request.Message.TargetPath ?? request.Message.SourcePath,
                        Error = ex.Message,
                    }, o => o.ResponseFor(request));
                });

        return request.Processed();
    }
}
