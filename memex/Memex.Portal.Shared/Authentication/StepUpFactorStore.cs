using System.Reactive.Linq;
using MeshWeaver.Data;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;

namespace Memex.Portal.Shared.Authentication;

/// <summary>
/// Reads and writes a user's <see cref="StepUpFactors"/> node (<c>Auth/_StepUpFactors/{user}/factors</c>),
/// always as System — the node type admits System alone.
///
/// <para><b>Both halves, never a guessed point read.</b> Whether the node EXISTS is learned from a
/// <c>scope:children</c> listing of the user's factor namespace; only a node the listing names is
/// then read by path. A point read of an absent path is a routing not-found that also opens the
/// storm-breaker on that path — which would then fast-fail the very enrolment write that creates it.
/// The listing trails the store, so a factor enrolled a moment ago can read as absent once; the
/// ladder then offers enrolment again, never a pass.</para>
///
/// <para><b>Writes are folds</b> (a passkey appended, a counter moved, a TOTP step consumed), so
/// they are CREATE-FIRST: the create carries the transform applied to an empty record, and only a
/// create refused as "already exists" falls back to <c>GetMeshNodeStream(path).Update(fold)</c>,
/// which the owning hub serialises.</para>
/// </summary>
/// <param name="hub">The mesh hub.</param>
internal sealed class StepUpFactorStore(IMessageHub hub)
{
    /// <summary>The bound on one listing or read; a read that does not answer is a fault, never "no factors".</summary>
    internal static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(10);

    private AccessService? Access => hub.ServiceProvider.GetService<AccessService>();

    /// <summary>The user's factors, or null when none are stored. Faults when the read does not answer.</summary>
    /// <param name="userId">The user.</param>
    /// <returns>Cold, single emission.</returns>
    public IObservable<StepUpFactors?> Load(string userId) =>
        Access.RunAsSystem(() => hub.ServiceProvider.GetRequiredService<IMeshService>()
                .Query<MeshNode>(MeshQueryRequest.FromQuery($"namespace:\"{StepUpPaths.FactorsNamespaceOf(userId)}\" scope:children"))
                .Where(c => c.ChangeType == QueryChangeType.Initial)
                .Take(1)
                .Select(c => c.Items.Any(n => n.Id == StepUpPaths.FactorsId)))
            .Timeout(ReadTimeout)
            .SelectMany(exists => exists
                ? Access.RunAsSystem(() => hub.GetMeshNode(StepUpPaths.Factors(userId), ReadTimeout).Take(1))
                    .Select(node => node?.ContentAs<StepUpFactors>(hub.JsonSerializerOptions))
                : Observable.Return<StepUpFactors?>(null));

    /// <summary>Applies <paramref name="fold"/> to the user's factors — creating the node on first use.</summary>
    /// <param name="userId">The user.</param>
    /// <param name="fold">The change, applied to the CURRENT value by the owning hub.</param>
    /// <returns>Cold; emits once the write is acknowledged.</returns>
    public IObservable<MeshNode> Update(string userId, Func<StepUpFactors, StepUpFactors> fold) =>
        Observable.Defer(() =>
        {
            var empty = new StepUpFactors { UserId = userId };
            var node = new MeshNode(StepUpPaths.FactorsId, StepUpPaths.FactorsNamespaceOf(userId))
            {
                Name = "Step-up factors",
                NodeType = StepUpPaths.FactorsNodeType,
                State = MeshNodeState.Active,
                Content = fold(empty),
            };
            var mesh = hub.ServiceProvider.GetRequiredService<IMeshService>();
            return Access.RunAsSystem(() => mesh.CreateNode(node).Take(1)
                .Catch((Exception ex) => !IsAlreadyExists(ex)
                    ? Observable.Throw<MeshNode>(ex)
                    : hub.GetWorkspace().GetMeshNodeStream(StepUpPaths.Factors(userId))
                        .Update(current => current with
                        {
                            Content = fold(current.ContentAs<StepUpFactors>(hub.JsonSerializerOptions) ?? empty),
                        })
                        .Take(1)));
        });

    private static bool IsAlreadyExists(Exception ex)
    {
        for (var e = ex; e is not null; e = e.InnerException)
        {
            if (e.Data[NodeCreationFailure.RejectionReasonKey] is NodeCreationRejectionReason.NodeAlreadyExists)
                return true;
            if (e.Message?.StartsWith("Node already exists", StringComparison.Ordinal) == true)
                return true;
        }
        return false;
    }
}
