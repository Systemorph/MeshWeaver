using System.Collections.Immutable;
using System.Reactive.Linq;
using MeshWeaver.Hosting.Persistence.Parsers;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.PluginCatalog;

/// <summary>
/// 🚨 <b>On a closed type set the package installer holds no NodeType definition rows</b> (#6627,
/// <c>Doc/Architecture/ClosedTypeSet</c> → "Package installs").
///
/// <para><b>Why.</b> A closed type set (<c>Mesh:ClosedTypeSet=true</c>) activates only the
/// NodeTypes its image registers in code; a <c>nodeType: NodeType</c> row is never compiled, adopted
/// or served there. Writing one anyway produced a row that could only ever settle at the closed-set
/// refusal — measured on the control instance: 73 of 126 NodeTypes at <c>Error</c>, every one
/// carrying <c>"NodeType 'X' is not part of this image's closed type set"</c> and no compile defect,
/// so the pre-prod NodeType sweep could never read zero there while package content was
/// installed.</para>
///
/// <para><b>What.</b> Every install lane (full node-repo, incremental delta, content, code) drops
/// the package's NodeType rows from its write set (<see cref="Withhold"/>) and removes the ones an
/// earlier install on this host left behind (<see cref="Reconcile"/>), and the boot repair pass
/// does the same over every install record so a host whose packages never update converges too.
/// Everything else the package ships — its root, instances, Source nodes, access satellites, the
/// install record — and the module assemblies installed beside it are unchanged.</para>
///
/// <para><b>Only the row is removed.</b> A NodeType's path prefixes its own <c>Source/</c> and, for
/// a leaf-shaped type, its instances, so a recursive delete would take records with it. The row is
/// deleted alone through the framework delete (<see cref="DeleteNodeRequest.KeepDescendants"/>), so
/// the delete validators run, the change feed and stream cache hear it, and a live activation at
/// the path is disposed — never a direct storage write. A CLAIMED row (<see cref="SyncBehavior"/> other than
/// <see cref="SyncBehavior.Include"/>) is the user's, not the package's — it is named and kept,
/// the same fence the installer's prune applies.</para>
/// </summary>
internal static class ClosedTypeSetPackageRows
{
    /// <summary>True for a NodeType definition row — <c>nodeType: NodeType</c>. Deliberately NOT
    /// <c>ImportWriteOrder.IsNodeTypeDefinition</c>, which also matches a <c>Space</c> partition
    /// ROOT carrying type content: that node is the package's root, not a type row, and withholding
    /// it would withhold the whole partition.</summary>
    /// <param name="node">The node to classify.</param>
    internal static bool IsNodeTypeRow(MeshNode node) =>
        string.Equals(node.NodeType, MeshNode.NodeTypePath, StringComparison.OrdinalIgnoreCase);

    /// <summary>The nodes an install writes on this host, and the NodeType rows it withholds.</summary>
    /// <param name="Write">The nodes to write — all of them on an open mesh.</param>
    /// <param name="WithheldPaths">The NodeType rows withheld — empty on an open mesh.</param>
    internal sealed record Split(MeshNode[] Write, ImmutableSortedSet<string> WithheldPaths);

    /// <summary>
    /// Splits <paramref name="nodes"/> into what this host writes and the NodeType rows a closed
    /// type set withholds. On an open mesh everything is written.
    /// </summary>
    /// <param name="hub">The installing hub.</param>
    /// <param name="nodes">The parsed package nodes.</param>
    /// <param name="packageId">The package, for the log line.</param>
    /// <param name="logger">Diagnostics.</param>
    internal static Split Withhold(
        IMessageHub hub, IReadOnlyList<MeshNode> nodes, string packageId, ILogger? logger)
    {
        var none = ImmutableSortedSet<string>.Empty.WithComparer(StringComparer.Ordinal);
        if (!hub.ServiceProvider.IsClosedTypeSet())
            return new Split(nodes.ToArray(), none);
        var withheld = nodes.Where(IsNodeTypeRow).Select(n => n.Path)
            .ToImmutableSortedSet(StringComparer.Ordinal);
        if (withheld.Count == 0)
            return new Split(nodes.ToArray(), none);
        logger?.LogInformation(
            "[PackageInstaller] {Id}: {Key}=true — withholding {Count} NodeType definition row(s) this "
            + "host can never serve; the package's other {Rest} node(s) install as usual: [{Paths}]",
            packageId, ClosedTypeSet.ConfigKey, withheld.Count, nodes.Count - withheld.Count,
            string.Join(", ", withheld));
        return new Split(nodes.Where(n => !IsNodeTypeRow(n)).ToArray(), withheld);
    }

