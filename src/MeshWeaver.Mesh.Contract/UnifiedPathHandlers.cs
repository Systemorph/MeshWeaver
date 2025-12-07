using MeshWeaver.Data;
using MeshWeaver.Messaging;

namespace MeshWeaver.Mesh;

/// <summary>
/// Handler for data/ prefix paths.
/// Format: data/addressType/addressId[/path]
/// The path interpretation is done by the local hub's path registry.
/// </summary>
public class DataPathHandler : IUnifiedPathHandler
{
    /// <inheritdoc />
    public (Address Address, WorkspaceReference Reference) Parse(string pathAfterPrefix)
    {
        if (string.IsNullOrEmpty(pathAfterPrefix))
            throw new ArgumentException("Invalid data path: path after 'data/' cannot be empty");

        var parts = pathAfterPrefix.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
            throw new ArgumentException($"Invalid data path: 'data/{pathAfterPrefix}'. Expected at least addressType/addressId");

        var address = new Address(parts[0], parts[1]);
        var path = parts.Length > 2 ? string.Join("/", parts.Skip(2)) : "";

        return (address, new DataPathReference(path));
    }
}

/// <summary>
/// Handler for area/ prefix paths.
/// Format: area/addressType/addressId/areaName[/areaId...]
/// The areaId includes all remaining path segments joined with '/'
/// </summary>
public class AreaPathHandler : IUnifiedPathHandler
{
    /// <inheritdoc />
    public (Address Address, WorkspaceReference Reference) Parse(string pathAfterPrefix)
    {
        if (string.IsNullOrEmpty(pathAfterPrefix))
            throw new ArgumentException("Invalid area path: path after 'area/' cannot be empty");

        var parts = pathAfterPrefix.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 3)
            throw new ArgumentException($"Invalid area path: 'area/{pathAfterPrefix}'. Expected format: area/addressType/addressId/areaName[/areaId]");

        var address = new Address(parts[0], parts[1]);
        var areaNamePart = parts[2];
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
            if (parts.Length > 3)
                areaId = string.Join("/", parts.Skip(3));
        }

        return (address, new LayoutAreaReference(areaName) { Id = areaId });
    }
}

/// <summary>
/// Handler for content/ prefix paths.
/// Format: content/addressType/addressId/collection/path or content/addressType/addressId/collection@partition/path
/// </summary>
public class ContentPathHandler : IUnifiedPathHandler
{
    /// <inheritdoc />
    public (Address Address, WorkspaceReference Reference) Parse(string pathAfterPrefix)
    {
        if (string.IsNullOrEmpty(pathAfterPrefix))
            throw new ArgumentException("Invalid content path: path after 'content/' cannot be empty");

        var parts = pathAfterPrefix.Split('/', 4, StringSplitOptions.None);
        if (parts.Length < 4)
            throw new ArgumentException($"Invalid content path: 'content/{pathAfterPrefix}'. Expected format: content/addressType/addressId/collection/path");

        var addressType = parts[0];
        var addressId = parts[1];
        var collectionPart = parts[2];
        var filePath = parts[3];

        if (string.IsNullOrEmpty(addressType))
            throw new ArgumentException($"Invalid content path: 'content/{pathAfterPrefix}'. Address type cannot be empty");
        if (string.IsNullOrEmpty(addressId))
            throw new ArgumentException($"Invalid content path: 'content/{pathAfterPrefix}'. Address ID cannot be empty");
        if (string.IsNullOrEmpty(collectionPart))
            throw new ArgumentException($"Invalid content path: 'content/{pathAfterPrefix}'. Collection cannot be empty");
        if (string.IsNullOrEmpty(filePath))
            throw new ArgumentException($"Invalid content path: 'content/{pathAfterPrefix}'. File path cannot be empty");

        var address = new Address(addressType, addressId);

        // Check for partition: collection@partition
        var atIndex = collectionPart.IndexOf('@');
        if (atIndex > 0)
        {
            var collection = collectionPart[..atIndex];
            var partition = collectionPart[(atIndex + 1)..];

            if (string.IsNullOrEmpty(collection))
                throw new ArgumentException($"Invalid content path: 'content/{pathAfterPrefix}'. Collection name cannot be empty");
            if (string.IsNullOrEmpty(partition))
                throw new ArgumentException($"Invalid content path: 'content/{pathAfterPrefix}'. Partition cannot be empty when @ is used");

            return (address, new FileReference(collection, filePath, partition));
        }

        return (address, new FileReference(collectionPart, filePath));
    }
}
