namespace MeshWeaver.Data;

/// <summary>
/// Base class for all content reference types that can be parsed from path patterns.
/// All paths include address information for routing: prefix:addressType/addressId/...
/// Supports multiple path formats:
/// - Content: content:addressType/addressId/collection/path or content:addressType/addressId/collection@partition/path
/// - Data: data:addressType/addressId[/collection[/entityId]]
/// - Layout Area: area:addressType/addressId/areaName[/areaId]
/// </summary>
public abstract record ContentReference
{
    /// <summary>
    /// The address type for routing (first part of the address).
    /// </summary>
    public abstract string AddressType { get; }

    /// <summary>
    /// The address ID for routing (second part of the address).
    /// </summary>
    public abstract string AddressId { get; }

    /// <summary>
    /// Converts the content reference back to its path string representation.
    /// </summary>
    public abstract string ToPath();

    /// <summary>
    /// Parses a path string into the appropriate ContentReference type.
    /// </summary>
    /// <param name="path">The path to parse</param>
    /// <returns>A ContentReference subtype based on the path pattern</returns>
    /// <exception cref="ArgumentException">When the path format is invalid</exception>
    public static ContentReference Parse(string path)
    {
        if (string.IsNullOrEmpty(path))
            throw new ArgumentException("Path cannot be empty", nameof(path));

        // Check for special prefixes
        if (path.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            return DataContentReference.ParseDataPath(path[5..]);

        if (path.StartsWith("area:", StringComparison.OrdinalIgnoreCase))
            return LayoutAreaContentReference.ParseAreaPath(path[5..]);

        if (path.StartsWith("content:", StringComparison.OrdinalIgnoreCase))
            return FileContentReference.ParseContentPath(path[8..]);

        // Unknown prefix
        throw new ArgumentException($"Invalid path: '{path}'. Expected prefix: data:, area:, or content:");
    }

    /// <summary>
    /// Tries to parse a path string into a ContentReference.
    /// </summary>
    /// <param name="path">The path to parse</param>
    /// <param name="reference">The parsed reference if successful</param>
    /// <returns>True if parsing succeeded, false otherwise</returns>
    public static bool TryParse(string path, out ContentReference? reference)
    {
        try
        {
            reference = Parse(path);
            return true;
        }
        catch
        {
            reference = null;
            return false;
        }
    }
}

/// <summary>
/// Reference to file-based content.
/// Supports formats:
/// - content:addressType/addressId/collection/path/to/file
/// - content:addressType/addressId/collection@partition/path/to/file
/// </summary>
public record FileContentReference(
    string AddressType,
    string AddressId,
    string Collection,
    string FilePath,
    string? Partition = null) : ContentReference
{
    /// <inheritdoc />
    public override string AddressType { get; } = AddressType;

    /// <inheritdoc />
    public override string AddressId { get; } = AddressId;

    /// <inheritdoc />
    public override string ToPath() =>
        Partition != null
            ? $"content:{AddressType}/{AddressId}/{Collection}@{Partition}/{FilePath}"
            : $"content:{AddressType}/{AddressId}/{Collection}/{FilePath}";

    /// <summary>
    /// Parses a content path (the part after "content:").
    /// Format: addressType/addressId/collection/path or addressType/addressId/collection@partition/path
    /// </summary>
    public static FileContentReference ParseContentPath(string remainder)
    {
        if (string.IsNullOrEmpty(remainder))
            throw new ArgumentException("Invalid content path: path after 'content:' cannot be empty");

        var parts = remainder.Split('/', 4, StringSplitOptions.None);
        if (parts.Length < 4)
            throw new ArgumentException($"Invalid content path: 'content:{remainder}'. Expected format: content:addressType/addressId/collection/path");

        var addressType = parts[0];
        var addressId = parts[1];
        var collectionPart = parts[2];
        var filePath = parts[3];

        if (string.IsNullOrEmpty(addressType))
            throw new ArgumentException($"Invalid content path: 'content:{remainder}'. Address type cannot be empty");
        if (string.IsNullOrEmpty(addressId))
            throw new ArgumentException($"Invalid content path: 'content:{remainder}'. Address ID cannot be empty");
        if (string.IsNullOrEmpty(collectionPart))
            throw new ArgumentException($"Invalid content path: 'content:{remainder}'. Collection cannot be empty");
        if (string.IsNullOrEmpty(filePath))
            throw new ArgumentException($"Invalid content path: 'content:{remainder}'. File path cannot be empty");

        // Check for partition: collection@partition
        var atIndex = collectionPart.IndexOf('@');
        if (atIndex > 0)
        {
            var collection = collectionPart[..atIndex];
            var partition = collectionPart[(atIndex + 1)..];

            if (string.IsNullOrEmpty(collection))
                throw new ArgumentException($"Invalid content path: 'content:{remainder}'. Collection name cannot be empty");
            if (string.IsNullOrEmpty(partition))
                throw new ArgumentException($"Invalid content path: 'content:{remainder}'. Partition cannot be empty when @ is used");

            return new FileContentReference(addressType, addressId, collection, filePath, partition);
        }

        return new FileContentReference(addressType, addressId, collectionPart, filePath);
    }

    /// <inheritdoc />
    public override string ToString() => ToPath();
}

/// <summary>
/// Reference to data content.
/// Supports formats:
/// - data:addressType/addressId (returns default data entity)
/// - data:addressType/addressId/collection (returns entire collection)
/// - data:addressType/addressId/collection/entityId (returns single entity)
/// </summary>
public record DataContentReference(
    string AddressType,
    string AddressId,
    string? Collection = null,
    string? EntityId = null) : ContentReference
{
    /// <inheritdoc />
    public override string AddressType { get; } = AddressType;

    /// <inheritdoc />
    public override string AddressId { get; } = AddressId;

    /// <summary>
    /// True if this reference uses the default data reference (no collection specified).
    /// </summary>
    public bool IsDefaultReference => Collection == null && EntityId == null;

    /// <summary>
    /// True if this reference is for an entire collection.
    /// </summary>
    public bool IsCollectionReference => Collection != null && EntityId == null;

    /// <summary>
    /// True if this reference is for a single entity.
    /// </summary>
    public bool IsEntityReference => Collection != null && EntityId != null;

    /// <inheritdoc />
    public override string ToPath()
    {
        var basePath = $"data:{AddressType}/{AddressId}";
        if (Collection != null)
            basePath += $"/{Collection}";
        if (EntityId != null)
            basePath += $"/{EntityId}";
        return basePath;
    }

    /// <summary>
    /// Parses a data path (the part after "data:").
    /// Format: addressType/addressId[/collection[/entityId]]
    /// </summary>
    public static DataContentReference ParseDataPath(string remainder)
    {
        if (string.IsNullOrEmpty(remainder))
            throw new ArgumentException("Invalid data path: path after 'data:' cannot be empty");

        var parts = remainder.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
            throw new ArgumentException($"Invalid data path: 'data:{remainder}'. Expected at least addressType/addressId");

        return parts.Length switch
        {
            2 => new DataContentReference(parts[0], parts[1]),
            3 => new DataContentReference(parts[0], parts[1], parts[2]),
            _ => new DataContentReference(parts[0], parts[1], parts[2], string.Join("/", parts.Skip(3)))
        };
    }

    /// <inheritdoc />
    public override string ToString() => ToPath();
}

/// <summary>
/// Reference to a layout area.
/// Supports formats:
/// - area:addressType/addressId/areaName
/// - area:addressType/addressId/areaName/areaId
/// </summary>
public record LayoutAreaContentReference(
    string AddressType,
    string AddressId,
    string AreaName,
    string? AreaId = null) : ContentReference
{
    /// <inheritdoc />
    public override string AddressType { get; } = AddressType;

    /// <inheritdoc />
    public override string AddressId { get; } = AddressId;

    /// <inheritdoc />
    public override string ToPath() =>
        AreaId != null
            ? $"area:{AddressType}/{AddressId}/{AreaName}/{AreaId}"
            : $"area:{AddressType}/{AddressId}/{AreaName}";

    /// <summary>
    /// Parses an area path (the part after "area:").
    /// Format: addressType/addressId/areaName[/areaId]
    /// </summary>
    public static LayoutAreaContentReference ParseAreaPath(string remainder)
    {
        if (string.IsNullOrEmpty(remainder))
            throw new ArgumentException("Invalid area path: path after 'area:' cannot be empty");

        var parts = remainder.Split('/', 4, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 3)
            throw new ArgumentException($"Invalid area path: 'area:{remainder}'. Expected format: area:addressType/addressId/areaName[/areaId]");

        return parts.Length switch
        {
            3 => new LayoutAreaContentReference(parts[0], parts[1], parts[2]),
            _ => new LayoutAreaContentReference(parts[0], parts[1], parts[2], parts[3])
        };
    }

    /// <inheritdoc />
    public override string ToString() => ToPath();
}
