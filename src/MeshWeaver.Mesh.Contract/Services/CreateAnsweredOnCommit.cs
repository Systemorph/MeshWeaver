using System.Reactive.Disposables;
using System.Reactive.Linq;
using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.Mesh.Services;

/// <summary>
/// A create whose caller is answered by the store's COMMIT, not by the create's reply (#6039).
///
/// <para><see cref="IMeshService.CreateNode"/> answers with the <c>CreateNodeResponse</c>, and
/// that reply is posted only after the post-creation handlers have finished. It then travels
/// back through the node-operation hub's own action block (<c>portal/nodeops-{meshId}</c>). A
/// responder enqueues its outgoing reply on its own block before routing it. So a caller that
/// only needs to know the row is DURABLE still waits for every handler and for whatever that block
/// is busy with. For an HTTP endpoint that answers an external sender, such as GitHub with its
/// 10-second limit, that wait reads as no response at all, although the row was committed in
/// milliseconds.</para>
///
/// <para>This primitive answers on whichever comes first. The first signal is the commit itself:
/// <see cref="IMeshInvalidationFeed"/> announces a <see cref="MeshChangeKind.Created"/> for the
/// node's path, and <c>StorageAdapterChangeFeedExtensions.WriteAndPublishCreated</c> publishes
/// that only after the storage write emitted. The second signal is the ordinary reply. A failure
/// BEFORE the commit still reaches the caller as an error, because no commit was announced and the
/// reply carries the refusal. A failure AFTER the commit, such as a post-creation handler that
/// faults, cannot change an answer that has already been given. The owning hub logs it where it
/// happens (<c>RunPostCreationHandlersObs</c>), so the caller does not wait for it.</para>
///
/// <para>Once the commit has answered, the caller's interest in the reply ENDS and the reply
/// subscription is disposed at once. The create was already posted and runs on at its owner, but
/// no callback stays registered on the issuing hub for a reply that may never be routed back.
/// Keeping one per call would turn the very failure this primitive tolerates, a reply that never
/// arrives, into an unbounded accumulation of pending callbacks.</para>
///
/// <para>🚨 <b>Use it only for a node type whose create contract ends at the row.</b> A type with
/// a post-creation handler that declares <see cref="INodePostCreationHandler.FailsCreateOnError"/>
/// (a Space and its creator grant) has a contract that is NOT fulfilled at commit: that create can
/// still roll itself back. An answer given on commit would then report a success the mesh later
/// retracts. The webhook inbox's <c>WebhookEvent</c> is the intended caller. It is a verbatim record
/// that consumers pick up from their own inbox query, and no handler is part of its contract.</para>
///
/// <para>Without an <see cref="IMeshInvalidationFeed"/> registered, the call is exactly
/// <see cref="IMeshService.CreateNode"/>, answered with the node's path.</para>
/// </summary>
public static class CreateAnsweredOnCommit
{
    /// <summary>
    /// Creates <paramref name="node"/> and emits its path once, as soon as the store has committed
    /// the row or the create's reply arrived, whichever is first. Cold: nothing is posted until
    /// Subscribe.
    /// </summary>
    /// <param name="mesh">The mesh service that issues the create.</param>
    /// <param name="services">The service provider that holds the mesh's invalidation feed.</param>
    /// <param name="node">The node to create. Its path is the commit signal's key, so it must be
    /// fresh. A path that already has a row would match that row's commit.</param>
    /// <returns>The committed node's path. It is the same value whichever signal answered: the feed
    /// carries the path and not the stamped node, so a caller that needs the stored node reads it
    /// from <c>GetMeshNodeStream(path)</c>.</returns>
    public static IObservable<string> CreateNodeAnsweredOnCommit(
        this IMeshService mesh, IServiceProvider services, MeshNode node)
    {
        // Built EAGERLY, as the caller would have built it: CreateNode captures the caller's
        // identity at the call, so the RunAsSystem seal around this call is what the create carries.
        var create = mesh.CreateNode(node);
        var feed = services.GetService<IMeshInvalidationFeed>();
        if (feed is null)
            return create.Take(1).Select(_ => node.Path);
        return create.AnswerOnCommit(feed, node.Path);
    }

    /// <summary>
    /// Answers a create on whichever comes first: <paramref name="feed"/> announcing a
    /// <see cref="MeshChangeKind.Created"/> for <paramref name="path"/>, or <paramref name="create"/>
    /// emitting. Emits <paramref name="path"/> once. A fault of <paramref name="create"/> before the
    /// commit is the caller's error. Once the commit has answered, the subscription to
    /// <paramref name="create"/> is disposed, so no reply callback outlives the answer.
    /// </summary>
    /// <param name="create">The cold create whose reply is the second signal.</param>
    /// <param name="feed">The process's invalidation feed.</param>
    /// <param name="path">The path of the node being created.</param>
    /// <returns>The path, once.</returns>
    public static IObservable<string> AnswerOnCommit(
        this IObservable<MeshNode> create, IMeshInvalidationFeed feed, string path)
        => Observable.Create<string>(observer =>
        {
            var answered = 0;
            bool Claim() => Interlocked.Exchange(ref answered, 1) == 0;
            // The commit can fire before the reply subscription is assigned (the create posts during
            // Subscribe). A SingleAssignmentDisposable disposed first disposes what is assigned later.
            var reply = new SingleAssignmentDisposable();

            // Subscribed BEFORE the create is posted, so a commit cannot slip past unseen.
            var commit = feed.Subscribe(change =>
            {
                if (!string.Equals(change.Path, path, StringComparison.OrdinalIgnoreCase)
                    || !Claim())
                    return;
                // The answer is given, so the reply is no longer owed to anyone here.
                reply.Dispose();
                observer.OnNext(path);
                observer.OnCompleted();
            }, MeshChangeKind.Created);

            reply.Disposable = create.Take(1).Subscribe(
                _ =>
                {
                    if (!Claim())
                        return;
                    observer.OnNext(path);
                    observer.OnCompleted();
                },
                ex =>
                {
                    if (Claim())
                        observer.OnError(ex);
                });

            // Both ended together: by the caller's dispose, and by Observable.Create on the answer.
            return new CompositeDisposable(commit, reply);
        });
}