    /// <summary>The outcome of <see cref="Reconcile"/>.</summary>
    /// <param name="WithheldPaths">Every candidate this host withholds: the rows the install itself
    /// withheld plus every stored NodeType row found among the candidates.</param>
    /// <param name="ReadPaths">The candidates whose stored row was READ — the population the
    /// withheld answer covers. <c>null</c> when storage could not be read (nothing was learned).</param>
    /// <param name="Removed">The rows actually deleted.</param>
    internal sealed record Outcome(
        ImmutableSortedSet<string> WithheldPaths,
        ImmutableSortedSet<string>? ReadPaths,
        ImmutableList<string> Removed);

    /// <summary>
    /// On a closed type set: reads <paramref name="candidatePaths"/> in one batch, and deletes every
    /// stored NodeType row among them — the rows earlier installs on this host left behind. On an
    /// open mesh it reads and deletes nothing.
    /// </summary>
    /// <param name="hub">The installing hub.</param>
    /// <param name="packageId">The package, for the log lines.</param>
    /// <param name="candidatePaths">The node paths the package declares.</param>
    /// <param name="knownWithheld">The rows the install withheld from its own write set.</param>
    /// <param name="logger">Diagnostics.</param>
    /// <returns>A cold observable emitting the outcome once.</returns>
    internal static IObservable<Outcome> Reconcile(
        IMessageHub hub, string packageId, IReadOnlyCollection<string> candidatePaths,
        IReadOnlyCollection<string> knownWithheld, ILogger? logger)
    {
        var known = knownWithheld.ToImmutableSortedSet(StringComparer.Ordinal);
        var persistence = hub.ServiceProvider.GetService<IStorageAdapter>();
        var candidates = candidatePaths.Concat(known)
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Distinct(StringComparer.Ordinal).ToArray();
        if (!hub.ServiceProvider.IsClosedTypeSet() || persistence is null || candidates.Length == 0)
            return Observable.Return(new Outcome(known, null, ImmutableList<string>.Empty));

        var options = hub.JsonSerializerOptions;
        var access = hub.ServiceProvider.GetRequiredService<AccessService>();
        return Observable.Defer(() => persistence.ReadMany(candidates, options))
            .ToList()
            .Select(rows => (IList<MeshNode>?)rows)
            .Catch<IList<MeshNode>?, Exception>(ex =>
            {
                logger?.LogWarning(ex,
                    "[PackageInstaller] {Id}: reading the declared nodes to remove the NodeType rows a "
                    + "closed type set does not hold FAILED — none was removed; the next install or boot "
                    + "repair pass tries again", packageId);
                return Observable.Return<IList<MeshNode>?>(null);
            })
            .SelectMany(rows =>
            {
                if (rows is null)
                    return Observable.Return(new Outcome(known, null, ImmutableList<string>.Empty));
                var typeRows = rows.Where(IsNodeTypeRow).ToArray();
                var withheld = known.Union(typeRows.Select(r => r.Path));
                var read = rows.Select(r => r.Path).ToImmutableSortedSet(StringComparer.Ordinal);
                var claimed = typeRows.Where(r => r.SyncBehavior != SyncBehavior.Include).ToArray();
                if (claimed.Length > 0)
                    logger?.LogWarning(
                        "[PackageInstaller] {Id}: {Count} NodeType row(s) on this closed type set are "
                        + "CLAIMED (not the package's to remove) and stay — each settles at the closed-set "
                        + "refusal until its owner removes it: [{Paths}]",
                        packageId, claimed.Length, string.Join(", ", claimed.Select(r => r.Path)));
                var toDelete = typeRows.Where(r => r.SyncBehavior == SyncBehavior.Include).ToArray();
                if (toDelete.Length == 0)
                    return Observable.Return(new Outcome(withheld, read, ImmutableList<string>.Empty));
                return toDelete
                    .Select(row => DeleteRowOnly(hub, access, row.Path, row.Version)
                        .Select(removed => removed ? row.Path : null)
                        .Catch<string?, Exception>(ex =>
                        {
                            logger?.LogWarning(ex,
                                "[PackageInstaller] {Id}: removing the NodeType row {Path} a closed type "
                                + "set does not hold FAILED — it keeps reading as the closed-set refusal "
                                + "until the next install or boot repair pass", packageId, row.Path);
                            return Observable.Return<string?>(null);
                        }))
                    .ToObservable().Concat().ToList()
                    .Select(deleted =>
                    {
                        var removed = deleted.Where(d => d is not null).Select(d => d!).ToImmutableList();
                        logger?.LogInformation(
                            "[PackageInstaller] {Id}: {Key}=true — removed {Removed} NodeType definition "
                            + "row(s) an earlier install left on this host: [{Paths}]",
                            packageId, ClosedTypeSet.ConfigKey, removed.Count, string.Join(", ", removed));
                        return new Outcome(withheld, read, removed);
                    });
            });
    }

