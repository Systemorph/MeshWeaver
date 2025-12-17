using MeshWeaver.Messaging;

namespace MeshWeaver.Mesh;

public static class MeshExtensions
{
    public static MessageHubConfiguration AddMeshTypes(this MessageHubConfiguration config)
    {
        // Register mesh-related types with short names for data consistency
        // Using short names ensures TypeSource registrations use the same collection name
        config.TypeRegistry.WithType(typeof(PingRequest), nameof(PingRequest));
        config.TypeRegistry.WithType(typeof(PingResponse), nameof(PingResponse));
        config.TypeRegistry.WithType(typeof(MeshNode), nameof(MeshNode));
        return config;
    }

}
