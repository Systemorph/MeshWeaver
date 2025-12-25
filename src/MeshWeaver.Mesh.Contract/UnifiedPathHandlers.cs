using MeshWeaver.Data;
using MeshWeaver.Messaging;

namespace MeshWeaver.Mesh;

/// <summary>
/// Handler for data keyword paths.
/// Format: addressType/addressId/data[/path]
/// The path interpretation is done by the local hub's path registry.
/// </summary>
public class DataPathHandler : IUnifiedPathHandler
{
    /// <inheritdoc />
    public (Address Address, WorkspaceReference Reference) Parse(string addressType, string addressId, string remainingPath)
    {
        var address = new Address(addressType, addressId);
        return (address, new DataPathReference(remainingPath ?? ""));
    }
}

/// <summary>
/// Handler for area keyword paths.
/// Format: addressType/addressId/area/areaName[/areaId...]
/// Or: addressType/addressId/areaName[/areaId...] (when area is the default)
/// The areaId includes all remaining path segments joined with '/'
/// </summary>
public class AreaPathHandler : IUnifiedPathHandler
{
    /// <inheritdoc />
    public (Address Address, WorkspaceReference Reference) Parse(string addressType, string addressId, string remainingPath)
    {
        var address = new Address(addressType, addressId);

        if (string.IsNullOrEmpty(remainingPath))
            return (address, new LayoutAreaReference(null) { Id = null });

        var parts = remainingPath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
            return (address, new LayoutAreaReference(null) { Id = null });

        var areaNamePart = parts[0];
        string areaName;
        string? areaId = null;

        // Check for ? separator in area name (e.g., SalesGrowthSummary?Year=2025)
        var queryIndex = areaNamePart.IndexOf('?');
        if (queryIndex > 0)
        {
            areaName = areaNamePart[..queryIndex];
            areaId = areaNamePart[(queryIndex + 1)..];
        }
        else
        {
            areaName = areaNamePart;
            // Join all remaining parts as areaId
            if (parts.Length > 1)
                areaId = string.Join("/", parts.Skip(1));
        }

        return (address, new LayoutAreaReference(areaName) { Id = areaId });
    }
}

/// <summary>
/// Handler for content keyword paths.
/// Format: addressType/addressId/content/collection/path or addressType/addressId/content/collection@partition/path
/// </summary>
public class ContentPathHandler : IUnifiedPathHandler
{
    /// <inheritdoc />
    public (Address Address, WorkspaceReference Reference) Parse(string addressType, string addressId, string remainingPath)
    {
        if (string.IsNullOrEmpty(remainingPath))
            throw new ArgumentException("Invalid content path: path after 'content' cannot be empty");

        var parts = remainingPath.Split('/', 2, StringSplitOptions.None);
        if (parts.Length < 2 || string.IsNullOrEmpty(parts[1]))
            throw new ArgumentException($"Invalid content path: '{addressType}/{addressId}/content/{remainingPath}'. Expected format: addressType/addressId/content/collection/path");

        var collectionPart = parts[0];
        var filePath = parts[1];

        if (string.IsNullOrEmpty(collectionPart))
            throw new ArgumentException($"Invalid content path: collection cannot be empty");

        var address = new Address(addressType, addressId);

        // Check for partition: collection@partition
        var atIndex = collectionPart.IndexOf('@');
        if (atIndex > 0)
        {
            var collection = collectionPart[..atIndex];
            var partition = collectionPart[(atIndex + 1)..];

            if (string.IsNullOrEmpty(collection))
                throw new ArgumentException("Invalid content path: collection name cannot be empty");
            if (string.IsNullOrEmpty(partition))
                throw new ArgumentException("Invalid content path: partition cannot be empty when @ is used");

            return (address, new FileReference(collection, filePath, partition));
        }

        return (address, new FileReference(collectionPart, filePath));
    }
}

/// <summary>
/// Handler for type keyword paths.
/// Format: addressType/addressId/type
/// The node type is encoded in the address (addressType/addressId).
/// Returns NodeTypeReference as a marker to get the NodeTypeData.
/// </summary>
public class TypePathHandler : IUnifiedPathHandler
{
    /// <inheritdoc />
    public (Address Address, WorkspaceReference Reference) Parse(string addressType, string addressId, string remainingPath)
    {
        var address = new Address(addressType, addressId);
        // The node type is already in the address, NodeTypeReference is just a marker
        return (address, new NodeTypeReference());
    }
}
