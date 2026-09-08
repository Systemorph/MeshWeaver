using System.Collections.Immutable;
using System.Reactive.Linq;
using System.Text.Json;
using MeshWeaver.Hosting.Persistence.Parsers;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;

namespace MeshWeaver.PluginCatalog;

/// <summary>
/// What an install RECORD says landed, compared against what is actually in the mesh.
///
/// <para>🚨 <b>Why this exists.</b> Until MeshWeaver#3485 nothing in the platform ever compared the
/// two. The installer's own numbers — <c>InstallResult(Total, Written)</c> and the record's
/// <c>InstalledNodeCount</c> — count what the installer DECIDED to write; they are never read back,
/// and <c>InstalledNodeCount</c> had no reader at all. The up-to-date gate in
/// <see cref="CatalogLayoutAreas"/> compared two content HASHES (<c>record.ModuleVersion</c> vs the
/// catalogue's), which is a statement about the SOURCE, not about the mesh. So a node that went
/// missing after an install was invisible to every instrument the platform had, and the remedy an
/// operator reaches for first — reinstall — returned <c>InstallResult(0, 0)</c> without fetching a
/// file.</para>
///
/// <para>Measured on <c>memex.systemorph.com</c>, 2026-09-07, eleven days after the loss:
/// <c>Plugins/Feedback</c> declares 14 files (13 of them nodes) and the <c>Feedback</c> partition
/// holds 10 of them — <c>Feedback/Guide</c> and <c>Feedback/Feedback/Source/FeedbackContent</c> are
/// absent. The missing source node was the proximate cause of the 2026-09-06 outage (#3472), and
/// nothing had named it in eleven days.</para>
///
/// <para>🚨 <b>The rule this type is built on.</b> A check that answers a BOOLEAN about something it
/// had to READ must never spell "the read failed" the same way it spells a real negative. So
/// <see cref="InstallCompletenessKind"/> has FIVE values and only ONE of them is a pass: a record
/// that declares no file map (<see cref="InstallCompletenessKind.Undeclared"/>) and a mesh that
/// could not be read (<see cref="InstallCompletenessKind.NotObserved"/>) are NOT
/// <see cref="InstallCompletenessKind.Complete"/>, and <see cref="InstallCompletenessVerdict.IsComplete"/>
/// is false for both.</para>
/// </summary>
public static class InstallCompleteness
{
    /// <summary>
    /// The node paths an install record DECLARES must be present, derived from the per-file hash map
    /// the installer stamped on it (<see cref="PackageManifest.InstalledFiles"/>) through
    /// <see cref="PackageInstaller.NodePathForFile(string, FileFormatParserRegistry)"/> — the
    /// installer's OWN file→node rule, not a second implementation of part of it.
    ///
    /// <para>🚨 <b>The population is the whole point, and getting it wrong is #3659.</b> This used
    /// to apply only the by-design exclusions (<c>README.md</c>, the <c>manifest.lock</c> sidecar,
    /// <c>content/**</c>) while the installer ALSO skipped every file whose extension no registered
    /// parser claims. So a package's ordinary carry-along files counted as nodes the install owed
    /// the mesh: <c>Chess</c> ships <c>Chess/gui/rn/chess.tsx</c>, nothing ever wrote it, and the
    /// sweep reported <c>Chess/gui/rn/chess</c> ABSENT at Error on every pod boot — indefinitely,
    /// and spelled exactly like the genuinely lost source node the sweep exists to find. A count
    /// over the wrong population reads exactly like a correct one, which is why
    /// <see cref="InstallCompletenessVerdict.DeclaredFiles"/> and
    /// <see cref="InstallCompletenessVerdict.NonNodeFiles"/> now travel with every verdict and are
    /// printed: a wrong population is visible rather than silent.</para>
    /// </summary>
    /// <param name="record">The install record's manifest, as stamped by the installer.</param>
    /// <param name="parsers">The parser registry the INSTALL would use. Required — the claimed
    /// extensions are DI-dependent, so a hard-coded list here would be exactly the second
    /// implementation this parameter exists to remove.</param>
    /// <returns>The declared node paths, ordinal-sorted and distinct; empty when nothing is declared.</returns>
    public static ImmutableSortedSet<string> DeclaredNodePaths(
        PackageManifest? record, FileFormatParserRegistry parsers)
    {
        ArgumentNullException.ThrowIfNull(parsers);
        if (record?.InstalledFiles is not { Count: > 0 } files)
            return ImmutableSortedSet<string>.Empty.WithComparer(StringComparer.Ordinal);
        return files.Keys
            .Select(f => PackageInstaller.NodePathForFile(f, parsers))
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => p!)
            .ToImmutableSortedSet(StringComparer.Ordinal);
    }

    /// <summary>
    /// <see cref="DeclaredNodePaths(PackageManifest, FileFormatParserRegistry)"/> against the
    /// BUILT-IN parser set alone.
    ///
    /// <para>🚨 A caller with a hub must pass that hub's registry: which extensions become nodes is
    /// DI-dependent, and this overload cannot see a module's contributed parser. It exists so the
    /// public surface of this change is purely ADDITIVE — removing a public member reds a plugin
    /// repo's trunk on pull requests that did not make the change (#2689) — and every caller inside
    /// the install and completeness paths passes a real registry.</para>
    /// </summary>
    /// <param name="record">The install record's manifest, as stamped by the installer.</param>
    public static ImmutableSortedSet<string> DeclaredNodePaths(PackageManifest? record) =>
        DeclaredNodePaths(record, PackageInstaller.BuiltInParsers);

    /// <summary>
    /// The verdict, computed purely — no mesh, no hub, so the falsification tests can drive every
    /// arm offline.
    /// </summary>
    /// <param name="packageId">The package the record belongs to.</param>
    /// <param name="partition">The partition the package installed into.</param>
    /// <param name="record">The install record's manifest, or null when no record exists.</param>
    /// <param name="present">
    /// The node paths OBSERVED in the mesh. 🚨 <c>null</c> means the read did not happen or failed —
    /// which yields <see cref="InstallCompletenessKind.NotObserved"/>, never a pass and never a
    /// shortfall. An empty (but non-null) set is a real observation of an empty partition.
    /// </param>
    public static InstallCompletenessVerdict Compare(
        string packageId,
        string partition,
        PackageManifest? record,
        IReadOnlySet<string>? present,
        FileFormatParserRegistry parsers)
    {
        ArgumentNullException.ThrowIfNull(parsers);
        if (record is null)
            return new InstallCompletenessVerdict(
                packageId, partition, InstallCompletenessKind.Undeclared, 0, 0,
                ImmutableSortedSet<string>.Empty.WithComparer(StringComparer.Ordinal),
                "no install record exists, so nothing declares what this partition should hold");

        var declared = DeclaredNodePaths(record, parsers);
        // 🚨 The POPULATION, carried on every verdict (#3659): how many files the record holds and
        // how many of them are not node candidates at all. Without these two the count of "declared
        // nodes" is a bare number that reads identically whether it was taken over the right set or
        // the wrong one — which is exactly how a `.tsx` asset was reported as a missing node on
        // every boot for as long as the sweep existed.
        var declaredFiles = record.InstalledFiles?.Count ?? 0;
        var nonNodeFiles = declaredFiles - (record.InstalledFiles?.Keys
            .Count(f => PackageInstaller.NodePathForFile(f, parsers) is not null) ?? 0);
        InstallCompletenessVerdict WithPopulation(InstallCompletenessVerdict verdict) =>
            verdict with { DeclaredFiles = declaredFiles, NonNodeFiles = nonNodeFiles };
        if (declared.Count == 0)
            return WithPopulation(new InstallCompletenessVerdict(
                packageId, partition, InstallCompletenessKind.Undeclared, 0, 0,
                ImmutableSortedSet<string>.Empty.WithComparer(StringComparer.Ordinal),
                declaredFiles == 0
                    ? "the install record carries no file map (installedFiles), so what it should "
                      + "hold is not declared anywhere — it was installed before the file map was "
                      + "stamped, or by a lane that does not stamp one. The next real install "
                      + "writes one."
                    : $"all {declaredFiles} file(s) the record declares are non-node files (a "
                      + "README, the manifest sidecar, a content/** asset, or an extension no "
                      + "parser claims), so this package declares no node to compare against"));

        // 🚨 A file map that does not map ONTO this partition cannot be compared against it, and
        // guessing a rebase would manufacture a shortfall out of a naming difference. Say so
        // instead — an unverifiable install is not a verified one.
        var offPartition = declared
            .Where(p => !IsUnder(p, partition))
            .ToImmutableSortedSet(StringComparer.Ordinal);
        if (offPartition.Count > 0)
            return WithPopulation(new InstallCompletenessVerdict(
                packageId, partition, InstallCompletenessKind.Undeclared, declared.Count, 0,
                offPartition,
                $"{offPartition.Count} of {declared.Count} declared file(s) map outside the target "
                + $"partition '{partition}' (e.g. '{offPartition[0]}'), so the record cannot be "
                + "compared against it"));

        if (present is null)
            return WithPopulation(new InstallCompletenessVerdict(
                packageId, partition, InstallCompletenessKind.NotObserved, declared.Count, 0,
                ImmutableSortedSet<string>.Empty.WithComparer(StringComparer.Ordinal),
                "the mesh could not be read, so completeness was NOT checked — this is not a pass"));

        var missing = declared
            .Where(p => !present.Contains(p))
            .ToImmutableSortedSet(StringComparer.Ordinal);
        var found = declared.Count - missing.Count;
        return WithPopulation(missing.Count == 0
            ? new InstallCompletenessVerdict(
                packageId, partition, InstallCompletenessKind.Complete, declared.Count, found,
                ImmutableSortedSet<string>.Empty.WithComparer(StringComparer.Ordinal),
                "every declared node is present")
            : new InstallCompletenessVerdict(
                packageId, partition, InstallCompletenessKind.Incomplete, declared.Count, found,
                missing,
                $"{missing.Count} of {declared.Count} declared node(s) are ABSENT from the mesh"));
    }

    /// <summary>
    /// <see cref="Compare(string, string, PackageManifest, IReadOnlySet{string}, FileFormatParserRegistry)"/>
    /// against the BUILT-IN parser set alone — see the remarks on
    /// <see cref="DeclaredNodePaths(PackageManifest)"/> for why this overload exists and when it is
    /// the wrong one to call.
    /// </summary>
    /// <param name="packageId">The package the record belongs to.</param>
    /// <param name="partition">The partition the package installed into.</param>
    /// <param name="record">The install record's manifest, or null when no record exists.</param>
    /// <param name="present">The node paths OBSERVED in the mesh; null means the read did not
    /// happen.</param>
    public static InstallCompletenessVerdict Compare(
        string packageId,
        string partition,
        PackageManifest? record,
        IReadOnlySet<string>? present) =>
        Compare(packageId, partition, record, present, PackageInstaller.BuiltInParsers);

    /// <summary>
    /// The reactive half: read back exactly the paths the record declares and compare.
    ///
    /// <para>The read is ONE batched <see cref="IStorageAdapter.ReadMany"/> over a bounded, KNOWN
    /// set — never a query (eventually consistent, and a stale negative here would manufacture a
    /// shortfall) and never N point reads of possibly-absent paths (which is what opens a storm
    /// breaker on the owning hub). A read that faults yields
    /// <see cref="InstallCompletenessKind.NotObserved"/>.</para>
    /// </summary>
    /// <param name="persistence">The storage adapter; <c>null</c> yields <c>NotObserved</c>.</param>
    /// <param name="options">Serializer options for the read.</param>
    /// <param name="packageId">The package id.</param>
    /// <param name="partition">The package's target partition.</param>
    /// <param name="record">The install record's manifest.</param>
    /// <returns>A cold observable emitting exactly one verdict. Subscribe to run.</returns>
    public static IObservable<InstallCompletenessVerdict> Observe(
        IStorageAdapter? persistence,
        JsonSerializerOptions options,
        string packageId,
        string partition,
        PackageManifest? record,
        FileFormatParserRegistry parsers)
    {
        ArgumentNullException.ThrowIfNull(parsers);
        var declared = DeclaredNodePaths(record, parsers);
        var files = record?.InstalledFiles?.Count ?? 0;
        var nonNode = files - (record?.InstalledFiles?.Keys
            .Count(f => PackageInstaller.NodePathForFile(f, parsers) is not null) ?? 0);
        if (persistence is null)
            return Observable.Return(new InstallCompletenessVerdict(
                packageId, partition, InstallCompletenessKind.NotObserved, declared.Count, 0,
                ImmutableSortedSet<string>.Empty.WithComparer(StringComparer.Ordinal),
                "this host registers no storage adapter, so the mesh was NOT read")
            { DeclaredFiles = files, NonNodeFiles = nonNode });
        // Nothing declared ⇒ nothing to read. Compare against an EMPTY observation rather than a
        // null one: the verdict is Undeclared either way, and reading the mesh to learn that would
        // be a round-trip that cannot change the answer.
        if (declared.Count == 0)
            return Observable.Return(
                Compare(packageId, partition, record, ImmutableHashSet<string>.Empty, parsers));

        return persistence.ReadMany(declared, options)
            .Select(n => n.Path)
            .ToList()
            .Select(paths => Compare(packageId, partition, record,
                paths.ToImmutableHashSet(StringComparer.Ordinal), parsers))
            .Catch<InstallCompletenessVerdict, Exception>(ex => Observable.Return(
                new InstallCompletenessVerdict(
                    packageId, partition, InstallCompletenessKind.NotObserved, declared.Count, 0,
                    ImmutableSortedSet<string>.Empty.WithComparer(StringComparer.Ordinal),
                    $"reading the mesh failed, so completeness was NOT checked — this is not a "
                    + $"pass. Cause: {ex.Message}")
                { DeclaredFiles = files, NonNodeFiles = nonNode }));
    }

    /// <summary>
    /// <see cref="Observe(IStorageAdapter, JsonSerializerOptions, string, string, PackageManifest, FileFormatParserRegistry)"/>
    /// against the BUILT-IN parser set alone — see the remarks on
    /// <see cref="DeclaredNodePaths(PackageManifest)"/> for why this overload exists and when it is
    /// the wrong one to call.
    /// </summary>
    /// <param name="persistence">The storage adapter; <c>null</c> yields <c>NotObserved</c>.</param>
    /// <param name="options">Serializer options for the read.</param>
    /// <param name="packageId">The package id.</param>
    /// <param name="partition">The package's target partition.</param>
    /// <param name="record">The install record's manifest.</param>
    public static IObservable<InstallCompletenessVerdict> Observe(
        IStorageAdapter? persistence,
        JsonSerializerOptions options,
        string packageId,
        string partition,
        PackageManifest? record) =>
        Observe(persistence, options, packageId, partition, record, PackageInstaller.BuiltInParsers);

    /// <summary>
    /// The OTHER half of the same question, and the one the record-driven arm cannot ask: a
    /// partition ROOT that exists while NO install record accounts for it.
    ///
    /// <para>🚨 <b>The shape this catches.</b> A node-repo install writes the partition root FIRST,
    /// as a bare <c>Space</c> placeholder with no content, and only stamps the install record at the
    /// very END (<see cref="PackageInstaller"/>'s stage 0 / <c>WriteInstalledRecord</c>). An install
    /// that dies in between therefore leaves a root the portal serves as an ordinary empty space,
    /// with nothing anywhere saying an install was attempted. Measured on
    /// <c>memex.systemorph.com</c> 2026-09-07: FOUR such roots — <c>AgenticPrimerDe</c>,
    /// <c>DataImportExport</c>, <c>DataModeling</c>, <c>ThinkInStreams</c> — all version 1, all
    /// created inside one fifteen-second window on 2026-09-06T10:55Z, none with an install
    /// record.</para>
    ///
    /// <para>The criteria are deliberately narrow, so a legitimately empty space is not diagnosed as
    /// wreckage: a TOP-LEVEL node, typed <c>Space</c>, carrying NO content, with no children other
    /// than <c>_</c>-prefixed satellites, that no install record accounts for. It REPORTS; it never
    /// deletes and never installs — the same discipline
    /// <see cref="InstalledPackageRepairService"/> already applies to a dangling record.</para>
    ///
    /// <para>Cost: one <see cref="IStorageAdapter.ListChildPaths"/> at the root, one batched
    /// <see cref="IStorageAdapter.ReadMany"/> over the top-level names, and one child listing per
    /// SURVIVING candidate — all on paths that are known to exist. A listing that faults yields
    /// nothing rather than a false accusation.</para>
    /// </summary>
    /// <param name="persistence">The storage adapter; <c>null</c> yields an empty sequence.</param>
    /// <param name="options">Serializer options for the read.</param>
    /// <param name="accountedPartitions">Partitions an install record already accounts for.</param>
    /// <returns>A cold observable emitting one verdict per unaccounted root. Subscribe to run.</returns>
    public static IObservable<InstallCompletenessVerdict> ObserveUnaccountedRoots(
        IStorageAdapter? persistence,
        JsonSerializerOptions options,
        IReadOnlySet<string> accountedPartitions)
    {
        // 🚨 No adapter is not "no abandoned roots" — it is "nobody looked". Same rule as the
        // fault path below; an empty sequence here would be a silent zero in the summary.
        if (persistence is null)
            return Observable.Return(NotSwept(
                "this host registers no storage adapter, so the abandoned-root sweep did NOT run"));

        return persistence.ListChildPaths(null)
            .Take(1)
            .Select(level => level.NodePaths
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .Where(p => !p.Contains('/', StringComparison.Ordinal))
                .Where(p => !p.StartsWith('_'))
                .Where(p => !accountedPartitions.Contains(p))
                .Distinct(StringComparer.Ordinal)
                .ToImmutableSortedSet(StringComparer.Ordinal))
            .SelectMany(candidates => candidates.Count > MaxUnaccountedRootsToRead
                // 🚨 REFUSE, LOUDLY — never truncate. Every top-level node is a partition, and on a
                // portal with many users that set is dominated by user roots this arm cannot be
                // about. Reading an unbounded number of them in one batch is a cost nobody asked
                // for at boot; silently reading the first N would be worse, because the roots it
                // skipped would be spelled exactly like roots that are fine. So it emits ONE
                // verdict saying the arm did not run and what the number was.
                ? Observable.Return(NotSwept(
                    $"{candidates.Count} top-level partition(s) are unaccounted for, above the "
                    + $"{MaxUnaccountedRootsToRead} this arm reads in one batch — so the "
                    + "abandoned-root sweep did NOT run this boot. This is not a clean result; it "
                    + "is an absent one."))
                : ObserveCandidateRoots(persistence, options, candidates))
            // 🚨 A FAULT MUST NOT FOLD INTO AN EMPTY SEQUENCE. Returning `Observable.Empty` here
            // would make "the abandoned-root sweep could not run" produce the same zero in the
            // summary as "there are no abandoned roots" — the exact `not checked reads as clean`
            // failure this whole type exists to remove, recreated inside it. Emit the absence
            // instead, so the sweep's denominator carries it.
            .Catch((Exception ex) => Observable.Return(NotSwept(
                $"the abandoned-root sweep FAILED and did not run: {ex.Message}")));
    }

    /// <summary>The whole-arm "this did not run" verdict — an absence, never a zero.</summary>
    private static InstallCompletenessVerdict NotSwept(string because) =>
        new("*", "*", InstallCompletenessKind.NotObserved, 0, 0,
            ImmutableSortedSet<string>.Empty.WithComparer(StringComparer.Ordinal), because);

    /// <summary>
    /// The bound on <see cref="ObserveUnaccountedRoots"/>' one batched read. Every top-level node is
    /// a partition, and a portal's user partitions dominate that set — this arm is about package
    /// roots, and above this many unaccounted ones it declines and says so rather than paying an
    /// unbounded read at boot.
    /// </summary>
    private const int MaxUnaccountedRootsToRead = 2000;

    private static IObservable<InstallCompletenessVerdict> ObserveCandidateRoots(
        IStorageAdapter persistence,
        JsonSerializerOptions options,
        ImmutableSortedSet<string> candidates)
    {
        return (candidates.Count == 0
                ? Observable.Return(ImmutableList<MeshNode>.Empty)
                : persistence.ReadMany(candidates, options)
                    .Where(IsAbandonedInstallRoot)
                    .ToList()
                    .Select(roots => roots.ToImmutableList()))
            .SelectMany(roots => roots.Count == 0
                ? Observable.Empty<InstallCompletenessVerdict>()
                : roots
                    .Select(root => Satellites(persistence, root.Path)
                        .SelectMany(shape => shape switch
                        {
                            // Only satellites ⇒ this is the placeholder shape.
                            SatelliteShape.OnlySatellites => Observable.Return(
                                new InstallCompletenessVerdict(
                                    root.Path, root.Path,
                                    InstallCompletenessKind.RootWithoutRecord, 0, 0,
                                    ImmutableSortedSet<string>.Empty
                                        .WithComparer(StringComparer.Ordinal),
                                    "a partition root exists that NO install record accounts for, "
                                    + "holding no content and nothing but satellites. That is the "
                                    + "shape an install leaves when it writes its root placeholder "
                                    + "and then stops — the portal serves it as an ordinary empty "
                                    + "space (MeshWeaver#3485)")),
                            // 🚨 The listing did not answer. Reporting the root as wreckage would be
                            // a false accusation; reporting NOTHING would spell it exactly like a
                            // root that is fine. So it is an absence, named.
                            SatelliteShape.Unreadable => Observable.Return(
                                new InstallCompletenessVerdict(
                                    root.Path, root.Path,
                                    InstallCompletenessKind.NotObserved, 0, 0,
                                    ImmutableSortedSet<string>.Empty
                                        .WithComparer(StringComparer.Ordinal),
                                    "this root has no install record and no content, but its "
                                    + "children could not be listed — so whether it is an abandoned "
                                    + "install placeholder was NOT determined")),
                            // It has real children: an ordinary partition, nothing to report.
                            _ => Observable.Empty<InstallCompletenessVerdict>(),
                        }))
                    .ToObservable()
                    .Concat());
    }

    /// <summary>The denominator, so a zero cannot hide its cause.</summary>
    /// <param name="verdicts">Every verdict the sweep produced.</param>
    public static InstallCompletenessSummary Summarize(
        IReadOnlyCollection<InstallCompletenessVerdict> verdicts) =>
        new(
            verdicts.Count(v => v.Kind is InstallCompletenessKind.Complete),
            verdicts.Count(v => v.Kind is InstallCompletenessKind.Incomplete),
            verdicts.Count(v => v.Kind is InstallCompletenessKind.Undeclared),
            verdicts.Count(v => v.Kind is InstallCompletenessKind.NotObserved),
            verdicts.Count(v => v.Kind is InstallCompletenessKind.RootWithoutRecord));

    private static bool IsUnder(string nodePath, string partition) =>
        string.Equals(nodePath, partition, StringComparison.Ordinal)
        || nodePath.StartsWith(partition + "/", StringComparison.Ordinal);

    /// <summary>
    /// The installer's stage-0 placeholder shape: a typed-<c>Space</c> root with NO content. The
    /// real root always carries content (a plugin root is <c>Store/Plugin</c> with a
    /// <c>PluginContent</c>), so a content-free <c>Space</c> at the top level is either the
    /// placeholder or an empty space nobody filled — which the satellite test then separates.
    /// </summary>
    private static bool IsAbandonedInstallRoot(MeshNode node) =>
        node.Content is null
        && string.Equals(node.NodeType, "Space", StringComparison.Ordinal)
        && !string.Equals(node.Path, PackageInstaller.InstalledPartition, StringComparison.Ordinal);

    /// <summary>What a candidate root's child listing said — THREE answers, because "it did not
    /// answer" is neither of the other two.</summary>
    private enum SatelliteShape
    {
        /// <summary>Nothing but <c>_</c>-prefixed satellites: the placeholder shape.</summary>
        OnlySatellites,

        /// <summary>Real children: an ordinary partition.</summary>
        HasContentChildren,

        /// <summary>The listing faulted or never emitted — NOT determined either way.</summary>
        Unreadable,
    }

    private static IObservable<SatelliteShape> Satellites(
        IStorageAdapter persistence, string partition) =>
        persistence.ListChildPaths(partition)
            .Take(1)
            .Select(children => children.NodePaths
                .Concat(children.DirectoryPaths)
                .Select(p => p.Split('/').LastOrDefault() ?? "")
                .All(segment => segment.StartsWith('_'))
                ? SatelliteShape.OnlySatellites
                : SatelliteShape.HasContentChildren)
            // 🚨 Neither a fault nor an empty completion is an ANSWER. Folding either into
            // "has children" would drop the root silently — spelling "not checked" exactly like
            // "checked and fine" — and folding it into "only satellites" would accuse a root
            // nobody read. Both become Unreadable, which the caller reports as an absence.
            .Catch<SatelliteShape, Exception>(_ => Observable.Return(SatelliteShape.Unreadable))
            .DefaultIfEmpty(SatelliteShape.Unreadable);
}

