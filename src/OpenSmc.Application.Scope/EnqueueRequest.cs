using OpenSmc.Messaging;
using OpenSmc.ShortGuid;

namespace OpenSmc.Application.Scope;

public record EnqueueRequest(Func<CancellationToken, Task> Action, string Id, int? Debounce) : IRequest<EvaluationFinished>
{
    public EnqueueRequest(Func<CancellationToken,Task> Action, string Id)
        : this(Action, Id, 0)
    {
    }
    public EnqueueRequest(Func<CancellationToken, Task> Action)
        : this(Action, Guid.NewGuid().AsString())
    {
    }
}

public record EvaluationFinished(string Id);