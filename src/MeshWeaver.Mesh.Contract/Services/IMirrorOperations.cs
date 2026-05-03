using MeshWeaver.Messaging;

namespace MeshWeaver.Mesh.Services;

/// <summary>
/// Cross-instance mirror — push or pull a subtree of MeshNodes between two
/// running MeshWeaver portals over MCP-HTTP. <see cref="MirrorRequest"/> is a
/// regular hub <see cref="IRequest{TResponse}"/>: post it at the local mesh
/// hub, observe the response. The handler lives in <c>MeshWeaver.Hosting</c>
/// (registered via <c>AddMirrorHandler</c>) — no DI of orchestrator types,
/// no service-locator. Same shape as <c>ExecuteScriptRequest</c> /
/// <c>MoveNodeRequest</c> / etc.
///
/// <para>The instance handling the request initiates outbound HTTPS to the
/// remote portal. For local↔prod that's always the local portal — prod
/// can't reach localhost.</para>
/// </summary>
public sealed record MirrorRequest : IRequest<MirrorResult>
{
    /// <summary>Remote portal base URL, e.g. <c>https://memex.meshweaver.cloud</c>.</summary>
    public required string RemoteBaseUrl { get; init; }

    /// <summary>ApiToken issued on the REMOTE portal (<c>mw_…</c>).</summary>
    public required string RemoteToken { get; init; }

    /// <summary>Path on the source side whose subtree to mirror.</summary>
    public required string SourcePath { get; init; }

    /// <summary>Optional path on the target side; defaults to <see cref="SourcePath"/>.</summary>
    public string? TargetPath { get; init; }

    /// <summary><c>"Push"</c> (local→remote) or <c>"Pull"</c> (remote→local). Default <c>"Push"</c>.</summary>
    public string Direction { get; init; } = "Push";

    /// <summary>If true, delete target nodes that don't exist on the source. Destructive.</summary>
    public bool RemoveMissing { get; init; }

    /// <summary>If true, enumerate what would be touched without writing.</summary>
    public bool DryRun { get; init; }
}

/// <summary>Wire-friendly mirror summary — JSON-serialised by both the MCP tool and the UI.</summary>
public sealed record MirrorResult
{
    /// <summary><c>"Ok"</c> | <c>"DryRun"</c> | <c>"Error"</c>.</summary>
    public required string Status { get; init; }

    /// <summary><c>"Push"</c> | <c>"Pull"</c>.</summary>
    public required string Direction { get; init; }

    /// <summary>Path on the source side (echoes the request).</summary>
    public required string SourcePath { get; init; }

    /// <summary>Path on the target side (defaults to source).</summary>
    public required string TargetPath { get; init; }

    /// <summary>Number of nodes successfully imported by the recursive copy.</summary>
    public int NodesImported { get; init; }

    /// <summary>Number of nodes the importer skipped (read or write failed; details in logs).</summary>
    public int NodesSkipped { get; init; }

    /// <summary>Number of nodes deleted on the target (only when <see cref="MirrorRequest.RemoveMissing"/>).</summary>
    public int NodesRemoved { get; init; }

    /// <summary>Number of partition object groups copied.</summary>
    public int PartitionsImported { get; init; }

    /// <summary>Wall-clock duration in milliseconds.</summary>
    public long ElapsedMs { get; init; }

    /// <summary>(Dry-run only) Number of nodes that would be touched.</summary>
    public int NodesScanned { get; init; }

    /// <summary>(Dry-run only) Sorted, de-duped list of paths the recursive enumerator visited.</summary>
    public IReadOnlyList<string> Paths { get; init; } = Array.Empty<string>();

    /// <summary>(Status=="Error") human-readable error message.</summary>
    public string? Error { get; init; }
}
