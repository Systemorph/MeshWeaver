using System.Text.Json;
using MeshWeaver.Messaging;
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

    public McpRemoteMeshClientFactory(IMessageHub hub, ILoggerFactory? loggerFactory = null)
    {
        _jsonOptions = hub.JsonSerializerOptions;
        _loggerFactory = loggerFactory;
    }

    public IRemoteMeshClient Create(string remoteBaseUrl, string remoteToken) =>
        new McpRemoteMeshClient(remoteBaseUrl, remoteToken, _jsonOptions, _loggerFactory);
}
