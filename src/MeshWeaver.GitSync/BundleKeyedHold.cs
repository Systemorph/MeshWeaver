using System.Collections.Immutable;
using System.Reactive.Linq;
using MeshWeaver.Compiler;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.GitSync;

/// <summary>
/// One NodeType whose sources an import HELD because no bundle for this instance's framework
/// identity carries the fingerprint they would produce (MeshWeaver#3845 hole 4).
/// </summary>
/// <param name="Path">The NodeType's mesh path.</param>
/// <param name="HeldFingerprint">The fingerprint its sources have NOW — the one its adopted bytes
/// were built from.</param>
/// <param name="WantedFingerprint">The fingerprint the incoming tree would produce — what a bundle
/// must record for this hold to release.</param>
/// <param name="Identity">The framework identity the judgement was made under. A roll makes the
/// judgement VOID rather than merely old, which is why it is recorded beside the fingerprints.</param>
/// <param name="Reason">Log copy: why this type is held.</param>
public sealed record BundleHeldNodeType(
    string Path, string HeldFingerprint, string WantedFingerprint, string Identity, string Reason)
{
    /// <summary>
    /// 🚨 THREE states, and the third is not decoration. <c>true</c>: this type is held only because
    /// it SHARES a source with another held type, not on its own reading. <c>false</c>: it is held on
    /// its own reading. <c>null</c>: the entry was written BEFORE this field existed (#4595) and
    /// cannot say which — the field is persisted on the sync config, so records predating it are
    /// read back by a portal that has this code.
    ///
    /// <para><b>Why the distinction decides a release.</b> A sharer's own wanted fingerprint can
    /// already be on the shelf while the type it shares with is still waiting. Releasing on the
    /// sharer would re-import, re-hold the identical set (the root is still missing) and do it again
    /// on every later publication — a futile import per announcement. So only an INDEPENDENTLY held
    /// type is a release trigger; a sharer is re-judged by the import the root's own release
    /// dispatches.</para>
    ///
    /// <para>🚨 <b>Why <c>null</c> is not folded into <c>false</c></b> (review on #4605). A plain
    /// <c>bool</c> would read every #4595 record as "independently held", which is exactly the
    /// futile-import loop above for a legacy sharer. It is not folded into <c>true</c> either: that
    /// would leave a legacy INDEPENDENT hold unable to release on the arrival it is waiting for.
    /// <c>SealedSyncReconcile.ReleasesAHold</c> therefore treats an unknown entry as a trigger only
    /// when the shelf now carries EVERY held entry's fingerprint — the one case where the re-import
    /// cannot be futile, because it clears the whole set. Entries this code writes are always
    /// explicit, so the unknown state is transitional by construction: the first import that
    /// concludes rewrites the list (<c>RecordSyncResult</c> writes it on every conclusion).</para>
    ///
    /// <para>An <c>init</c> property rather than a sixth positional parameter: this record is
    /// persisted on the sync config and its primary-constructor arity is public surface
    /// (<c>scripts/check-record-signatures.py</c>).</para>
    /// </summary>
    public bool? HeldBySharing { get; init; }
}

/// <summary>
/// What an import may write, once the bundle inventory has spoken: which NodeTypes are held, which
/// node paths therefore neither move nor prune, and which types the gate declined to judge.
/// </summary>
/// <param name="Held">The held types, ordinal by path.</param>
/// <param name="HeldPaths">Every node path held — a type's own node, its sources and its include
/// closure, in BOTH the incoming tree and the current partition. Space-RELATIVE, the shape
/// <see cref="SyncIgnore"/> matches.</param>
/// <param name="Abstained">Types the gate could not judge, with the reason — stated, never silent:
/// they import exactly as they did before.</param>
public sealed record BundleHoldDecision(
    ImmutableList<BundleHeldNodeType> Held,
    ImmutableHashSet<string> HeldPaths,
    ImmutableList<string> Abstained)
{
    /// <summary>Nothing held and nothing abstained — the answer for a mesh with no inventory to
    /// consult, and for an import that moves no adopted type.</summary>
    public static BundleHoldDecision Nothing { get; } = new([], ImmutableHashSet<string>.Empty, []);

    /// <summary>True when this import holds something back, i.e. writes less than it fetched.</summary>
    public bool Holds => !Held.IsEmpty;

    /// <summary>Whether a node path (ABSOLUTE) is held by this decision.</summary>
    /// <param name="partition">The Space the import writes.</param>
    /// <param name="nodePath">The node's absolute mesh path.</param>
    /// <returns>True when the import must neither write nor prune it.</returns>
    public bool HoldsNode(string partition, string? nodePath)
        => nodePath is { Length: > 0 } path
           && !HeldPaths.IsEmpty
           && path.StartsWith(partition + "/", StringComparison.OrdinalIgnoreCase)
           && HeldPaths.Contains(path[(partition.Length + 1)..]);
}

