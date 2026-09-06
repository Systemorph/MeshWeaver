using System.Reactive.Linq;
using System.Reactive.Subjects;

namespace MeshWeaver.Mesh.Services;

/// <summary>
/// 🚨 <b>"This NodeType reached a usable build in THIS process" — the PROCESS-LOCAL fact, published
/// even when the shared stamp that would have recorded it is withheld.</b> Issue #3478.
///
/// <para><b>Why it has to exist.</b> The bake gate's regression RETRACTION (#1214) asks exactly one
/// question: <i>has this type, which the sweep condemned, since built on THIS image?</i> Until now
/// it answered that by watching the type's shared MeshNode record, because that is where the
/// compile watcher publishes — the record was a PROXY for a local fact. That proxy breaks the
/// moment a refused process stops publishing: the pod recompiles the type successfully, the stamp
/// is withheld (correctly — it names an identity the serving replicas cannot load), and the watch
/// waits forever for a record that will never move. #1214's incident would then be permanent
/// instead of self-healing: a regression formed against a half-applied content update would hold
/// the rollout until a human noticed, which is the failure that fix exists to remove.</para>
///
/// <para>So the retraction now observes the fact DIRECTLY. It is strictly better evidence than the
/// record: no round-trip, no framework comparison, no freshness heuristic — the compile happened
/// here, on this image, just now. The record-based wait stays alongside it, because a type baked by
/// a PEER on the same image is a legitimate recovery this signal cannot see.</para>
///
/// <para><b>Mesh-scoped instance singleton</b> (registered by <c>AddMeshCatalog</c>), never static:
/// two test meshes must not hear each other's compiles. Hot and non-replaying by design — a
/// consumer subscribes before the compile it is waiting for, exactly as the record watch does.</para>
/// </summary>
public sealed class LocalNodeTypeBuilds : IDisposable
{
    private readonly Subject<string> built = new();
    private volatile bool disposed;

    /// <summary>
    /// Emits the path of every NodeType that reached a usable build IN THIS PROCESS, whether or not
    /// the shared record was allowed to record it.
    /// </summary>
    public IObservable<string> Built => built.AsObservable();

    /// <summary>
    /// Records one such build. Called from the compile write-back BEFORE the (possibly withheld)
    /// stamp, so the signal survives a refusal — that is the entire point.
    /// </summary>
    /// <param name="typePath">The NodeType's mesh path.</param>
    public void RecordUsableBuild(string typePath)
    {
        if (disposed || string.IsNullOrEmpty(typePath))
            return;
        built.OnNext(typePath);
    }

    /// <summary>Mesh teardown — later observations are dropped rather than thrown.</summary>
    public void Dispose()
    {
        if (disposed)
            return;
        disposed = true;
        built.OnCompleted();
        built.Dispose();
    }
}
