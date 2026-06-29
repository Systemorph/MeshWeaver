using System.Reactive.Linq;
using MeshWeaver.Connection.SignalR;
using MeshWeaver.Data;
using MeshWeaver.Graph;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Memex.Client.Services;

/// <summary>
/// Joins a remote mesh: opens the live SignalR connection via <c>ConnectToMesh</c> AND persists a
/// <c>MemexInstance</c> node so the next boot reconnects (the bootstrap reads instances with
/// <c>GetQuery</c> and dials each authenticated one). The token comes from <see cref="MeshOAuthClient"/>
/// (OAuth) or a manual paste.
/// </summary>
public sealed class MeshConnector
{
    private readonly IMessageHub _hub;
    private readonly ILogger<MeshConnector>? _logger;

    public MeshConnector(IMessageHub hub)
    {
        _hub = hub;
        _logger = hub.ServiceProvider.GetService<ILoggerFactory>()?.CreateLogger<MeshConnector>();
    }

    /// <summary>Connect to (and remember) a remote mesh. The mesh id is the URL host, which keys the
    /// SignalR connection and routes <c>{meshId}/…</c> targets to it.</summary>
    public void Connect(string url, string token, string? displayName = null)
    {
        var meshId = new Uri(url).Host;
        var name = string.IsNullOrWhiteSpace(displayName) ? meshId : displayName!;

        // Live connection now — the multi-remote route sends {meshId}/… targets over it.
        _hub.ConnectToMesh(
            url.TrimEnd('/') + "/signalr",
            () => Task.FromResult<string?>(token),
            AddressExtensions.CreateMeshAddress(meshId));

        // Persist as a MemexInstance node (create-or-update) so the next launch reconnects.
        var content = new MemexInstanceContent
        {
            DisplayName = name,
            Url = url,
            Token = token,
            MeshId = meshId,
            LastConnected = DateTime.UtcNow,
        };
        var path = MemexInstanceNodeType.PathFor(meshId);
        var workspace = _hub.GetWorkspace();

        workspace.GetQuery("connect-" + meshId, $"path:{path}")
            .Take(1).Timeout(TimeSpan.FromSeconds(10))
            .Subscribe(
                existing =>
                {
                    if (existing.Any())
                        workspace.GetMeshNodeStream(path)
                            .Update(n => n with { Name = name, Content = content })
                            .Subscribe(_ => { }, ex => _logger?.LogWarning(ex, "instance update failed for {Path}", path));
                    else
                        _hub.ServiceProvider.GetRequiredService<IMeshService>()
                            .CreateNode(new MeshNode(meshId, MemexInstanceNodeType.Segment)
                            {
                                NodeType = MemexInstanceNodeType.NodeType,
                                Name = name,
                                Content = content,
                            })
                            .Subscribe(_ => { }, ex => _logger?.LogWarning(ex, "instance create failed for {Path}", path));
                },
                ex => _logger?.LogWarning(ex, "instance existence check failed for {Path}", path));

        _logger?.LogInformation("Connecting to mesh {MeshId} at {Url}", meshId, url);

        // Detect-and-notify: if the remote runs a newer platform version than this bundled app, prompt
        // the user to update + relaunch (a sandboxed app can't self-replace). Best-effort, error-sunk.
        new UpdateNotificationService(
                _hub.ServiceProvider.GetService<ILoggerFactory>()?.CreateLogger<UpdateNotificationService>())
            .CheckRemote(workspace, meshId);
    }
}