/// <summary>
/// 🚨 <b>A NodeType's sources wait for its bundle</b> — the per-type half of adopt-then-sync
/// (MeshWeaver#3845 hole 4). The full design, including what a partially-held Space means for
/// <c>LastSyncCommitSha</c> and what releases a hold, is
/// <c>Doc/Architecture/AdoptThenSyncPerNodeType</c>.
///
/// <para><b>Why the seal cannot answer this.</b> <see cref="SealedSyncGate"/> lands a Space on the
/// commit this instance's bundles were baked from, and that is a REPOSITORY fact:
/// <see cref="SealedSource"/> says "the sentinel is present and every bundle it lists is on disk".
/// Adoption is decided per TYPE — one bundle entry's source fingerprint against that type's own
/// <c>CurrentSourceFingerprint</c> — and a publication can be sealed, at the right commit, under the
/// right identity, and still not contain the bundle a given type needs (#3461: the two producers of
/// one prefix compose different module sets for one identity). So the tree can be right while a
/// type's sources move onto bytes this instance does not have.</para>
///
/// <para><b>The rule.</b> An import never moves an ADOPTED NodeType's compile input onto a
/// fingerprint no bundle for this identity carries. That type's source set is held — neither written
/// nor pruned — and the rest of the Space imports.</para>
///
/// <para>🚨 <b>Only on a <c>Modules:RequirePrebuilt</c> mesh</b> (policy
/// <c>module-sync-per-manifest-hash</c>). Everywhere else a changed type is NOT held: under the
/// compatibility ladder it compiles from the synced source against the running platform, and
/// holding its sources stranded it on an old tree until a publication or a roll arrived. A
/// RequirePrebuilt mesh refuses the local compile by design, so moving the sources there would park
/// the type — the one case this hold still protects, which is why
/// <c>BundleKeyedHoldReading</c> asks this decision only on such a mesh. The pure decision below is
/// unchanged: it is what that mesh takes.</para>
///
/// <para>Pure and offline: the caller supplies the incoming tree, the current partition, the live
/// definitions and the inventory. The fingerprints are computed by the SAME functions the bake and
/// the owner use (<see cref="NodeSet.ResolveSources"/> +
/// <c>NodeTypeSourceFingerprint.ComputeWithIncludes</c>), because a second implementation of "which
/// files count" would make a shape difference look like staleness (#2813).</para>
/// </summary>
public static class BundleKeyedHold
{
    /// <summary>
    /// Decides which NodeTypes an import must hold. Six answers in order; only the last holds.
    /// </summary>
    /// <param name="partition">The Space being imported.</param>
    /// <param name="incoming">The parsed nodes the import would write (the tree at the commit).</param>
    /// <param name="current">The partition's nodes as they are now.</param>
    /// <param name="live">Live definitions by NodeType path — read authoritatively (the node's own
    /// stream), never from a query snapshot, because a stale one would decide a hold.</param>
    /// <param name="definitionOf">Reads a node's <see cref="NodeTypeDefinition"/> with the caller's
    /// serializer options — never a cast: a definition that arrived as untyped JSON reads as absent,
    /// and this gate must see the difference.</param>
    /// <param name="inventory">What bundles for this identity carry, per type.</param>
    /// <param name="identity">This instance's framework identity.</param>
    /// <param name="logger">Diagnostics.</param>
    /// <returns>The decision; never null.</returns>
    public static BundleHoldDecision Decide(
        string partition,
        IReadOnlyList<MeshNode> incoming,
        IReadOnlyList<MeshNode> current,
        IReadOnlyDictionary<string, NodeTypeDefinition> live,
        Func<MeshNode, NodeTypeDefinition?> definitionOf,
        PrebuiltBundleInventory inventory,
        string identity,
        ILogger? logger = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(partition);
        ArgumentNullException.ThrowIfNull(incoming);
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(live);
        ArgumentNullException.ThrowIfNull(definitionOf);
        ArgumentNullException.ThrowIfNull(inventory);

        // A mesh that consumes no bundles has nothing to keep in step: every type compiles here, so
        // holding its sources would wait for a publication that is never coming — hole 1's
        // adjudication, one level down.
        //
        // 🚨 And an UNREADABLE reading holds NOTHING either, which is the opposite of this file's
        // usual direction and is deliberate (review on #4595). "Cannot tell is never clear to
        // proceed" applies where a hold can be RELEASED; here it cannot: the release predicate reads
        // the same shelf (`SealedSyncReconcile.ReleasesAHold` refuses an unreadable inventory), so
        // one unreadable archive would wedge every changed adopted type of the Space with nothing
        // able to clear it. Holding requires evidence that the bytes are absent, and an unreadable
        // shelf is not that evidence — the type imports as it did before this gate and may go
        // StaleAdopted, which is honest, serving and announced (#3583).
        //
        // 🚨 And it does NOT reopen what #3461 phase 5 closed ("cannot tell", never "nothing
        // sealed"): that contract is about the SEAL INDEX, whose unreadable reading still holds at
        // the SOURCE level (`SealedSyncGate.RefusedForUnreadableIndex`) — which means the import
        // this gate sits inside does not run at all. What reaches this branch is the narrow case
        // where the index read fine and an ARCHIVE did not, inside an import the seal has already
        // cleared; the source cannot move past its seal either way.
        if (inventory.Outcome is not SealedReadOutcome.Read || live.Count == 0)
        {
            if (inventory.Outcome is SealedReadOutcome.Unreadable)
                logger?.LogWarning(
                    "[BundleHold] {Partition}: the bundle inventory for framework identity {Identity} "
                    + "could not be READ, so no NodeType is held — a hold taken from an unreadable "
                    + "shelf could not be released by the same shelf. The import writes what it "
                    + "fetched; a type whose sources move past its bundle reports StaleAdopted until "
                    + "the shelf is readable again", partition, identity);
            return BundleHoldDecision.Nothing;
        }

        var incomingSet = NodeSet.Create(incoming);
        var currentSet = NodeSet.Create(current);
        var abstained = ImmutableList.CreateBuilder<string>();
        var candidates = new List<Candidate>();

        foreach (var (typePath, definition) in live.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            // 1. Only a VERIFIED adoption has something to keep in step. `Compiled` recompiles from
            //    whatever lands; `StaleAdopted` / `AdoptionRefused` are already behind, so a hold
            //    protects nothing; `AdoptedUnverified` was never compared against any source, so
            //    there is no verified state to preserve.
            if (definition.BuildProvenance is not BuildProvenance.AdoptedVerified
                || definition.CurrentSourceFingerprint is not { Length: > 0 } liveFingerprint)
                continue;

            // A type the repository DROPPED is the importer's retirement decision
            // (StaticRepoImportResult.HeldNodeTypePaths), not this gate's.
            if (incomingSet.Find(typePath) is not { } incomingTypeNode)
                continue;
            var incomingDefinition = definitionOf(incomingTypeNode) ?? definition;

            // 2. CALIBRATE, and abstain when this import cannot reproduce the reading it would act
            //    on. Rather than guessing whether a type's queries reach outside this Space, compute
            //    the fingerprint the live record already states: equal ⇒ the incoming computation is
            //    trustworthy; different ⇒ a move cannot be judged here, and a gate that cannot
            //    reproduce today's answer must not act on tomorrow's.
            var currentFold = Fold(currentSet, typePath, definition, logger);
            if (currentFold is null || !string.Equals(currentFold, liveFingerprint, StringComparison.Ordinal))
            {
                abstained.Add(
                    $"{typePath}: this import cannot reproduce the live source fingerprint "
                    + $"(live {liveFingerprint}, computed {currentFold ?? "unestablished"}) — its "
                    + "compile input reaches beyond this Space's tree, so a move cannot be judged here");
                continue;
            }

            // 3. Does the type's compile input move at all?
            var incomingFold = Fold(incomingSet, typePath, incomingDefinition, logger);
            if (incomingFold is null)
            {
                abstained.Add(
                    $"{typePath}: the incoming tree could not establish this type's source queries, so "
                    + "the fingerprint it would produce is unknown");
                continue;
            }

            candidates.Add(new Candidate(
                typePath,
                currentFold,
                incomingFold,
                [
                    .. SourcePathsOf(currentSet, typePath, definition, logger)
                        .Concat(SourcePathsOf(incomingSet, typePath, incomingDefinition, logger))
                        .Select(p => Relative(partition, p)),
                ]));
        }

        var held = new Dictionary<string, BundleHeldNodeType>(StringComparer.OrdinalIgnoreCase);
        var heldPaths = ImmutableHashSet.CreateBuilder<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var candidate in candidates)
        {
            // 4. Unchanged input — nothing to hold.
            if (string.Equals(candidate.IncomingFold, candidate.CurrentFold, StringComparison.Ordinal))
                continue;
            // 5. A bundle for THIS identity carrying the incoming fingerprint means the release this
            //    import already performs adopts it: AdoptedVerified → AdoptedVerified.
            if (inventory.Carries(candidate.Path, candidate.IncomingFold))
                continue;

            // 6. Otherwise the sources would run ahead of the bytes.
            var recorded = inventory.FingerprintsOf(candidate.Path);
            held[candidate.Path] = new BundleHeldNodeType(
                candidate.Path, candidate.CurrentFold, candidate.IncomingFold, identity,
                recorded.IsEmpty
                    ? $"no bundle for framework identity {identity} names this type, and its sources "
                      + $"would move from {candidate.CurrentFold} to {candidate.IncomingFold}"
                    : $"the bundle(s) for framework identity {identity} record "
                      + $"{string.Join(", ", recorded.OrderBy(f => f, StringComparer.Ordinal))} for this "
                      + $"type, not the {candidate.IncomingFold} its incoming sources would produce")
            {
                // 🚨 STAMPED, never defaulted: `null` is reserved for a record written before this
                // field existed, and a release reads the three states differently (review on #4605).
                HeldBySharing = false,
            };
            Hold(candidate);
        }