    /// <summary>
    /// ONE framework delete of the row at <paramref name="path"/> and nothing under it
    /// (<see cref="DeleteNodeRequest.KeepDescendants"/>), as System. It goes through the node
    /// lifecycle — delete validators, the <c>Deleted</c> change event, the stream-cache
    /// invalidation and the disposal of a live activation at the path — never a storage write.
    /// True = removed, false = already gone; errors on a refusal. Cold.
    ///
    /// <para>🚨 <b>Version-guarded.</b> The row is classified (an unclaimed NodeType row) from the
    /// batch read; <see cref="DeleteNodeRequest.ExpectedVersion"/> carries the version that read
    /// saw, so a row CLAIMED (or otherwise rewritten) after the read answers <c>Conflict</c> and is
    /// kept — the decision is never applied to a node it was not made about.</para>
    /// </summary>
    /// <param name="hub">The installing hub.</param>
    /// <param name="access">The access service that supplies the System identity.</param>
    /// <param name="path">The NodeType row to remove.</param>
    /// <param name="readVersion">The version the classifying read saw.</param>
    private static IObservable<bool> DeleteRowOnly(
        IMessageHub hub, AccessService access, string path, long readVersion) =>
        access.RunAsSystem(() => Observable.Defer(() => hub.GetMeshHub().NodeOperationIssuingHub()
            .Observe(new DeleteNodeRequest(path)
                {
                    KeepDescendants = true,
                    ExpectedVersion = readVersion,
                    DeletedBy = WellKnownUsers.System,
                },
                o => o.WithTarget(hub.NodeOperationTarget()))
            .Take(1)
            .SelectMany(delivery => delivery.Message.Success
                ? Observable.Return(!delivery.Message.AlreadyAbsent)
                : Observable.Throw<bool>(new InvalidOperationException(
                    $"{delivery.Message.RejectionReason}: {delivery.Message.Error ?? "the delete was refused"}")))));

