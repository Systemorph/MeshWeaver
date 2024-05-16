using System.Reactive.Linq;
using Json.Patch;
using Microsoft.Extensions.DependencyInjection;
using OpenSmc.Activities;

namespace OpenSmc.Data.Serialization;

public abstract class DataChangedStreamBase<TStream, TReference, TChange>
    where TReference : WorkspaceReference<TStream>
{
    protected readonly IChangeStream<TStream, TReference> Stream;
    protected readonly IActivityService ActivityService;

    protected DataChangedStreamBase(IChangeStream<TStream, TReference> stream)
    {
        ActivityService = stream.Hub.ServiceProvider.GetRequiredService<IActivityService>();

        Stream = stream;
    }

    protected TStream ApplyPatch(TStream current, JsonPatch patch)
    {
        return patch.Apply(current, Stream.Hub.JsonSerializerOptions);
    }

    protected JsonPatch GetPatch(ref TStream current, TStream fullChange)
    {
        var jsonPatch = current.CreatePatch(fullChange, Stream.Hub.JsonSerializerOptions);
        if (!jsonPatch.Operations.Any())
            return null;
        current = fullChange;
        return jsonPatch;
    }
}
