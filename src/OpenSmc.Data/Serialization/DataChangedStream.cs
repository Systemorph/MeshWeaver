using System.Reactive.Linq;
using Json.Patch;
using Microsoft.Extensions.DependencyInjection;
using OpenSmc.Activities;

namespace OpenSmc.Data.Serialization;

public abstract class DataChangedStreamBase<TStream, TReference, TChange>
    where TReference : WorkspaceReference<TStream>
{
    protected ChangeItem<TStream> Current { get; set; }
    protected abstract IObservable<TChange> ChangeStream { get; }
    protected readonly IChangeStream<TStream, TReference> InStream;
    protected readonly IActivityService ActivityService;
    protected readonly Func<
        WorkspaceState,
        TReference,
        ChangeItem<TStream>,
        ChangeItem<WorkspaceState>
    > backfeed;

    protected DataChangedStreamBase(IChangeStream<TStream, TReference> stream)
    {
        ActivityService = stream.Hub.ServiceProvider.GetRequiredService<IActivityService>();
        Current = stream.Current;

        InStream = stream;
        backfeed = stream.Workspace.ReduceManager.ReduceTo<TStream>().GetBackfeed<TReference>();
    }

    protected TStream ApplyPatch(JsonPatch patch)
    {
        return patch.Apply(Current.Value, InStream.Hub.JsonSerializerOptions);
    }

    protected JsonPatch GetPatch(TStream fullChange)
    {
        var jsonPatch = Current.Value.CreatePatch(fullChange, InStream.Hub.JsonSerializerOptions);
        if (!jsonPatch.Operations.Any())
            return null;
        return jsonPatch;
    }
}
