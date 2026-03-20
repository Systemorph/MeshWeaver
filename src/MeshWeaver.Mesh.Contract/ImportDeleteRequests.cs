using MeshWeaver.Mesh.Security;
using MeshWeaver.Messaging;
using MeshWeaver.Messaging.Security;

namespace MeshWeaver.Mesh;

/// <summary>
/// Request to import nodes from a source path into the mesh.
/// </summary>
/// <param name="SourcePath">Server-side source directory path</param>
/// <param name="TargetPath">Target root path in the mesh</param>
[RequiresPermission(Permission.Create)]
public record ImportNodesRequest(string SourcePath, string TargetPath) : IRequest<ImportNodesResponse>
{
    /// <summary>
    /// If true, re-import even if data already exists.
    /// </summary>
    public bool Force { get; init; }
}

/// <summary>
/// Response for node import request.
/// </summary>
public record ImportNodesResponse
{
    /// <summary>Number of nodes imported.</summary>
    public int NodesImported { get; init; }
    /// <summary>Number of partitions imported.</summary>
    public int PartitionsImported { get; init; }
    /// <summary>Number of nodes skipped (already existed).</summary>
    public int NodesSkipped { get; init; }
    /// <summary>Number of partitions skipped.</summary>
    public int PartitionsSkipped { get; init; }
    /// <summary>Number of nodes removed during import.</summary>
    public int NodesRemoved { get; init; }
    /// <summary>Time taken for the import.</summary>
    public TimeSpan Elapsed { get; init; }
    /// <summary>Error message if the import failed; null on success.</summary>
    public string? Error { get; init; }
    /// <summary>True if the import succeeded.</summary>
    public bool Success => Error == null;

    /// <summary>Creates a successful response.</summary>
    public static ImportNodesResponse Ok(int nodesImported, int partitionsImported, int nodesSkipped, int partitionsSkipped, TimeSpan elapsed, int nodesRemoved = 0) => new()
    {
        NodesImported = nodesImported,
        PartitionsImported = partitionsImported,
        NodesSkipped = nodesSkipped,
        PartitionsSkipped = partitionsSkipped,
        NodesRemoved = nodesRemoved,
        Elapsed = elapsed
    };

    /// <summary>Creates a failed response.</summary>
    public static ImportNodesResponse Fail(string error) => new() { Error = error };
}

/// <summary>
/// Request to import content files into a content collection.
/// </summary>
/// <param name="CollectionName">Name of the content collection</param>
/// <param name="SourcePath">Server-side source directory path</param>
/// <param name="TargetPath">Target folder path within the collection</param>
[RequiresPermission(Permission.Create)]
public record ImportContentRequest(string CollectionName, string SourcePath, string TargetPath) : IRequest<ImportContentResponse>;

/// <summary>
/// Response for content import request.
/// </summary>
public record ImportContentResponse
{
    /// <summary>Number of files successfully imported.</summary>
    public int FilesImported { get; init; }
    /// <summary>Error message if the import failed; null on success.</summary>
    public string? Error { get; init; }
    /// <summary>True if the import succeeded.</summary>
    public bool Success => Error == null;

    /// <summary>Creates a successful response.</summary>
    public static ImportContentResponse Ok(int filesImported) => new() { FilesImported = filesImported };
    /// <summary>Creates a failed response.</summary>
    public static ImportContentResponse Fail(string error) => new() { Error = error };
}

/// <summary>
/// Request to delete content from a content collection folder.
/// </summary>
/// <param name="CollectionName">Name of the content collection</param>
/// <param name="FolderPath">Folder path to delete within the collection</param>
[RequiresPermission(Permission.Delete)]
public record DeleteContentRequest(string CollectionName, string FolderPath) : IRequest<DeleteContentResponse>;

/// <summary>
/// Response for content deletion request.
/// </summary>
public record DeleteContentResponse
{
    /// <summary>Number of items deleted.</summary>
    public int ItemsDeleted { get; init; }
    /// <summary>Error message if deletion failed; null on success.</summary>
    public string? Error { get; init; }
    /// <summary>True if the deletion succeeded.</summary>
    public bool Success => Error == null;

    /// <summary>Creates a successful response.</summary>
    public static DeleteContentResponse Ok(int itemsDeleted) => new() { ItemsDeleted = itemsDeleted };
    /// <summary>Creates a failed response.</summary>
    public static DeleteContentResponse Fail(string error) => new() { Error = error };
}
