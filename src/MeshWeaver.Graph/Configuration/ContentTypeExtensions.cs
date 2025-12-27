using MeshWeaver.Messaging;

namespace MeshWeaver.Graph.Configuration;

/// <summary>
/// Extension methods for configuring content types on MessageHubConfiguration.
/// </summary>
public static class ContentTypeExtensions
{
    /// <summary>
    /// Registers a content type for this hub's MeshNode.Content.
    /// The type is added to the TypeRegistry for JSON serialization ($type discriminator).
    /// </summary>
    /// <typeparam name="T">The content type to register.</typeparam>
    /// <param name="config">The hub configuration.</param>
    /// <returns>Updated configuration with the content type registered.</returns>
    public static MessageHubConfiguration WithContentType<T>(this MessageHubConfiguration config)
    {
        // Register the type with TypeRegistry for JSON serialization
        config.TypeRegistry.WithType(typeof(T), typeof(T).Name);
        return config;
    }

    /// <summary>
    /// Registers a content type for this hub's MeshNode.Content using runtime Type.
    /// </summary>
    /// <param name="config">The hub configuration.</param>
    /// <param name="contentType">The content type to register.</param>
    /// <returns>Updated configuration with the content type registered.</returns>
    public static MessageHubConfiguration WithContentType(this MessageHubConfiguration config, Type contentType)
    {
        config.TypeRegistry.WithType(contentType, contentType.Name);
        return config;
    }
}
