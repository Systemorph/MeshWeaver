using System.Reactive.Linq;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;

namespace Memex.Portal.Shared.Authentication;

/// <summary>
/// The server-side half of a step-up in progress (<see cref="StepUpPending"/>, at
/// <c>Auth/_StepUpPending/{handle}</c>), always as System — the node type admits System alone.
/// Reactive end to end; the one Task bridge is the MVC action that calls it.
/// </summary>
/// <param name="hub">The mesh hub.</param>
internal sealed class StepUpPendingStore(IMessageHub hub)
{
    /// <summary>The bound on one write or read; one that does not answer fails the step-up (closed).</summary>
    internal static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private AccessService? Access => hub.ServiceProvider.GetService<AccessService>();

    private IMeshService Mesh => hub.ServiceProvider.GetRequiredService<IMeshService>();

    /// <summary>Stores a pending step-up. Cold; emits once it is stored.</summary>
    /// <param name="pending">The pending step-up.</param>
    /// <returns>The stored node.</returns>
    public IObservable<MeshNode> Begin(StepUpPending pending) =>
        Access.RunAsSystem(() => Mesh.CreateNode(new MeshNode(pending.Id, StepUpPaths.PendingNamespace)
            {
                Name = "Step-up in progress",
                NodeType = StepUpPaths.PendingNodeType,
                State = MeshNodeState.Active,
                Content = pending,
            }).Take(1))
            .Timeout(Timeout);

    /// <summary>
    /// Reads the pending step-up a handle names. Only ever called with a handle this host minted and
    /// sealed into the browser's cookie, so the node exists unless it expired and was removed. Cold,
    /// single emission; null when absent.
    /// </summary>
    /// <param name="handle">The handle.</param>
    /// <returns>The pending step-up, or null.</returns>
    public IObservable<StepUpPending?> Read(string handle) =>
        Access.RunAsSystem(() => hub.GetMeshNode(StepUpPaths.Pending(handle), Timeout).Take(1))
            .Select(n => n?.ContentAs<StepUpPending>(hub.JsonSerializerOptions))
            .DefaultIfEmpty(null);

    /// <summary>
    /// TAKES a pending step-up — at most one caller ever gets it. Reads it, lets
    /// <paramref name="admits"/> check it belongs to the request (user, state, expiry), then claims it
    /// through <see cref="StepUpSingleUse.ClaimPending"/>: of two concurrent requests carrying the
    /// same cookie, only the store's winner receives the record; the other gets null. The node is
    /// deleted afterwards as tidying — the claim, not the delete, is what makes it single use.
    /// Cold, single emission.
    /// </summary>
    /// <param name="handle">The handle.</param>
    /// <param name="admits">The request's own checks; a record it refuses is never claimed.</param>
    /// <returns>The pending step-up for the winner; null otherwise.</returns>
    public IObservable<StepUpPending?> Take(string handle, Func<StepUpPending, bool> admits) =>
        Read(handle).SelectMany(pending => pending is null || !admits(pending)
            ? Observable.Return<StepUpPending?>(null)
            : new StepUpSingleUse(hub).ClaimPending(handle, pending.UserId)
                .Select(won => won ? pending : null));

    /// <summary>Deletes a pending step-up once it produced its proof. Cold.</summary>
    /// <param name="handle">The handle.</param>
    /// <returns>Whether it was deleted.</returns>
    public IObservable<bool> Complete(string handle) =>
        Access.RunAsSystem(() => Mesh.DeleteNode(StepUpPaths.Pending(handle)));
}
