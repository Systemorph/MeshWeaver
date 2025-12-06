using MeshWeaver.Messaging;

namespace MeshWeaver.Data;

/// <summary>
/// Registry implementation for unified reference prefix handlers.
/// Each module registers its own handler (e.g., AddData registers "data:", AddLayout registers "area:").
/// </summary>
public class UnifiedReferenceRegistry : IUnifiedReferenceRegistry
{
    private readonly Dictionary<string, IUnifiedReferenceHandler> handlers = new(StringComparer.OrdinalIgnoreCase);

    /// <inheritdoc />
    public void Register(string prefix, IUnifiedReferenceHandler handler)
    {
        handlers[prefix.TrimEnd(':')] = handler;
    }

    /// <inheritdoc />
    public bool TryGetHandler(string prefix, out IUnifiedReferenceHandler? handler)
    {
        return handlers.TryGetValue(prefix.TrimEnd(':'), out handler);
    }

    /// <inheritdoc />
    public IEnumerable<string> Prefixes => handlers.Keys;
}

/// <summary>
/// Handler for data: prefix paths.
/// Maps to CollectionReference or EntityReference.
/// </summary>
public class DataPrefixHandler : IUnifiedReferenceHandler
{
    /// <inheritdoc />
    public Address GetAddress(ContentReference reference)
    {
        var dataRef = (DataContentReference)reference;
        return new Address(dataRef.AddressType, dataRef.AddressId);
    }

    /// <inheritdoc />
    public WorkspaceReference CreateWorkspaceReference(ContentReference reference)
    {
        var dataRef = (DataContentReference)reference;
        return dataRef switch
        {
            { IsEntityReference: true } => new EntityReference(dataRef.Collection!, dataRef.EntityId!),
            { IsCollectionReference: true } => new CollectionReference(dataRef.Collection!),
            _ => throw new NotSupportedException("Default data reference requires a collection. Use format: data:addressType/addressId/collection")
        };
    }
}
