using System.Reactive.Disposables;
using System.Reactive.Linq;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

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
/// faults or a reply that never comes, cannot change an answer that has already been given. It is
/// logged at Warning, never dropped, and the create's own chain runs on untouched.</para>
///
/// <para>🚨 <b>Use it only for a node type whose create contract ends at the row.</b> A type with
/// a post-creation handler that declares <see cref="INodePostCreationHandler.FailsCreateOnError"/>
/// (a Space and its creator grant) has a contract that is NOT fulfilled at commit: that create can
/// still roll itself back. An answer given on commit would then report a success the mesh later
/// retracts. The webhook inbox's <c>WebhookEvent</c> is the intended caller. It is a verbatim record
/// that consumers pick up from their own inbox query, and no handler is part of its contract.</para>
///
/// <para>Without an <see cref="IMeshInvalidationFeed"/> registered, the call is exactly
/// <see cref="IMeshService.CreateNode"/>.</para>
/// </summary>
public static class CreateAnsweredOnCommit
{
    /// <summary>
    /// Creates <paramref name="node"/> and emits once, as soon as the store has committed the row or
    /// the create's reply arrived, whichever is first. Cold: nothing is posted until Subscribe.
    /// </summary>
    /// <param name="mesh">The mesh service that issues the create.</param>
    /// <param name="services">The service provider that holds the mesh's invalidation feed.</param>
    /// <param name="node">The node to create. Its path is the commit signal's key, so it must be
    /// fresh. A path that already has a row would match that row's commit.</param>
    /// <returns>The created node. On the commit signal this is <paramref name="node"/> as submitted,
    /// because the feed carries the path and not the stamped node. On the reply it is the stored
    /// node.</returns>
    public static IObservable<MeshNode> CreateNodeAnsweredOnCommit(
        this IMeshService mesh, IServiceProvider services, MeshNode node)
    {
        // Built EAGERLY, as the caller would have built it: CreateNode captures the caller's
        // identity at the call, so the RunAsSystem seal around this call is what the create carries.
        var create = mesh.CreateNode(node);
        var feed = services.GetService<IMeshInvalidationFeed>();
        if (feed is null)
            return create.Take(1);
        var logger = services.GetService<ILoggerFactory>()?.CreateLogger(typeof(CreateAnsweredOnCommit));

        return Observable.Create<MeshNode>(observer =>
        {
            var answered = 0;
            bool Claim() => Interlocked.Exchange(ref answered, 1) == 0;

            // Subscribed BEFORE the create is posted, so a commit cannot slip past unseen.
            var commit = feed.Subscribe(change =>
            {
                if (!string.Equals(change.Path, node.Path, StringComparison.OrdinalIgnoreCase)
                    || !Claim())
                    return;
                observer.OnNext(node);
                observer.OnCompleted();
            }, MeshChangeKind.Created);

            // 🚨 NOT disposed with the caller's subscription. The reply is still owed after an answer
            // on commit, and a late failure must be SEEN: it is logged, never left with nowhere to go.
            create.Take(1).Subscribe(
                stored =>
                {
                    if (!Claim())
                        return;
                    observer.OnNext(stored);
                    observer.OnCompleted();
                },
                ex =>
                {
                    if (Claim())
                    {
                        observer.OnError(ex);
                        return;
                    }
                    logger?.LogWarning(ex,
                        "Create of {Path} was answered on its commit; its reply later reported a failure",
                        node.Path);
                });

            return Disposable.Create(commit.Dispose);
        });
    }
}
