using MeshWeaver.Mesh;

namespace MeshWeaver.Graph;

/// <summary>
/// A pluggable continuation for an <see cref="EventSubscription"/> whose effect lives in a higher layer
/// than <c>MeshWeaver.Graph</c> — e.g. the delegation backstop (<see cref="EventContinuationType.PostThreadMessage"/>)
/// posts a sub-thread's result back into the parent thread, which needs <c>MeshWeaver.AI</c>'s thread
/// submission surface that Graph cannot reference. Register an implementation in DI; the
/// <c>EventSubscriptionRunner</c> resolves the one whose <see cref="Handles"/> matches a fired
/// subscription's <see cref="EventSubscription.ContinuationType"/> and runs it. The runner wraps the
/// returned observable in a system-impersonation scope, so <see cref="Run"/>'s construction and the
/// initial subscribe are under the system identity. Because <c>AccessContext</c> is AsyncLocal, if your
/// handler defers work onto a NEW async/reactive boundary it created itself, re-establish the identity
/// there (the framework's write primitives already carry it through <c>.Subscribe</c>). The
/// natively-handled <see cref="EventContinuationType.GrantSpaceAccess"/> continuation does NOT go through
/// this seam.
/// </summary>
public interface IEventSubscriptionContinuationHandler
{
    /// <summary>The continuation type this handler runs.</summary>
    EventContinuationType Handles { get; }

    /// <summary>Runs the continuation for a fired <paramref name="subscription"/> (cold — the caller
    /// subscribes). Everything the effect needs is on the subscription (e.g. <see cref="EventSubscription.WatchPath"/>
    /// = the source, <see cref="EventSubscription.TargetPath"/> = the destination).</summary>
    IObservable<MeshNode> Run(EventSubscription subscription);
}
