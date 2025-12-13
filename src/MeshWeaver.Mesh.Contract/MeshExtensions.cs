using MeshWeaver.Messaging;

namespace MeshWeaver.Mesh;

public static class MeshExtensions
{
    public static MessageHubConfiguration AddMeshTypes(this MessageHubConfiguration config)
    {
        // Register mesh-related types (but not address types - they're now unified)
        config.TypeRegistry.WithTypes(typeof(PingRequest), typeof(PingResponse), typeof(MeshNode));
        return config;
    }

}