/// <summary>
/// What an install-completeness check concluded. 🚨 Only <see cref="Complete"/> is a pass — see the
/// remarks on <see cref="InstallCompleteness"/> for why the other four exist.
/// </summary>
public enum InstallCompletenessKind
{
    /// <summary>Every node the record declares is present in the mesh.</summary>
    Complete,

    /// <summary>At least one declared node is ABSENT. The install is being served partial.</summary>
    Incomplete,

    /// <summary>
    /// Nothing declares what should be here — no record at all, no file map on the record, or a file
    /// map that does not address this partition. NOT a pass: it was not checked.
    /// </summary>
    Undeclared,

    /// <summary>The mesh could not be read. NOT a pass: it was not checked.</summary>
    NotObserved,

    /// <summary>
    /// A partition root exists that no install record accounts for — an install that created its
    /// root and stopped.
    /// </summary>
    RootWithoutRecord,
}

/// <summary>One package's completeness verdict.</summary>
/// <param name="PackageId">The package the record belongs to.</param>
/// <param name="Partition">The partition it installed into.</param>
/// <param name="Kind">The verdict.</param>
/// <param name="Declared">How many node paths the record declares.</param>
/// <param name="Present">How many of them were observed in the mesh.</param>
/// <param name="Missing">The declared paths that are absent (or, for <c>Undeclared</c>, the paths that do not address the partition).</param>
/// <param name="Because">Why, in one sentence, for the log line.</param>
public sealed record InstallCompletenessVerdict(
    string PackageId,
    string Partition,
    InstallCompletenessKind Kind,
    int Declared,
    int Present,
    ImmutableSortedSet<string> Missing,
    string Because)
{
    /// <summary>🚨 True ONLY for <see cref="InstallCompletenessKind.Complete"/>. "Not checked" is not "clean".</summary>
    public bool IsComplete => Kind is InstallCompletenessKind.Complete;

    /// <summary>
    /// How many FILES the install record declares — the population <see cref="Declared"/> was taken
    /// over. 🚨 Carried and printed because a count over the WRONG population reads exactly like a
    /// correct one (#3659): the sweep counted every carry-along asset as a node the install owed
    /// the mesh and reported it absent on every boot, and nothing in the output said which set had
    /// been counted. Zero on a record with no file map, and on a verdict built by a caller
    /// compiled before this field existed — an init-only property, not a positional parameter, so
    /// such a caller keeps binding.
    /// </summary>
    public int DeclaredFiles { get; init; }

    /// <summary>
    /// How many of <see cref="DeclaredFiles"/> are NOT node candidates — a README, the
    /// <c>manifest.lock</c> sidecar, a <c>content/**</c> asset, or a file whose extension no
    /// registered parser claims. <see cref="Declared"/> is the DISTINCT node paths the rest map to,
    /// so <c>DeclaredFiles - NonNodeFiles</c> exceeds it exactly when two files fold onto one node
    /// (the <c>X.json</c> → <c>X/index.json</c> layout move) — which is why all three are printed
    /// rather than two and a subtraction.
    /// </summary>
    public int NonNodeFiles { get; init; }

    /// <summary>What this verdict counted, and over what — one clause, on every line that reports
    /// a verdict, so a wrong population is visible instead of silent.</summary>
    public string Population =>
        $"{DeclaredFiles} file(s) declared, {NonNodeFiles} of them not node files "
        + $"(README/manifest/content assets, or an extension no parser claims) → {Declared} "
        + "distinct node path(s) compared";

    /// <inheritdoc />
    public override string ToString() =>
        $"{PackageId} [{Kind}] {Present}/{Declared} present ({Population})"
        + (Missing.Count == 0 ? "" : $" — missing: {string.Join(", ", Missing.Take(10))}"
                                     + (Missing.Count > 10 ? $" (+{Missing.Count - 10} more)" : ""));
}

/// <summary>The sweep's denominator.</summary>
/// <param name="Complete">Installs verified complete.</param>
/// <param name="Incomplete">Installs missing at least one declared node.</param>
/// <param name="Undeclared">Installs that declare nothing to compare against.</param>
/// <param name="NotObserved">Installs whose mesh state could not be read.</param>
/// <param name="RootWithoutRecord">Partition roots no install record accounts for.</param>
public readonly record struct InstallCompletenessSummary(
    int Complete,
    int Incomplete,
    int Undeclared,
    int NotObserved,
    int RootWithoutRecord)
{
    /// <summary>Every verdict counted.</summary>
    public int Total => Complete + Incomplete + Undeclared + NotObserved + RootWithoutRecord;

    /// <inheritdoc />
    public override string ToString() =>
        $"{Total} checked · {Complete} complete · {Incomplete} INCOMPLETE · "
        + $"{Undeclared} not declared · {NotObserved} not observed · "
        + $"{RootWithoutRecord} root(s) with no record";
}