        if (held.Count == 0)
            return new BundleHoldDecision([], ImmutableHashSet<string>.Empty, abstained.ToImmutable());

        // 🚨 SHARING CLOSES OVER THE HOLD. A source node shared by a held type and an unheld one is
        // held, so the unheld type's own input cannot move either — and it must be held too, or its
        // fingerprint would land on a fold made of the held node's OLD text beside the tree's new
        // siblings, which no bundle carries. Iterated to a fixed point, so a chain of sharers closes.
        bool grew;
        do
        {
            grew = false;
            foreach (var candidate in candidates)
            {
                if (held.ContainsKey(candidate.Path))
                    continue;
                if (candidate.SourcePaths.FirstOrDefault(heldPaths.Contains) is not { } shared)
                    continue;
                held[candidate.Path] = new BundleHeldNodeType(
                    candidate.Path, candidate.CurrentFold, candidate.IncomingFold, identity,
                    $"shares held source '{shared}' with another held type, so its own compile input "
                    + "cannot move either")
                {
                    // Not a release trigger: its own bundle may already be on the shelf, and
                    // re-importing for that would re-hold the same set — see BundleHeldNodeType.
                    HeldBySharing = true,
                };
                Hold(candidate);
                grew = true;
            }
        }
        while (grew);

        var all = held.Values.OrderBy(h => h.Path, StringComparer.Ordinal).ToImmutableList();
        logger?.LogWarning(
            "[BundleHold] {Partition}: {Count} NodeType(s) HELD — their sources would move onto a "
            + "fingerprint no bundle for framework identity {Identity} carries ({Bundles} bundle(s) "
            + "read): {Held}. The rest of the Space imports and the Space keeps the commit it holds; "
            + "a publication carrying the wanted fingerprint, a new commit, or a roll releases it "
            + "(Doc/Architecture/AdoptThenSyncPerNodeType).",
            partition, all.Count, identity, inventory.Bundles,
            string.Join("; ", all.Select(h => $"{h.Path} ({h.Reason})")));

