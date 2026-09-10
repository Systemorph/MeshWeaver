using System.Collections.Immutable;
using System.Reactive.Linq;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Graph;

/// <summary>
/// Helper for deep-copying a mesh node tree (source node + all descendants) to a target namespace.
///
/// <para>🚨 <b>A copy asserts a set equality, so it has to establish one.</b> This helper used to
/// return a COUNT of writes that succeeded, and nothing anywhere established either half of the
/// equality it was reporting on: that the set it ENUMERATED is the source subtree, or that the set
/// it WROTE is the set it enumerated. Both shortfalls therefore read as success. The consequence is
/// recorded in <c>Doc/Architecture/MissingDeclaredSources</c> — a NodeType that reached a user
/// partition without its <c>Source/</c> subtree and then failed its compile on every pod boot for
/// four days, reporting three missing symbols that live in nodes nobody has. The design is
/// <c>Doc/Architecture/CopyCompleteness</c>.</para>
/// </summary>
public static class NodeCopyHelper
{
    /// <summary>Default per-level concurrency for the parallel
    /// <see cref="CreateOrUpdateNodeRequest"/> fan-out below. Bounded so a
    /// large subtree doesn't open every per-node hub at once on the
    /// receiving side.</summary>
    public const int DefaultBatchSize = 16;

    /// <summary>
    /// Opening words of the refusal a copy answers with when the caller cannot read the whole
    /// source subtree. Spelled once so the producer and every reader cannot drift — the same
    /// reasoning as <see cref="CopyNodeRequest.IncompleteCopyRefusal"/>, which is the sibling
    /// marker on the <c>CopyNodeRequest</c> path.
    /// </summary>
    public const string IncompleteSourceRefusal =
        "Refused: this caller cannot read the whole subtree";

    /// <summary>
    /// Opening words of the refusal a copy answers with when it could not establish whether its
    /// enumeration was complete. Distinct from <see cref="IncompleteSourceRefusal"/> on purpose:
    /// "I checked and it is short" and "I could not check" must never share a shape.
    /// </summary>
    public const string UndeterminedCompletenessRefusal =
        "Refused: the copy cannot establish what the source subtree holds";

    /// <summary>
    /// Opening words of the report a copy answers with when the writes ran and the target set is
    /// short of the enumerated set.
    /// </summary>
    public const string IncompleteCopyReport = "Incomplete copy of";

    /// <summary>
    /// Copies a node and all its descendants to a target namespace, and emits the number of nodes
    /// WRITTEN — or errors, naming exactly what did not land. The source node's Id is preserved;
    /// paths are rewritten under the target namespace.
    ///
    /// <para>This is the COUNT surface, derived from <see cref="CopyNodeTreeOutcome"/> so the two
    /// cannot drift. It exists because a caller that only wants "how many" should not have to know
    /// how a copy can fall short — but it can no longer emit a number for a copy that did:
    /// anything other than <see cref="NodeCopyStatus.Copied"/> is an <c>OnError</c> carrying
    /// <see cref="NodeCopyOutcome.Describe"/>. Use <see cref="CopyNodeTreeOutcome"/> where the
    /// outcome is rendered as a verdict.</para>
    /// </summary>
    /// <param name="meshQuery">Reads the source subtree.</param>
    /// <param name="nodeFactory">Unused: the per-node writes route through <paramref name="hub"/> to
    /// its node-operation target. Kept because <c>Templates/NodeCopy.csx</c> — a mesh-hosted script
    /// compiled at RUNTIME, which <c>dotnet build</c> cannot see — binds this overload
    /// positionally.</param>
    /// <param name="hub">Supplies the caller identity, the reply address and the node-operation
    /// target. May be ANY hub — an MCP/REST session hub, a per-node UI hub, the mesh hub.</param>
    /// <param name="sourcePath">The subtree root to copy.</param>
    /// <param name="targetNamespace">The namespace the copy lands under; empty means the root.</param>
    /// <param name="force">Overwrite an existing target node instead of leaving it alone.</param>
    /// <param name="logger">Optional; resolved from <paramref name="hub"/> when omitted.</param>
    /// <param name="batchSize">Per-level concurrency bound.</param>
    /// <returns>A cold observable emitting the number of nodes written.</returns>
    public static IObservable<int> CopyNodeTree(
        IMeshService meshQuery,
        IMeshService nodeFactory,
        IMessageHub hub,
        string sourcePath,
        string targetNamespace,
        bool force,
        ILogger? logger = null,
        int batchSize = DefaultBatchSize)
        => CopyNodeTreeOutcome(
                meshQuery, nodeFactory, hub, sourcePath, targetNamespace, force, logger, batchSize)
            .SelectMany(outcome => outcome.IsComplete
                ? Observable.Return(outcome.CopiedCount)
                : Observable.Throw<int>(new InvalidOperationException(outcome.Describe())));

