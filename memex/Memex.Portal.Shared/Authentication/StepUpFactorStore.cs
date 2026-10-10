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
/// <para><b>Creating the FIRST factor and adding a further one are different writes, and never
/// fall back into each other.</b> Which one is allowed is an authorization decision (the first needs
/// a fresh sign-in, every further one a step-up), so <see cref="Create"/> only ever creates — a node
/// that already exists is a REFUSAL, never a silent update — and reads the stored node back so the
/// caller can see whether its own write is the one that landed (a concurrent create response cannot
/// say). <see cref="Update"/> folds onto a node known to exist, through
/// <c>GetMeshNodeStream(path).Update(fold)</c>.</para>
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

    /// <summary>
    /// Creates the user's factors node with <paramref name="first"/> — the first factor's write. A node
    /// that already exists is refused (null), never turned into an update: the caller was authorized
    /// for a FIRST factor only, on a listing that may have been stale. Cold.
    /// </summary>
    /// <param name="userId">The user.</param>
    /// <param name="first">The factors to store.</param>
    /// <returns>The STORED factors read back after the create, or null when the node already existed.</returns>
    public IObservable<StepUpFactors?> Create(string userId, StepUpFactors first) =>
        Observable.Defer(() =>
        {
            var node = new MeshNode(StepUpPaths.FactorsId, StepUpPaths.FactorsNamespaceOf(userId))
            {
                Name = "Step-up factors",
                NodeType = StepUpPaths.FactorsNodeType,
                State = MeshNodeState.Active,
                Content = first with { UserId = userId },
            };
            var mesh = hub.ServiceProvider.GetRequiredService<IMeshService>();
            return Access.RunAsSystem(() => mesh.CreateNode(node).Take(1)
                    .Select(_ => true)
                    .Catch((Exception ex) => StepUpSingleUse.IsAlreadyExists(ex) ? Observable.Return(false) : Observable.Throw<bool>(ex))
                    .SelectMany(created => !created
                        ? Observable.Return<StepUpFactors?>(null)
                        : hub.GetMeshNode(StepUpPaths.Factors(userId), ReadTimeout).Take(1)
                            .Select(stored => stored?.ContentAs<StepUpFactors>(hub.JsonSerializerOptions))))
                .Timeout(ReadTimeout);
        });

    /// <summary>
    /// Applies <paramref name="fold"/> to the user's EXISTING factors node, through the node stream —
    /// the owning hub serialises it. Only for a node a listing has named; never a create.
    /// </summary>
    /// <param name="userId">The user.</param>
    /// <param name="fold">The change, applied to the current value.</param>
    /// <returns>Cold; emits once the write is acknowledged.</returns>
    public IObservable<MeshNode> Update(string userId, Func<StepUpFactors, StepUpFactors> fold) =>
        Observable.Defer(() => Access.RunAsSystem(() => hub.GetWorkspace().GetMeshNodeStream(StepUpPaths.Factors(userId))
            // Typed: null means ABSENT only; present-but-unreadable content faults instead of being
            // folded over as an empty record.
            .Update<StepUpFactors>((node, content) => node with { Content = fold(content ?? new StepUpFactors { UserId = userId }) })
            .Take(1)));
}
