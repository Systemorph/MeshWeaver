using System.Collections.Immutable;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace MeshWeaver.Mesh;

/// <summary>Status of an approval request.</summary>
public enum ApprovalStatus
{
    /// <summary>Awaiting decision.</summary>
    Pending,
    /// <summary>Approved by the approver.</summary>
    Approved,
    /// <summary>Rejected by the approver.</summary>
    Rejected
}

/// <summary>
/// Represents an approval request on a mesh node.
/// Approvals are satellite content — permissions delegate to the primary document node.
/// </summary>
public record Approval
{
    /// <summary>Unique identifier for the approval.</summary>
    [Browsable(false)]
    [Key]
    public string Id { get; init; } = Guid.NewGuid().ToString();

    /// <summary>
    /// Path of the primary document node this approval belongs to.
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

    /// <summary>
    /// The approval step-up receipts stamped onto this approval — <c>{ approver → receipt id }</c>,
    /// written by the platform's step-up endpoint as the approver
    /// (<see cref="MeshWeaver.Mesh.Security.StepUpPaths.StampProperty"/>, <c>Doc/Architecture/ApprovalStepUp</c>).
    /// With step-up declared on the instance a decision counts only once its decider's receipt is
    /// valid for THIS approval's path and <see cref="StepUpBinding"/>, and the approval's control plane
    /// consumes it (single use). Null until a step-up stamps it. Not one of the approval's terms:
    /// <see cref="StepUpBinding"/> never covers it.
    /// </summary>
    [Browsable(false)]
    public ImmutableDictionary<string, string>? StepUpReceipts { get; init; }

    /// <summary>
    /// What an approval step-up for deciding this approval binds — the hash of the approval's TERMS
    /// (id, document, requester, approver, purpose, due date) together with the DECISION being made
    /// (<see cref="Status"/>). The page that sends a decider to step up and the control plane that
    /// consumes the receipt both compute it from the same record, so a receipt minted for one
    /// approval, one set of terms or one decision never confirms another. Excludes
    /// <see cref="ApprovalDate"/>, <see cref="CreatedAt"/> and <see cref="StepUpReceipts"/>, which
    /// change without the approver deciding anything new. Each field is length-prefixed, so no two
    /// different term sets produce the same bytes. Pure.
    /// </summary>
    /// <returns><c>approval:sha256:{64 lowercase hex}</c>.</returns>
    public string StepUpBinding()
    {
        var sb = new StringBuilder();
        void Field(string? value)
        {
            if (value is null) { sb.Append('-'); return; }
            sb.Append(Encoding.UTF8.GetByteCount(value).ToString(CultureInfo.InvariantCulture)).Append(':').Append(value);
        }
        Field("MeshWeaver.Approval.StepUp.v1");
        Field(Id);
        Field(PrimaryNodePath);
        Field(Requester);
        Field(Approver);
        Field(Purpose);
        Field(DueDate?.UtcTicks.ToString(CultureInfo.InvariantCulture));
        Field(Status.ToString());
        return "approval:sha256:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()))).ToLowerInvariant();
    }

    /// <summary>
    /// Value equality over every field, the <see cref="StepUpReceipts"/> map compared by its ENTRIES:
    /// a record's generated equality compares an <c>ImmutableDictionary</c> by reference, so two reads
    /// of the same stored approval would otherwise never be equal once a receipt is stamped — and a
    /// control plane that writes only "while the node is still the revision it judged" would never write.
    /// </summary>
    /// <param name="other">The other approval.</param>
    /// <returns>True when every field is equal.</returns>
    public virtual bool Equals(Approval? other) =>
        other is not null
        && (ReferenceEquals(this, other)
            || (EqualityContract == other.EqualityContract
                && Id == other.Id
                && PrimaryNodePath == other.PrimaryNodePath
                && Requester == other.Requester
                && Approver == other.Approver
                && Purpose == other.Purpose
                && DueDate == other.DueDate
                && ApprovalDate == other.ApprovalDate
                && CreatedAt == other.CreatedAt
                && Status == other.Status
                && ReceiptsEqual(StepUpReceipts, other.StepUpReceipts)));

    /// <inheritdoc />
    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(EqualityContract);
        hash.Add(Id);
        hash.Add(PrimaryNodePath);
        hash.Add(Requester);
        hash.Add(Approver);
        hash.Add(Purpose);
        hash.Add(DueDate);
        hash.Add(ApprovalDate);
        hash.Add(CreatedAt);
        hash.Add(Status);
        hash.Add(StepUpReceipts?.Count ?? -1);
        return hash.ToHashCode();
    }

    private static bool ReceiptsEqual(ImmutableDictionary<string, string>? a, ImmutableDictionary<string, string>? b)
    {
        if (ReferenceEquals(a, b)) return true;
        if (a is null || b is null || a.Count != b.Count) return false;
        // Canonical ORDINAL entry sequences — never either map's own key comparer, which would make
        // equality asymmetric between an ordinal and a case-insensitive map.
        return a.OrderBy(e => e.Key, StringComparer.Ordinal)
            .SequenceEqual(b.OrderBy(e => e.Key, StringComparer.Ordinal), OrdinalEntryComparer.Instance);
    }

    private sealed class OrdinalEntryComparer : IEqualityComparer<KeyValuePair<string, string>>
    {
        public static readonly OrdinalEntryComparer Instance = new();

        public bool Equals(KeyValuePair<string, string> x, KeyValuePair<string, string> y) =>
            string.Equals(x.Key, y.Key, StringComparison.Ordinal) && string.Equals(x.Value, y.Value, StringComparison.Ordinal);

        public int GetHashCode(KeyValuePair<string, string> obj) =>
            HashCode.Combine(StringComparer.Ordinal.GetHashCode(obj.Key), StringComparer.Ordinal.GetHashCode(obj.Value));
    }
}