    /// <summary>
    /// Copies a node and all its descendants to a target namespace and reports what became of every
    /// one of them.
    ///
    /// <para><b>The three things it establishes, in order.</b></para>
    ///
    /// <para><b>1. The enumeration's own completeness — before writing anything.</b> The subtree
    /// listing runs AS THE CALLER, because that is the read row-level security filters and copying
    /// out from under it would let a caller duplicate rows they may not read into a place they
    /// control. But a listing that came back short BECAUSE of that filter is not an empty subtree,
    /// and the two used to be the same value. The size of the subtree is therefore established a
    /// second time, from the SAME query run as System — the shape MeshWeaver.Plugins' installer
    /// already uses, for the same reason: a gated package hides its children from a subject-scoped
    /// query. A difference is a REFUSAL, not a smaller copy.</para>
    ///
    /// <para><b>2. Parents before children.</b> Nodes are written level by level, deepest last,
    /// with <paramref name="batchSize"/> in flight WITHIN a level. Nothing in the platform requires
    /// a parent to exist — no handler, validator, router or storage adapter probes one — so this is
    /// not a correctness fix for the happy path. It is what makes the FAILURE well-formed: a level
    /// that does not land ends the descent, so a copy that stops leaves a prefix of the tree instead
    /// of scattering children under parents that never arrived. It also honours what
    /// <see cref="IStorageAdapter.WriteMany"/> states about ordering — "callers order parents before
    /// children on purpose … activating a child's per-node hub while its parent's is still cold is
    /// the race that used to wedge installs".</para>
    ///
    /// <para><b>3. A post-condition over the ledger, not a re-read.</b> Every enumerated node ends
    /// with a <see cref="NodeCopyEntry"/> saying what the owning hub ACKNOWLEDGED. The outcome is
    /// then a set comparison: covered vs enumerated. It is assembled from the write
    /// acknowledgements rather than by querying the target back, because a query is eventually
    /// consistent and would answer about a moment that is not this one.</para>
    ///
    /// <para><b>And deliberately NOT a rollback.</b> There is no cross-partition transaction here —
    /// each node is its own hub and its own row, possibly in another schema — and under
    /// <paramref name="force"/> a copy overwrites nodes it holds no before-image of, so a
    /// compensating delete could not restore them and would destroy them instead. A compensating
    /// delete is a second uncontrolled mutation on the path where the code is least trustworthy;
    /// the guarantee offered instead is all-or-nothing at the point of REFUSAL (1), and a
    /// well-formed prefix plus a complete inventory when a write fails (2 + 3).</para>
    ///
    /// <para><b>Routing</b> — every per-node request is targeted at
    /// <c>hub.NodeOperationTarget()</c>: the nearest ancestor that declared
    /// <c>WithNodeOperationExecution</c>, else the root mesh hub, where the node-operation
    /// handlers are guaranteed to be registered.</para>
    ///
    /// <para><b>force semantics</b> are encoded in the request verb: <c>false</c> uses
    /// <see cref="CreateNodeRequest"/> and records an existing target as
    /// <see cref="NodeCopyDisposition.SkippedExisting"/>; <c>true</c> uses
    /// <see cref="CreateOrUpdateNodeRequest"/>, which writes either way. The helper never deletes a
    /// target — that race against the per-node hub's disposal was the cause of the previous
    /// "GetNode returns null after force-overwrite" bug.</para>
    /// </summary>
    /// <param name="meshQuery">Reads the source subtree.</param>
    /// <param name="nodeFactory">Unused: the per-node writes route through <paramref name="hub"/> to
    /// its node-operation target.</param>
    /// <param name="hub">Supplies the caller identity, the reply address and the node-operation
    /// target.</param>
    /// <param name="sourcePath">The subtree root to copy.</param>
    /// <param name="targetNamespace">The namespace the copy lands under; empty means the root.</param>
    /// <param name="force">Overwrite an existing target node instead of leaving it alone.</param>
    /// <param name="logger">Optional; resolved from <paramref name="hub"/> when omitted.</param>
    /// <param name="batchSize">Per-level concurrency bound.</param>
    /// <returns>A cold observable emitting exactly one <see cref="NodeCopyOutcome"/>.</returns>
    public static IObservable<NodeCopyOutcome> CopyNodeTreeOutcome(
        IMeshService meshQuery,
        IMeshService nodeFactory,
        IMessageHub hub,
        string sourcePath,
        string targetNamespace,
        bool force,
        ILogger? logger = null,
        int batchSize = DefaultBatchSize)
    {
        logger ??= hub.ServiceProvider.GetService<ILoggerFactory>()
            ?.CreateLogger("MeshWeaver.Graph.NodeCopyHelper");

        // 🚨 Capture the caller's identity EAGERLY, at invocation (the click action / MCP tool /
        // handler thread where AsyncLocal is still correct). The per-node posts below fire from
        // SelectMany continuations on the query's emission scheduler, where the AsyncLocal
        // AccessContext is WIPED — so an un-stamped post would carry no identity and the
        // PostPipeline would fail closed (only the root landing; recursive children erroring with
        // "AccessContext must never be null … message=CreateNodeRequest" — the cross-partition copy
        // bug). Stamp the captured context explicitly on every post, exactly as FanOutDeleteSubtree
        // does for recursive deletes — and run the ENUMERATION under it too, so what the copy reads
        // and what it writes are the same identity by construction rather than by scheduler luck.
        var accessService = hub.ServiceProvider.GetService<AccessService>();
        var callerAccessContext = accessService?.Context ?? accessService?.CircuitContext;

        // 🚨 Target the per-node create/upsert requests EXPLICITLY — never leave it to the calling
        // hub. An un-targeted Observe delivers to the CALLER, which only works when that hub happens
        // to register the node-op handlers itself (per-node UI hubs do, via AddMeshDataSource). The
        // MCP/REST session hub (portal/mcp-{user}-{session}, SessionHubResolver) used to register
        // only AddData — every per-node post bounced with "No handler found for message type
        // CreateNodeRequest" (memex 2026-07-13, MCP copy tool). NodeOperationTarget resolves to the
        // nearest hub that declared WithNodeOperationExecution — that session hub now does — and to
        // the root mesh hub otherwise.
        var operationTarget = hub.NodeOperationTarget();

        // Shared post options for both verbs: route to that target and stamp the
        // eagerly-captured caller identity (mirrors MeshService.ConfigurePost).
        PostOptions ConfigurePost(PostOptions o)
        {
            o = o.WithTarget(operationTarget);
            return callerAccessContext is null ? o : o.WithAccessContext(callerAccessContext);
        }

        // The namespace the source root lives in, derived from the path so the refusal messages
        // below can name the target before any node has been read.
        var sourceNamespace = MeshNode.FromPath(sourcePath).Namespace ?? "";
        var targetRootPath = RemapPathFor(sourcePath, sourceNamespace, targetNamespace);

        // 🚨 THE DENOMINATOR NEEDS A SECOND IDENTITY, AND WITHOUT ONE THERE IS NO ANSWER. With no
        // AccessService there is nothing to run the System-scoped enumeration under, so the two
        // readings would be the SAME read and the comparison would pass having compared a set with
        // itself — a control whose green is guaranteed by construction
        // (Doc/Architecture/ControlsThatCannotFail). Say "not determined" instead. Unreachable on a
        // real mesh: every hosting path registers AccessService.
        if (accessService is null)
            return Observable.Return(NodeCopyOutcome.CompletenessNotDetermined(
                sourcePath, targetRootPath,
                "no AccessService is registered, so the subtree cannot be enumerated a second time "
                + "as System and a short enumeration would be indistinguishable from a small subtree"));

        // ONE query shape, read twice under two identities — the difference between the answers IS
        // the completeness question, and sharing the shape is what makes the difference mean only
        // that. Complete(): this is an ENUMERATION, not a page (IMeshQuery.Complete).
        var subtreeQuery = MeshQueryRequest
            .FromQuery($"path:{sourcePath} scope:subtree")
            .Complete();

        IObservable<IReadOnlyList<MeshNode>> Enumerate() => meshQuery
            .Query<MeshNode>(subtreeQuery)
            .Take(1)
            .Select(c => (IReadOnlyList<MeshNode>)c.Items
                .Where(n => !string.IsNullOrEmpty(n.Path))
                .ToArray());

        // AS THE CALLER — explicitly, not by ambient. This is the set that may be copied, and it is
        // the read row-level security filters. Take(1) snapshots the listing; later edits to the
        // source don't follow.
        var callerView = accessService.RunAs(callerAccessContext, Enumerate);

        return callerView.SelectMany(allNodes =>
        {
            var sourceNode = allNodes.FirstOrDefault(n =>
                string.Equals(n.Path, sourcePath, StringComparison.Ordinal));
            if (sourceNode == null)
                return Observable.Return(
                    NodeCopyOutcome.SourceNotFound(sourcePath, targetRootPath));

            var enumerated = allNodes
                .Select(n => n.Path)
                .ToImmutableHashSet(StringComparer.OrdinalIgnoreCase);

            // AS SYSTEM — the same query, so the only thing that can differ is what the identity is
            // permitted to see. SecurityService grants Permission.All to System unconditionally,
            // and RunAsSystem seals the scope at both ends so neither the subscribing thread nor
            // anything composed downstream inherits it (#1790/#1444). Nothing read here is ever
            // COPIED: only the size of the set is taken from it.
            return accessService.RunAsSystem(Enumerate).SelectMany(sourceNodes =>
            {
                var hidden = sourceNodes.Count(n => !enumerated.Contains(n.Path));
                if (hidden > 0)
                {
                    logger?.LogWarning(
                        "Refusing copy {SourcePath} -> {TargetNamespace}: the subtree holds "
                        + "{Held} node(s) and this caller can read {Readable}",
                        sourcePath, targetNamespace, sourceNodes.Count, allNodes.Count);
                    return Observable.Return(NodeCopyOutcome.SourceNotFullyReadable(
                        sourcePath, targetRootPath, allNodes.Count, sourceNodes.Count));
                }

                return WriteLevels(allNodes)
                    .Select(entries => entries.All(e => e.Covered)
                        ? NodeCopyOutcome.Copied(
                            sourcePath, targetRootPath, entries, sourceNodes.Count)
                        : NodeCopyOutcome.Incomplete(
                            sourcePath, targetRootPath, entries, sourceNodes.Count));
            });
        });

        // ---- the write, level by level -------------------------------------------------------

        IObservable<ImmutableList<NodeCopyEntry>> WriteLevels(IReadOnlyList<MeshNode> allNodes)
        {
            // Depth is the segment count of the path, so a level holds only nodes whose ancestors
            // are all in shallower levels. Same computation MeshOperations' import already orders
            // by ("Parents first — deterministic, and lets a partition root land before children").
            var levels = allNodes
                .GroupBy(n => n.Path.Count(c => c == '/'))
                .OrderBy(g => g.Key)
                .Select(g => g.OrderBy(n => n.Path, StringComparer.Ordinal).ToImmutableList())
                .ToImmutableList();

            NodeCopyEntry NotAttempted(MeshNode node) => new(
                node.Path,
                RemapPathFor(node.Path, sourceNamespace, targetNamespace),
                NodeCopyDisposition.NotAttempted);

            IObservable<ImmutableList<NodeCopyEntry>> Descend(
                int level, ImmutableList<NodeCopyEntry> ledger)
            {
                if (level >= levels.Count)
                    return Observable.Return(ledger);

                return levels[level]
                    .Select(CopyOne)
                    .ToObservable()
                    .Merge(batchSize)
                    .ToList()
                    .SelectMany(written =>
                    {
                        var next = ledger.AddRange(written);
                        if (!written.Any(e => e.Disposition == NodeCopyDisposition.Failed))
                            return Descend(level + 1, next);

                        // 🚨 Stop the descent. A child of a node that did not land is an orphan —
                        // exactly the residue measured on the unfixed helper, where a leaf reached
                        // the target under a folder that never did. Everything deeper is recorded
                        // as NOT ATTEMPTED, which is a different fact from "failed" and is the half
                        // nobody could see before.
                        return Observable.Return(next.AddRange(
                            levels.Skip(level + 1).SelectMany(l => l).Select(NotAttempted)));
                    });
            }

            return Descend(0, []);
        }

        // ---- one node --------------------------------------------------------------------------

        IObservable<NodeCopyEntry> CopyOne(MeshNode node)
        {
            var newPath = RemapPathFor(node.Path, sourceNamespace, targetNamespace);
            var copiedNode = MeshNode.FromPath(newPath) with
            {
                Name = node.Name,
                NodeType = node.NodeType,
                Icon = node.Icon,
                Category = node.Category,
                Content = node.Content,
                State = MeshNodeState.Active,
                PreRenderedHtml = node.PreRenderedHtml,
            };

            NodeCopyEntry Entry(NodeCopyDisposition disposition, string? error = null) =>
                new(node.Path, newPath, disposition) { Error = error };

            var attempt = force
                ? hub.Observe<CreateOrUpdateNodeResponse>(
                        new CreateOrUpdateNodeRequest(copiedNode), ConfigurePost)
                    .FirstAsync()
                    .Select(d => d.Message)
                    .Select(resp =>
                    {
                        if (!resp.Success)
                            return Entry(NodeCopyDisposition.Failed, resp.Error);
                        logger?.LogInformation(
                            "Copied {SourcePath} -> {TargetPath} ({Mode})",
                            node.Path, newPath, resp.WasCreated ? "created" : "updated");
                        return Entry(resp.WasCreated
                            ? NodeCopyDisposition.Created
                            : NodeCopyDisposition.Updated);
                    })
                : hub.Observe<CreateNodeResponse>(new CreateNodeRequest(copiedNode), ConfigurePost)
                    .FirstAsync()
                    .Select(d => d.Message)
                    .Select(resp =>
                    {
                        if (resp.Success)
                        {
                            logger?.LogInformation(
                                "Copied node {SourcePath} -> {TargetPath}", node.Path, newPath);
                            return Entry(NodeCopyDisposition.Created);
                        }
                        if (resp.RejectionReason == NodeCreationRejectionReason.NodeAlreadyExists)
                        {
                            logger?.LogInformation(
                                "Skipping existing node at {TargetPath}", newPath);
                            return Entry(NodeCopyDisposition.SkippedExisting);
                        }
                        return Entry(NodeCopyDisposition.Failed, resp.Error);
                    });

            // 🚨 NOT a swallow. The fault is RECORDED, it ends the descent, and it makes the whole
            // operation fail carrying the full inventory. What it stops is the merge aborting on
            // the first fault: that used to cancel the observation of every sibling already in
            // flight — whose writes had been POSTED and landed anyway — so the one path in the
            // exception was the only one anybody could name, and the fate of the rest was
            // unknowable. Letting each node answer for itself is what makes the post-condition
            // possible at all.
            return attempt.Catch((Exception ex) =>
                Observable.Return(Entry(NodeCopyDisposition.Failed, ex.Message)));
        }
    }

    private static string RemapPathFor(string path, string sourceNamespace, string targetNamespace)
    {
        string relativePart;
        if (string.IsNullOrEmpty(sourceNamespace))
        {
            relativePart = path;
        }
        else if (path.StartsWith(sourceNamespace + "/", StringComparison.Ordinal))
        {
            relativePart = path[(sourceNamespace.Length + 1)..];
        }
        else
        {
            relativePart = path;
        }

        return string.IsNullOrEmpty(targetNamespace)
            ? relativePart
            : $"{targetNamespace}/{relativePart}";
    }
}
