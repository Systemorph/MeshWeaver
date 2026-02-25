using System.ComponentModel;
using System.ComponentModel.DataAnnotations;

namespace MeshWeaver.Mesh;

public enum ApprovalStatus { Pending, Approved, Rejected }

/// <summary>
/// Represents an approval request on a mesh node.
/// Approvals are satellite content — permissions delegate to the primary document node.
/// </summary>
public record Approval : ISatelliteContent
{
    [Browsable(false)]
    [Key]
    public string Id { get; init; } = Guid.NewGuid().ToString();

    /// <summary>
    /// Path of the primary document node this approval belongs to (ISatelliteContent).
    /// </summary>
    [Browsable(false)]
    public string? PrimaryNodePath { get; init; }

    /// <summary>
    /// User ObjectId of the person requesting the approval.
    /// </summary>
    [Browsable(false)]
    public string Requester { get; init; } = string.Empty;

    /// <summary>
    /// User ObjectId of the person who should approve.
    /// </summary>
    [Browsable(false)]
    public string Approver { get; init; } = string.Empty;

    /// <summary>
    /// Purpose / reason for the approval request.
    /// </summary>
    public string Purpose { get; init; } = string.Empty;

    /// <summary>
    /// Optional due date for the approval.
    /// </summary>
    [Browsable(false)]
    public DateTimeOffset? DueDate { get; init; }

    /// <summary>
    /// When the approval was granted or rejected.
    /// </summary>
    [Browsable(false)]
    public DateTimeOffset? ApprovalDate { get; init; }

    /// <summary>
    /// When the approval request was created.
    /// </summary>
    [Browsable(false)]
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// Current status of the approval.
    /// </summary>
    [Browsable(false)]
    public ApprovalStatus Status { get; init; } = ApprovalStatus.Pending;
}
