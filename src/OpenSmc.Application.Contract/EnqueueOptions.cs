using System;

namespace OpenSmc.Application;

public record EnqueueOptions
{
    internal string QueueType { get; init; } = QueueName.Initialize;

    public EnqueueOptions WithQueue(string queueType)
    {
        return this with { QueueType = queueType };
    }

    internal bool KeepUpToDate { get; init; }

    // TODO V10: rename: watch.. register.. monitor... subscribe to update/changes... recurrent... reevaluate... (2023/04/06, Ekaterina Mishina)
    public EnqueueOptions RescheduleOnUpdate(bool keepUpToDate = true)
    {
        return this with { KeepUpToDate = keepUpToDate };
    }

    internal string Id { get; init; } = Guid.NewGuid().AsString();
    public EnqueueOptions WithId(string id) => this with { Id = id };
}
