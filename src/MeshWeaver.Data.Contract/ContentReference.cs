namespace MeshWeaver.Data;

/// <summary>
/// Base class for all content reference types that can be parsed from path patterns.
/// Supports multiple path formats:
/// - Content: content:collection/path/to/file or content:collection@partition/path/to/file
/// - Data: data:addressType/addressId[/collection[/entityId]]
/// - Layout Area: area:areaName[/areaId]
/// </summary>
public abstract record ContentReference
{
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
/// - content:collection/path/to/file
/// - content:collection@partition/path/to/file
/// </summary>
public record FileContentReference(string Collection, string FilePath, string? Partition = null)
    : ContentReference
{
    /// <inheritdoc />
    public override string ToPath() =>
        Partition != null
            ? $"content:{Collection}@{Partition}/{FilePath}"
            : $"content:{Collection}/{FilePath}";

    /// <summary>
    /// Parses a content path (the part after "content:").
    /// Format: collection/path/to/file or collection@partition/path/to/file
    /// </summary>
    public static FileContentReference ParseContentPath(string remainder)
    {
        if (string.IsNullOrEmpty(remainder))
            throw new ArgumentException("Invalid content path: path after 'content:' cannot be empty");

        var slashIndex = remainder.IndexOf('/');
        if (slashIndex <= 0)
            throw new ArgumentException($"Invalid content path: 'content:{remainder}'. Expected format: content:collection/path/to/file");

        var collectionPart = remainder[..slashIndex];
        var filePath = remainder[(slashIndex + 1)..];

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

            return new FileContentReference(collection, filePath, partition);
        }

        return new FileContentReference(collectionPart, filePath);
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
/// - area:areaName
/// - area:areaName/areaId
/// </summary>
public record LayoutAreaContentReference(string AreaName, string? AreaId = null)
    : ContentReference
{
    /// <inheritdoc />
    public override string ToPath() =>
        AreaId != null ? $"area:{AreaName}/{AreaId}" : $"area:{AreaName}";

    /// <summary>
    /// Parses an area path (the part after "area:").
    /// Format: areaName[/areaId]
    /// </summary>
    public static LayoutAreaContentReference ParseAreaPath(string remainder)
    {
        if (string.IsNullOrEmpty(remainder))
            throw new ArgumentException("Invalid area path: area name cannot be empty");

        var parts = remainder.Split('/', 2, StringSplitOptions.RemoveEmptyEntries);
        return parts.Length switch
        {
            0 => throw new ArgumentException("Invalid area path: area name is required"),
            1 => new LayoutAreaContentReference(parts[0]),
            _ => new LayoutAreaContentReference(parts[0], parts[1])
        };
    }

    /// <inheritdoc />
    public override string ToString() => ToPath();
}
