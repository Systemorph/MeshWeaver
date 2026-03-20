using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.AI;

/// <summary>
/// Extension methods for adding thread support to mesh node hubs.
/// </summary>
public static class ThreadExtensions
{
    public static MessageHubConfiguration AddThreadSupport(this MessageHubConfiguration configuration)
    {
        configuration.TypeRegistry.AddAITypes();
        return configuration;
    }
}
