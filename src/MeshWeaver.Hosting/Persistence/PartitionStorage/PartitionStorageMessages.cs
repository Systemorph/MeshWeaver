using System.Collections.Immutable;
using System.Text.Json;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;

namespace MeshWeaver.Hosting.Persistence.PartitionStorage;

/// <summary>
/// Write a batch of <see cref="MeshNode"/>s to the partition. The handler
/// validates, opens a transaction, writes every node, commits, then emits an
/// activity entry. Single message in / single response out — batching at the
/// caller level avoids one-message-per-row overhead.
/// </summary>
public record WriteBatchRequest(
    ImmutableList<MeshNode> Nodes,
    JsonSerializerOptions Options) : IRequest<WriteBatchResponse>;

/// <summary>Result of a <see cref="WriteBatchRequest"/>.</summary>
public record WriteBatchResponse(
    ImmutableList<MeshNode> WrittenNodes,
    string? Error = null);

/// <summary>
/// Delete a batch of nodes by path. The handler opens a transaction, deletes
/// every path, commits, then emits an activity entry.
/// </summary>
public record DeleteBatchRequest(
    ImmutableList<string> Paths) : IRequest<DeleteBatchResponse>;

/// <summary>Result of a <see cref="DeleteBatchRequest"/>.</summary>
public record DeleteBatchResponse(
    ImmutableList<string> DeletedPaths,
    string? Error = null);

/// <summary>Read a single node from the partition.</summary>
public record ReadNodeRequest(
    string Path,
    JsonSerializerOptions Options) : IRequest<ReadNodeResponse>;

/// <summary>Result of a <see cref="ReadNodeRequest"/>.</summary>
public record ReadNodeResponse(MeshNode? Node);

/// <summary>Existence check for a single node path.</summary>
public record ExistsRequest(string Path) : IRequest<ExistsResponse>;

/// <summary>Result of an <see cref="ExistsRequest"/>.</summary>
public record ExistsResponse(bool Exists);

/// <summary>
/// Lists child paths under a parent path inside this partition.
/// Returns node paths (records present at that level) and directory paths
/// (intermediate folders).
/// </summary>
public record ListChildPathsRequest(string? ParentPath)
    : IRequest<ListChildPathsResponse>;

/// <summary>Result of a <see cref="ListChildPathsRequest"/>.</summary>
public record ListChildPathsResponse(
    ImmutableList<string> NodePaths,
    ImmutableList<string> DirectoryPaths);
