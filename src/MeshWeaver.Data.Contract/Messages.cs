using System.Collections.Immutable;
using MeshWeaver.Activities;
using MeshWeaver.Messaging;
using Microsoft.Extensions.Options;

namespace MeshWeaver.Data;

public record StreamMessage(string StreamId);

public record DataChangeRequest
    : IRequest<DataChangeResponse>
{
    public string ChangedBy { get; init; }
    public ImmutableList<object> Creations { get; init; } = [];

    public ImmutableList<object> Updates { get; init; } = [];
    public ImmutableList<object> Deletions { get; init; } = [];
    public UpdateOptions Options { get; init; }
    public string StreamId { get; init; }

    public DataChangeRequest WithCreations(params object[] creations)
        => this with { Creations = Creations.AddRange(creations) };

    public DataChangeRequest WithUpdates(params object[] updates)
    => this with { Updates = Updates.AddRange(updates) };
    public DataChangeRequest WithDeletions(params object[] deletions)
    => this with { Deletions = Deletions.AddRange(deletions) };

    public static DataChangeRequest Create(IReadOnlyCollection<object> creations, string changedBy) =>
        new() { Creations = creations.ToImmutableList(), ChangedBy = changedBy };
    public static DataChangeRequest Update(IReadOnlyCollection<object> updates, string changedBy = null, UpdateOptions options = null) =>
        new() { Updates = updates.ToImmutableList(), ChangedBy = changedBy, Options = options };
    public static DataChangeRequest Delete(IReadOnlyCollection<object> deletes, string changedBy) =>
        new() { Deletions = deletes.ToImmutableList(), ChangedBy = changedBy};

};

public record DataChangeResponse(long Version, ActivityLog Log)
{
    public DataChangeStatus Status { get; init; } =
        Log.Status switch
        {
            ActivityStatus.Succeeded => DataChangeStatus.Committed,
            _ => DataChangeStatus.Failed
        };
}

public enum DataChangeStatus
{
    Committed,
    Failed
}

public enum ChangeType
{
    Full,
    Patch,
    Instance,
    NoUpdate
}

public record DataChangedEvent(
    string StreamId,
    long Version,
    RawJson Change,
    ChangeType ChangeType,
    string ChangedBy
);

public record SubscribeRequest(string StreamId, WorkspaceReference Reference) : IRequest<DataChangedEvent>
{
    public Address Subscriber { get; init; }
}

/// <summary>
/// Ids of the synchronization requests to be stopped (generated with request)
/// </summary>
public record UnsubscribeRequest(string StreamId);