        return new BundleHoldDecision(all, heldPaths.ToImmutable(), abstained.ToImmutable());

        void Hold(Candidate candidate)
        {
            // The type's OWN node is held with its sources: its `configuration` lambda is compile
            // input the fingerprint does not cover, so importing a new configuration over held
            // sources would compile a combination neither the bake nor this mesh has ever seen.
            heldPaths.Add(Relative(partition, candidate.Path));
            foreach (var path in candidate.SourcePaths)
                heldPaths.Add(path);
        }
    }

    /// <summary>One type's readings, computed once: both folds and every source path either side
    /// names (Space-relative).</summary>
    private sealed record Candidate(
        string Path, string CurrentFold, string IncomingFold, ImmutableArray<string> SourcePaths);

    /// <summary>
    /// One type's source fingerprint over a node set — the bake's own computation
    /// (<see cref="NodeSet.ResolveSources"/> + <c>NodeTypeSourceFingerprint.ComputeWithIncludes</c>,
    /// includes resolved against the same set), or null when the set could not establish the type's
    /// source queries.
    ///
    /// <para>Every read here is a dictionary lookup, so the computation completes on SUBSCRIBE and
    /// the value is in hand when this returns. A composition that did not complete synchronously
    /// would leave it null, which is the abstain direction — never a hold taken from a value that
    /// never arrived.</para>
    /// </summary>
    private static string? Fold(
        NodeSet nodes, string typePath, NodeTypeDefinition definition, ILogger? logger)
    {
        var resolution = nodes.ResolveSources(definition.Sources, definition.Tests, typePath);
        if (!resolution.IsEstablished)
            return null;
        string? fingerprint = null;
        NodeTypeSourceFingerprint
            .ComputeWithIncludes(resolution.Sources, typePath,
                (anchored, authored) => Observable.Return(ReadInclude(nodes, anchored, authored)),
                logger)
            // A one-shot, so the error arm is the fault's only home: a fold that threw leaves the
            // value null, which is the ABSTAIN direction — never a hold taken from a fault.
            .Subscribe(
                computed => fingerprint = computed.Fingerprint,
                exception => logger?.LogWarning(exception,
                    "[BundleHold] the source fingerprint of {TypePath} could not be computed — this "
                    + "import does not judge it", typePath));
        return fingerprint;
    }

    /// <summary>Every node path a type's compile input names in one set: the resolved sources plus
    /// the <c>@@</c>-include closure. Absolute paths.</summary>
    private static IEnumerable<string> SourcePathsOf(
        NodeSet nodes, string typePath, NodeTypeDefinition definition, ILogger? logger)
    {
        var resolution = nodes.ResolveSources(definition.Sources, definition.Tests, typePath);
        var paths = resolution.Sources.Select(n => n.Path).ToList();
        NodeTypeSourceFingerprint
            .ComputeWithIncludes(resolution.Sources, typePath,
                (anchored, authored) => Observable.Return(ReadInclude(nodes, anchored, authored)),
                logger)
            .Subscribe(
                computed => paths.AddRange(computed.Includes),
                // A one-shot: a closure that threw leaves the query-resolved paths alone, so a hold
                // covers what was established and the fault is stated rather than rethrown onto the
                // scheduler's thread.
                exception => logger?.LogWarning(exception,
                    "[BundleHold] the include closure of {TypePath} could not be resolved — its hold "
                    + "covers the query-resolved sources only", typePath));
        return paths;
    }

    /// <summary>The include reader <c>NodeSetCompiler</c> uses — the anchored path, then the authored
    /// one, then absent (an absent include is an answer, never a stall).</summary>
    private static (MeshNode? Node, string Path) ReadInclude(NodeSet nodes, string anchored, string? authored)
    {
        if (nodes.Find(anchored) is { } hit)
            return (hit, anchored);
        if (authored is { Length: > 0 } && nodes.Find(authored) is { } fallback)
            return (fallback, authored);
        return (null, anchored);
    }

    /// <summary>A node path as <see cref="SyncIgnore"/> matches it: relative to the Space root, no
    /// leading slash. A path outside the partition is returned whole — this Space's import can
    /// neither write nor prune it, and the ignore matcher never matches it either.</summary>
    private static string Relative(string partition, string nodePath)
        => nodePath.StartsWith(partition + "/", StringComparison.OrdinalIgnoreCase)
            ? nodePath[(partition.Length + 1)..]
            : nodePath;
}