    /// <summary>The node paths <paramref name="files"/> declare (non-node files dropped).</summary>
    /// <param name="files">Package-relative file paths.</param>
    /// <param name="parsers">The parser registry the install uses.</param>
    internal static ImmutableSortedSet<string> NodePathsOf(
        IEnumerable<string>? files, FileFormatParserRegistry parsers) =>
        (files ?? [])
            .Select(f => PackageInstaller.NodePathForFile(f, parsers))
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => p!)
            .ToImmutableSortedSet(StringComparer.Ordinal);

    /// <summary>The files among <paramref name="files"/> whose node is in <paramref name="paths"/>.</summary>
    /// <param name="files">Package-relative file paths.</param>
    /// <param name="paths">Node paths.</param>
    /// <param name="parsers">The parser registry the install uses.</param>
    internal static ImmutableSortedSet<string> FilesOf(
        IEnumerable<string>? files, IReadOnlyCollection<string> paths, FileFormatParserRegistry parsers)
    {
        var set = paths.ToImmutableHashSet(StringComparer.Ordinal);
        return (files ?? [])
            .Where(f => PackageInstaller.NodePathForFile(f, parsers) is { } p && set.Contains(p))
            .ToImmutableSortedSet(StringComparer.Ordinal);
    }

    /// <summary>
    /// What a <see cref="Reconcile"/> outcome tells the install record: the declared files it
    /// classified, and those of them that are withheld. Both <c>null</c> when nothing was learned.
    /// </summary>
    /// <param name="outcome">The reconcile outcome.</param>
    /// <param name="declaredFiles">The file map the record is stamped with.</param>
    /// <param name="parsers">The parser registry the install uses.</param>
    internal static (IReadOnlyCollection<string>? Examined, IReadOnlyCollection<string>? Withheld) RecordInputs(
        Outcome outcome, IEnumerable<string>? declaredFiles, FileFormatParserRegistry parsers)
    {
        if (declaredFiles is null || (outcome.ReadPaths is null && outcome.WithheldPaths.Count == 0))
            return (null, null);
        var files = declaredFiles.ToArray();
        var examinedPaths = (outcome.ReadPaths ?? ImmutableSortedSet<string>.Empty)
            .Union(outcome.WithheldPaths);
        return (FilesOf(files, examinedPaths, parsers), FilesOf(files, outcome.WithheldPaths, parsers));
    }

    /// <summary>
    /// The record's <see cref="PackageManifest.WithheldFiles"/> after this write. <c>null</c> on an
    /// open mesh (nothing is withheld there). On a closed type set: the previous answer, minus the
    /// files this pass looked at, plus the ones it found withheld — restricted to the files the
    /// record now declares. An unexamined previous entry carries forward; a file never examined is
    /// not claimed withheld, so a wrong "not withheld" surfaces as a completeness shortfall the heal
    /// repairs with a full install, never as a silently hidden node.
    /// </summary>
    /// <param name="closedTypeSet">Whether this host serves a closed type set.</param>
    /// <param name="previous">The record's previous answer.</param>
    /// <param name="examined">The files this pass classified.</param>
    /// <param name="withheldNow">Those of <paramref name="examined"/> that are withheld.</param>
    /// <param name="declaredNow">The file map the record is stamped with.</param>
    internal static ImmutableSortedSet<string>? MergeWithheldFiles(
        bool closedTypeSet,
        ImmutableSortedSet<string>? previous,
        IReadOnlyCollection<string>? examined,
        IReadOnlyCollection<string>? withheldNow,
        IReadOnlyCollection<string>? declaredNow)
    {
        if (!closedTypeSet)
            return null;
        var seen = (examined ?? []).ToImmutableHashSet(StringComparer.Ordinal);
        var merged = (previous ?? ImmutableSortedSet<string>.Empty)
            .Where(f => !seen.Contains(f))
            .Concat(withheldNow ?? [])
            .ToImmutableSortedSet(StringComparer.Ordinal);
        if (declaredNow is not null)
        {
            var declared = declaredNow.ToImmutableHashSet(StringComparer.Ordinal);
            merged = merged.Where(declared.Contains).ToImmutableSortedSet(StringComparer.Ordinal);
        }
        return merged.Count == 0 && previous is null ? null : merged;
    }
}
