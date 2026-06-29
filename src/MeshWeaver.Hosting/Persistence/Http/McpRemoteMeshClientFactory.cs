using System.Text.Json;
using MeshWeaver.Mesh.Threading;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Hosting.Persistence.Http;

/// <summary>
/// Production <see cref="IRemoteMeshClientFactory"/>: hands out
/// <see cref="McpRemoteMeshClient"/> instances tied to the local hub's
/// <see cref="IMessageHub.JsonSerializerOptions"/> so types deserialise
/// the same way they would on the wire.
/// </summary>
public sealed class McpRemoteMeshClientFactory : IRemoteMeshClientFactory
{
    private readonly JsonSerializerOptions _jsonOptions;
    private readonly ILoggerFactory? _loggerFactory;
    // The bounded Http pool every client's connect handshake + CallToolAsync leaves
    // route through. Resolved once from the mesh-scoped registry; the Unbounded
    // fallback still offloads off the hub scheduler (never worse than bare FromAsync).
    private readonly IIoPool _httpPool;

    /// <summary>
    /// Creates the factory, capturing the hub's serializer options and resolving the
    /// mesh-scoped Http I/O pool that every produced client routes its calls through.
    /// </summary>
    /// <param name="hub">Local message hub; supplies the JSON serializer options and the I/O pool registry.</param>
    /// <param name="loggerFactory">Optional logger factory passed to each created client.</param>
    public McpRemoteMeshClientFactory(IMessageHub hub, ILoggerFactory? loggerFactory = null)
    {
        _jsonOptions = hub.JsonSerializerOptions;
        _loggerFactory = loggerFactory;
        _httpPool = hub.ServiceProvider.GetService<IoPoolRegistry>()?.Get(IoPoolNames.Http)
                    ?? IoPool.Unbounded;
    }

    /// <inheritdoc />
    public IRemoteMeshClient Create(string remoteBaseUrl, string remoteToken) =>
        new McpRemoteMeshClient(remoteBaseUrl, remoteToken, _jsonOptions, _loggerFactory, _httpPool);
}
