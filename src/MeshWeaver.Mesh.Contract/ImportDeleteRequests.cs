using MeshWeaver.Messaging;

namespace MeshWeaver.Mesh;

/// <summary>
/// Request to import nodes from a source path into the mesh.
/// </summary>
/// <param name="SourcePath">Server-side source directory path</param>
/// <param name="TargetPath">Target root path in the mesh</param>
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
    public int NodesImported { get; init; }
    public int PartitionsImported { get; init; }
    public int NodesSkipped { get; init; }
    public int PartitionsSkipped { get; init; }
    public int NodesRemoved { get; init; }
    public TimeSpan Elapsed { get; init; }
    public string? Error { get; init; }
    public bool Success => Error == null;

    public static ImportNodesResponse Ok(int nodesImported, int partitionsImported, int nodesSkipped, int partitionsSkipped, TimeSpan elapsed, int nodesRemoved = 0) => new()
    {
        NodesImported = nodesImported,
        PartitionsImported = partitionsImported,
        NodesSkipped = nodesSkipped,
        PartitionsSkipped = partitionsSkipped,
        NodesRemoved = nodesRemoved,
        Elapsed = elapsed
    };

    public static ImportNodesResponse Fail(string error) => new() { Error = error };
}

/// <summary>
/// Request to import content files into a content collection.
/// </summary>
/// <param name="CollectionName">Name of the content collection</param>
/// <param name="SourcePath">Server-side source directory path</param>
/// <param name="TargetPath">Target folder path within the collection</param>
public record ImportContentRequest(string CollectionName, string SourcePath, string TargetPath) : IRequest<ImportContentResponse>;

/// <summary>
/// Response for content import request.
/// </summary>
public record ImportContentResponse
{
    public int FilesImported { get; init; }
    public string? Error { get; init; }
    public bool Success => Error == null;

    public static ImportContentResponse Ok(int filesImported) => new() { FilesImported = filesImported };
    public static ImportContentResponse Fail(string error) => new() { Error = error };
}

/// <summary>
/// Request to delete content from a content collection folder.
/// </summary>
/// <param name="CollectionName">Name of the content collection</param>
/// <param name="FolderPath">Folder path to delete within the collection</param>
public record DeleteContentRequest(string CollectionName, string FolderPath) : IRequest<DeleteContentResponse>;

/// <summary>
/// Response for content deletion request.
/// </summary>
public record DeleteContentResponse
{
    public int ItemsDeleted { get; init; }
    public string? Error { get; init; }
    public bool Success => Error == null;

    public static DeleteContentResponse Ok(int itemsDeleted) => new() { ItemsDeleted = itemsDeleted };
    public static DeleteContentResponse Fail(string error) => new() { Error = error };
}
