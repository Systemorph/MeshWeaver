using System.Collections.Immutable;
using System.Reactive.Linq;
using System.Text.Json;
using MeshWeaver.Hosting.Persistence.Parsers;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using Microsoft.Extensions.Logging;

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
    /// <c>content/**</c>; since #4101 also <c>src/**</c> module sources) while the installer ALSO skipped every file whose extension no registered
    /// parser claims. So a package's ordinary carry-along files counted as nodes the install owed
    /// the mesh: <c>Chess</c> ships <c>Chess/gui/rn/chess.tsx</c>, nothing ever wrote it, and the
    /// sweep reported <c>Chess/gui/rn/chess</c> ABSENT at Error on every pod boot — indefinitely,
    /// and spelled exactly like the genuinely lost source node the sweep exists to find. A count
    /// over the wrong population reads exactly like a correct one, which is why
    /// <see cref="InstallCompletenessVerdict.DeclaredFiles"/> and
    /// <see cref="InstallCompletenessVerdict.NonNodeFiles"/> now travel with every verdict and are
    /// printed: a wrong population is visible rather than silent.</para>
    ///
    /// <para>🚨 <b>And the CONTENT half, which no path can answer</b> (the second half of #3659).
    /// A file whose extension IS claimed can still fail to become a node on its bytes — a
    /// well-formed <c>package.json</c> or <c>tsconfig.json</c> inside a package folder carries no
    /// <c>$type</c>/<c>id</c>/<c>nodeType</c>, so <c>JsonFileParser</c> answers "no node here" and
    /// the installer writes nothing. Deriving the population from paths alone counted it anyway and
    /// reported <c>{Pkg}/tsconfig</c> ABSENT, at Error, on every boot forever — unhealable, because
    /// the same bytes fail the same way on every reinstall, and spelled identically to the genuinely
    /// lost node this sweep exists to find. The installer now RECORDS that answer
    /// (<see cref="PackageManifest.UnreadableFiles"/>) and this reads it, so the two sides cannot
    /// disagree about the content question either. 🚨 <c>null</c> (a record stamped before the
    /// installer recorded it) subtracts NOTHING — unknown is not "checked, none".</para>
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
        var unreadable = record.UnreadableFiles;
        return files.Keys
            .Where(f => unreadable?.Contains(f) != true)
            .Select(f => PackageInstaller.NodePathForFile(f, parsers))
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => p!)
            .ToImmutableSortedSet(StringComparer.Ordinal);
    }

    /// <summary>
    /// The FILES a record's <see cref="PackageManifest.UnreadableFiles"/> still names — the ones
    /// that would otherwise have been reported ABSENT forever, so the sweep can name them under
    /// their OWN sentence instead of dropping them silently (#3659).
    ///
    /// <para>🚨 <b>FILES, with their extensions — never the node paths they would have mapped to</b>
    /// (Copilot review). The remedy for one of these is to move or fix <c>gui/rn/tsconfig.json</c>;
    /// a line naming <c>gui/rn/tsconfig</c> points at nothing on disk, and two files can fold onto
    /// one node path, so the node form can also name fewer things than there are. A report whose
    /// purpose is to be ACTED on has to name the thing the operator acts on.</para>
    ///
    /// <para>🚨 Subtracting them from the declared set is only half the fix. A file a package ships
    /// that cannot become a node IS a fault — the package declares a node that will never exist —
    /// and silently excluding it would trade a wrong Error for a missing one. What changes is WHICH
    /// sentence it gets: a packaging defect nobody can reinstall away, not an absence a reinstall
    /// repairs.</para>
    ///
    /// <para>Two filters, both load-bearing. A file the record no longer DECLARES is gone from the
    /// package and is not reported. And a file that is no longer a node candidate AT ALL under the
    /// registry serving this boot — a module contributed the parser and is not loaded here — is not
    /// reported either: it is an ordinary non-node file today, counted as one, and reporting it as a
    /// packaging defect would accuse a package of shipping something wrong because THIS host is
    /// configured differently.</para>
    /// </summary>
    /// <param name="record">The install record's manifest, as stamped by the installer.</param>
    /// <param name="parsers">The parser registry the INSTALL would use.</param>
    public static ImmutableSortedSet<string> UnreadableFilePaths(
        PackageManifest? record, FileFormatParserRegistry parsers)
    {
        ArgumentNullException.ThrowIfNull(parsers);
        if (record?.UnreadableFiles is not { Count: > 0 } unreadable)
            return ImmutableSortedSet<string>.Empty.WithComparer(StringComparer.Ordinal);
        var declared = record.InstalledFiles;
        return unreadable
            .Where(f => declared is null || declared.ContainsKey(f))
            .Where(f => PackageInstaller.NodePathForFile(f, parsers) is not null)
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
    /// 🚨 <b>What an INCREMENTAL update must add back to its delta</b> — the files a candidate
    /// manifest declares, is not already fetching because their hash did not move, and whose node
    /// is NOT in the mesh.
    ///
    /// <para><b>The defect this closes (MeshWeaver#4259).</b> The incremental path's fetch set is
    /// <c>newManifest.DiffFrom(record.InstalledFiles)</c> — a comparison of two DECLARATIONS, the
    /// candidate's lock against the record the installer itself stamped. Nothing in it observes the
    /// mesh. So a node lost AFTER a previous install is never re-fetched and never re-written: its
    /// hash is unchanged, so it is not in the delta, so it never reaches
    /// <c>PackageInstaller.DecideAndWrite</c> — which would have written it, because it writes
    /// whenever <c>current is null</c>. The presence-awareness exists one layer BELOW a set the
    /// absent file never enters.</para>
    ///
    /// <para>Measured on memex.meshweaver.cloud, 2026-09-14. <c>Plugins/Hosting</c> took the
    /// incremental path at 2026-09-13T22:03Z (module 1.18 → 1.19) and wrote exactly the two files
    /// whose content had moved — <c>Hosting/Deployment/Source/AksOpsResult</c> (v1, 22:02:54.601Z)
    /// and <c>Hosting/Deployment/Source/PlatformBuildInboxWatcher</c> (v16, 22:02:57.255Z). Eleven
    /// other declared files whose hashes had NOT moved were absent from the mesh and stayed absent,
    /// among them <c>Hosting/Deployment/Source/TriageIntake.cs</c> and both
    /// <c>Hosting/TriageStatus/Source/*.cs</c>. Those three had been present that morning — the
    /// release node <c>Hosting/TriageStatus/Release/20260913081235-bylQeIj_</c> names all three in
    /// its <c>sourceVersions</c> and its status is <c>Succeeded</c> — so this is the loss-then-update
    /// shape, not a delivery that never happened.</para>
    ///
    /// <para>🚨 <b>Only the hash-EQUAL path could heal it, and that is the path an update never
    /// takes.</b> MeshWeaver#3485 made the up-to-date exit observe the mesh before skipping, so a
    /// reinstall at an unchanged module hash repairs. But once the hash has moved, every install
    /// takes this path — and each new publication moves it again. A package that loses a node can
    /// therefore stay short across arbitrarily many updates while each one reports success.</para>
    ///
    /// <para>🚨 <b>Three answers, never two.</b> A <c>null</c> <paramref name="presentNodePaths"/>
    /// means the mesh was not read — the batched read faulted, or no storage adapter exists — and
    /// yields EMPTY. Widening the fetch on an unobserved mesh would re-fetch the whole package every
    /// time a read hiccups; refusing to widen is the conservative arm, and the caller is obliged to
    /// say that it could not check rather than proceed as if it had. An empty-but-non-null set is a
    /// real observation of a partition holding none of the declared nodes.</para>
    /// </summary>
    /// <param name="declaredFiles">The CANDIDATE manifest's file map — what the source ships now.
    /// Deliberately not the record's: the fetch may only ask for files the source still has.</param>
    /// <param name="alreadyFetching">The delta's own added/changed files, which the caller is
    /// fetching regardless. Never returned, so the caller can union without de-duplicating.</param>
    /// <param name="presentNodePaths">The node paths OBSERVED in the mesh, or <c>null</c> when the
    /// read did not happen — see the remarks.</param>
    /// <param name="parsers">The parser registry the INSTALL would use; the file→node rule is
    /// DI-dependent (#3659), so a second implementation here would answer differently.</param>
    /// <returns>The declared files to add to the fetch, ordinal-sorted; empty when nothing is
    /// missing, when nothing is declared, or when the mesh was not observed.</returns>
    public static ImmutableSortedSet<string> FilesToRestore(
        IReadOnlyDictionary<string, string>? declaredFiles,
        IReadOnlySet<string>? alreadyFetching,
        IReadOnlySet<string>? presentNodePaths,
        FileFormatParserRegistry parsers) =>
        FilesToRestore(declaredFiles, alreadyFetching, presentNodePaths, parsers, null);

    /// <summary>
    /// <see cref="FilesToRestore(IReadOnlyDictionary{string,string}, IReadOnlySet{string}, IReadOnlySet{string}, FileFormatParserRegistry)"/>,
    /// minus the files the install itself recorded as UNREADABLE (#3659).
    ///
    /// <para>🚨 <b>Why this arm exists</b> (Copilot review). The restore set is "declared, and its
    /// node is not in the mesh" — and a file the installer could not read as a node is
    /// PERMANENTLY in that state. Without this it is re-fetched, re-parsed and re-skipped on
    /// EVERY incremental update, for the life of the package, and the update's own log line names
    /// it as an absent node being restored — a second place where an unhealable case wears an
    /// actionable one's words.</para>
    ///
    /// <para>🚨 <b>And it does not strand a package that FIXES the file.</b> The exclusion applies
    /// only to files whose hash did not move: a changed file is in <paramref name="alreadyFetching"/>
    /// (the delta), which this method never returns anyway, so it still travels, still gets parsed,
    /// and — now readable — is dropped from the record by
    /// <see cref="PackageInstaller.MergeUnreadableFiles"/>. The recorded set can only ever delay a
    /// re-examination that nothing else would have triggered.</para>
    /// </summary>
    /// <param name="declaredFiles">The CANDIDATE manifest's file map.</param>
    /// <param name="alreadyFetching">The delta's own added/changed files; never returned.</param>
    /// <param name="presentNodePaths">The node paths OBSERVED in the mesh, or <c>null</c> when the
    /// read did not happen.</param>
    /// <param name="parsers">The parser registry the INSTALL would use.</param>
    /// <param name="knownUnreadable">The record's <see cref="PackageManifest.UnreadableFiles"/>, or
    /// <c>null</c> when no install has recorded one — in which case nothing is excluded, exactly as
    /// before.</param>
    public static ImmutableSortedSet<string> FilesToRestore(
        IReadOnlyDictionary<string, string>? declaredFiles,
        IReadOnlySet<string>? alreadyFetching,
        IReadOnlySet<string>? presentNodePaths,
        FileFormatParserRegistry parsers,
        IReadOnlySet<string>? knownUnreadable)
    {
        ArgumentNullException.ThrowIfNull(parsers);
        var empty = ImmutableSortedSet<string>.Empty.WithComparer(StringComparer.Ordinal);
        if (presentNodePaths is null || declaredFiles is not { Count: > 0 })
            return empty;
        return declaredFiles.Keys
            .Where(f => alreadyFetching?.Contains(f) != true)
            .Where(f => knownUnreadable?.Contains(f) != true)
            .Select(f => (File: f, Node: PackageInstaller.NodePathForFile(f, parsers)))
            .Where(x => !string.IsNullOrWhiteSpace(x.Node) && !presentNodePaths.Contains(x.Node!))
            .Select(x => x.File)
            .ToImmutableSortedSet(StringComparer.Ordinal);
    }

    /// <summary>
    /// The reactive half of
    /// <see cref="FilesToRestore(IReadOnlyDictionary{string,string}, IReadOnlySet{string}, IReadOnlySet{string}, FileFormatParserRegistry, IReadOnlySet{string})"/>:
    /// ONE batched
    /// <see cref="IStorageAdapter.ReadMany"/> over a bounded, KNOWN set of node paths, answering
    /// which of them the mesh holds.
    ///
    /// <para>🚨 <c>null</c> on a fault or a missing adapter, never an empty set — the same rule
    /// <see cref="Observe(IStorageAdapter, JsonSerializerOptions, string, string, PackageManifest, FileFormatParserRegistry, string)"/>
    /// keeps. An empty set would say "the mesh holds none of them", which on a Postgres read that
    /// faulted (a satellite table that does not exist answers <c>42P01</c>, not "absent") would make
    /// an install re-fetch every file it ships, every time.</para>
    /// </summary>
    /// <param name="persistence">The storage adapter; <c>null</c> yields <c>null</c>.</param>
    /// <param name="options">Serializer options for the read.</param>
    /// <param name="nodePaths">The paths to look for. Empty yields an empty OBSERVATION — there was
    /// nothing to ask, which is a real answer and not a failed read.</param>
    /// <returns>A cold observable emitting exactly once. Subscribe to run.</returns>
    public static IObservable<IReadOnlySet<string>?> ObservePresent(
        IStorageAdapter? persistence,
        JsonSerializerOptions options,
        IReadOnlyCollection<string> nodePaths)
    {
        ArgumentNullException.ThrowIfNull(nodePaths);
        if (persistence is null)
            return Observable.Return<IReadOnlySet<string>?>(null);
        if (nodePaths.Count == 0)
            return Observable.Return<IReadOnlySet<string>?>(
                ImmutableHashSet<string>.Empty.WithComparer(StringComparer.Ordinal));
        // 🚨 Defer is load-bearing, not style. ReadMany is a method CALL: without Defer it runs
        // when this method is called, which is before the Catch below exists — so an adapter that
        // throws SYNCHRONOUSLY escapes past the null/Warning path and the caller falls back to a
        // full package re-fetch on every such fault. Inside Defer, a synchronous throw and an
        // asynchronous OnError reach the same conservative outcome.
        return Observable.Defer(() => persistence.ReadMany(nodePaths, options))
            .Select(n => n.Path)
            .ToList()
            .Select(paths => (IReadOnlySet<string>?)paths.ToImmutableHashSet(StringComparer.Ordinal))
            .Catch<IReadOnlySet<string>?, Exception>(_ =>
                Observable.Return<IReadOnlySet<string>?>(null));
    }

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
        var population = PopulationOf(record, parsers);
        var declaredFiles = population.DeclaredFiles;
        InstallCompletenessVerdict WithPopulation(InstallCompletenessVerdict verdict) =>
            population.Apply(verdict);
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
                      + "README, the manifest sidecar, a content/** asset, a src/** module source, "
                      + "or an extension no parser claims)"
                      + (population.UnreadableFilePaths.Count == 0
                          ? ""
                          : $" or, for {population.UnreadableFilePaths.Count} of them, a claimed "
                            + "extension the install could NOT read as a node")
                      + ", so this package declares no node to compare against"));

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
    ///
    /// <para>🚨 <b>That claim is a property of the CHAIN, not of this call site</b> (#4200) — every
    /// layer has to forward the batch, and one that does not sends dispatch to the interface
    /// DEFAULT, <c>Observable.Merge(paths.Select(Read))</c>, inside itself: precisely the N point
    /// reads this paragraph says are avoided, with everything batched below it unreachable. The
    /// adapter resolved here is <c>SubtreeDeletionGuard → MonotonicWriteGuard → VersionWriting →
    /// PersistenceService</c>, and until this change the facade declared no <c>ReadMany</c> and
    /// <c>VersionWritingStorageAdapter</c> declared none either — so fixing only one of them would
    /// have changed nothing observable here. <c>StorageAdapterDecoratorsForwardBatchReadGuard</c>
    /// now holds every decorator to the forward, which is what makes the sentence above
    /// maintainable rather than merely true today.</para>
    ///
    /// <para>The consequence landed HERE because the two paths do not fail alike: a Postgres point
    /// read CATCHES <c>42P01 undefined_table</c> and answers <c>null</c>, so a partition whose
    /// satellite table was never created was spelled exactly like an absent node and became
    /// <see cref="InstallCompletenessKind.Incomplete"/> plus an ABSENT name, where the batched read
    /// faults and reaches the <c>.Catch</c> below as the honest
    /// <see cref="InstallCompletenessKind.NotObserved"/>.</para>
    /// </summary>
    /// <param name="persistence">The storage adapter; <c>null</c> yields <c>NotObserved</c>.</param>
    /// <param name="options">Serializer options for the read.</param>
    /// <param name="packageId">The package id.</param>
    /// <param name="partition">The package's target partition.</param>
    /// <param name="record">The install record's manifest.</param>
    /// <returns>A cold observable emitting exactly one verdict. Subscribe to run.</returns>
    /// <param name="recordIdentity">
    /// Which record version this verdict is being taken over — <see cref="DescribeRecord"/>.
    /// Optional so the signature stays source-compatible, but a caller that HAS the record node
    /// should always pass it: a declared count nothing can attribute to a record version is the
    /// defect MeshWeaver#4200 cost a day of archaeology to (see
    /// <see cref="InstallCompletenessVerdict.RecordIdentity"/>).
    /// </param>
    public static IObservable<InstallCompletenessVerdict> Observe(
        IStorageAdapter? persistence,
        JsonSerializerOptions options,
        string packageId,
        string partition,
        PackageManifest? record,
        FileFormatParserRegistry parsers,
        string? recordIdentity)
    {
        ArgumentNullException.ThrowIfNull(parsers);
        var declared = DeclaredNodePaths(record, parsers);
        var population = PopulationOf(record, parsers);
        // 🚨 EVERY arm carries it, including the ones that are not a pass: a NotObserved that
        // cannot say which record it failed to verify is as unattributable as an Incomplete that
        // cannot say which record it counted (MeshWeaver#4200).
        InstallCompletenessVerdict Attribute(InstallCompletenessVerdict verdict) =>
            recordIdentity is { Length: > 0 } ? verdict with { RecordIdentity = recordIdentity } : verdict;

        if (persistence is null)
            return Observable.Return(Attribute(population.Apply(new InstallCompletenessVerdict(
                packageId, partition, InstallCompletenessKind.NotObserved, declared.Count, 0,
                ImmutableSortedSet<string>.Empty.WithComparer(StringComparer.Ordinal),
                "this host registers no storage adapter, so the mesh was NOT read"))));
        // Nothing declared ⇒ nothing to read. Compare against an EMPTY observation rather than a
        // null one: the verdict is Undeclared either way, and reading the mesh to learn that would
        // be a round-trip that cannot change the answer.
        if (declared.Count == 0)
            return Observable.Return(Attribute(
                Compare(packageId, partition, record, ImmutableHashSet<string>.Empty, parsers)));

        return persistence.ReadMany(declared, options)
            .Select(n => n.Path)
            .ToList()
            .Select(paths => Attribute(Compare(packageId, partition, record,
                paths.ToImmutableHashSet(StringComparer.Ordinal), parsers)))
            .Catch<InstallCompletenessVerdict, Exception>(ex => Observable.Return(
                Attribute(population.Apply(new InstallCompletenessVerdict(
                    packageId, partition, InstallCompletenessKind.NotObserved, declared.Count, 0,
                    ImmutableSortedSet<string>.Empty.WithComparer(StringComparer.Ordinal),
                    $"reading the mesh failed, so completeness was NOT checked — this is not a "
                    + $"pass. Cause: {ex.Message}")))));
    }

    /// <summary>
    /// One line naming an install record: where it lives, which VERSION of it was read, when that
    /// version was written, and the two stamps that identify the source snapshot behind it.
    ///
    /// <para>Pure, so the sweep's provenance is testable with no mesh — and public, so the caller
    /// that actually reads the node (which is the only thing that knows its version) can build it.</para>
    /// </summary>
    /// <param name="path">The record node's path, e.g. <c>Plugins/Store</c>.</param>
    /// <param name="version">The record node's version as READ — not as it is now.</param>
    /// <param name="lastModified">When that version was written.</param>
    /// <param name="record">The manifest, for the stamps that name the source snapshot.</param>
    public static string DescribeRecord(
        string path, long version, DateTimeOffset lastModified, PackageManifest? record)
    {
        var stamps = record is null
            ? "no manifest"
            : $"version {record.Version ?? "?"}, moduleVersion {record.ModuleVersion ?? "?"}, "
              + $"installedAtUtc {record.InstalledAtUtc?.ToString("O") ?? "?"}, "
              + $"{record.InstalledFiles?.Count ?? 0} file(s) in the map";
        return $"{path} v{version} (written {lastModified:O}; {stamps})";
    }

    /// <summary>
    /// <c>Observe</c> without a record identity.
    ///
    /// <para>🚨 <b>This overload exists for BINARY compatibility and is not redundant.</b> Adding
    /// an optional parameter to the seven-parameter method above would have been source-compatible and silently
    /// binary-BREAKING: the six-parameter metadata signature disappears, and an assembly compiled
    /// against it fails at run time with <c>MissingMethodException</c> — the failure shape that
    /// cannot be seen by any compile in this repository, because nothing here is compiled against
    /// an older core. Two real overloads keep both signatures in the metadata.</para>
    ///
    /// <para>A caller that HAS the record node should use the other one: a declared count nothing
    /// can attribute to a record version is the defect MeshWeaver#4200 cost a day of archaeology
    /// to (see <see cref="InstallCompletenessVerdict.RecordIdentity"/>).</para>
    /// </summary>
    public static IObservable<InstallCompletenessVerdict> Observe(
        IStorageAdapter? persistence,
        JsonSerializerOptions options,
        string packageId,
        string partition,
        PackageManifest? record,
        FileFormatParserRegistry parsers) =>
        Observe(persistence, options, packageId, partition, record, parsers, null);

    /// <summary>
    /// The population a verdict was taken over, computed ONCE per record: how many files the
    /// record declares, how many of them are not node candidates, and — separately — how many of
    /// those are module sources (#4101), so the line can say what the non-node files ARE.
    /// </summary>
    private readonly record struct Population(
        int DeclaredFiles, int NonNodeFiles, int ModuleSourceFiles,
        ImmutableSortedSet<string> ModuleSourceDirectories,
        ImmutableSortedSet<string> UnreadableFilePaths)
    {
        public InstallCompletenessVerdict Apply(InstallCompletenessVerdict verdict) => verdict with
        {
            DeclaredFiles = DeclaredFiles,
            NonNodeFiles = NonNodeFiles,
            ModuleSourceFiles = ModuleSourceFiles,
            ModuleSourceDirectories = ModuleSourceDirectories,
            UnreadableFilePaths = UnreadableFilePaths,
        };
    }

    private static Population PopulationOf(PackageManifest? record, FileFormatParserRegistry parsers)
    {
        var empty = ImmutableSortedSet<string>.Empty.WithComparer(StringComparer.Ordinal);
        if (record?.InstalledFiles is not { Count: > 0 } files)
            return new Population(0, 0, 0, empty, empty);
        // 🚨 THREE buckets, not two. A non-node file is excluded BY DESIGN (a README, the manifest
        // sidecar, a content asset, a module source, an extension no parser claims); an UNREADABLE
        // one is a packaging fault the installer met and recorded (#3659). Folding them together
        // would hide the fault inside a number that reads as routine.
        //
        // 🚨 ORDER MATTERS, and getting it wrong loses a file from the arithmetic entirely (Copilot
        // review). The recorded set is an INSTALL-TIME observation; the registry serving THIS boot
        // may differ — a module contributed the parser and is not loaded here. Asking the recorded
        // set first made such a file neither a non-node (excluded by the record) nor an unreadable
        // one (UnreadableFilePaths drops it, since it is no longer a node candidate), so
        // DeclaredFiles stopped accounting for it. The CURRENT rule is therefore asked first, and
        // the record only classifies files that are still node candidates today.
        var nonNode = files.Keys.Count(f => PackageInstaller.NodePathForFile(f, parsers) is null);
        var sources = files.Keys.Where(PackageInstaller.IsModuleSourcePath).ToList();
        var directories = sources
            .Select(ModuleSourceDirectoryOf)
            .ToImmutableSortedSet(StringComparer.Ordinal);
        return new Population(
            files.Count, nonNode, sources.Count, directories,
            UnreadableFilePaths(record, parsers));
    }

    /// <summary><c>src/X/a/b.cs</c> → <c>src/X</c>: the module directory a declared source
    /// belongs to, for the population line.</summary>
    private static string ModuleSourceDirectoryOf(string relativePath)
    {
        var rest = relativePath[PackageInstaller.ModuleSourcePrefix.Length..];
        var slash = rest.IndexOf('/');
        return slash < 0
            ? relativePath
            : PackageInstaller.ModuleSourcePrefix + rest[..slash];
    }

    /// <summary>
    /// The MODULE half of a mixed package's completeness (#4101): whether the compiled module the
    /// record declares (<see cref="PackageManifest.Module"/>) is active on THIS pod.
    ///
    /// <para>🚨 <b>What this can and cannot answer.</b> The sources the lock declares under
    /// <c>src/&lt;Module&gt;/</c> are compiled into the module bundle by the pack lane; the volume
    /// holds ASSEMBLIES, not sources, so they cannot be checked file-by-file the way node files
    /// are. What CAN be asked is the activation record (<see cref="ModuleLandingService.GetActivation"/>)
    /// and this process's loaded assemblies — the same two the pending-restart surface reads
    /// (<see cref="ModuleActivationStatus"/>). So "active" here means "an enabled entry names it
    /// AND an assembly of that name is loaded in this process"; it is NOT "every source file
    /// arrived", and the wording of every verdict says so. Pure and total: the caller supplies the
    /// list and the loaded set, so the rule is testable with no host.</para>
    /// </summary>
    /// <param name="packageId">The package the record belongs to.</param>
    /// <param name="record">The install record's manifest.</param>
    /// <param name="activation">The persisted activation list, or null when it could not be read
    /// (no landing service on this host, or the read faulted) — which yields
    /// <see cref="ModuleActivationVerdictKind.NotObserved"/>, never a pass.</param>
    /// <param name="loadedAssemblyNames">Assembly SIMPLE names loaded in this process
    /// (<see cref="ModuleActivationStatus.LoadedAssemblyNames()"/> in production).</param>
    /// <param name="baseDirectory">The deployment root the <c>modules/</c> tree lives under
    /// (<see cref="ModuleLandingService.BaseDirectory"/>), so the line can name the directory.</param>
    /// <returns>A verdict, or null when the record declares no module — nothing to ask.</returns>
    public static ModuleActivationVerdict? ModuleActivation(
        string packageId,
        PackageManifest? record,
        ModuleActivationList? activation,
        IReadOnlySet<string> loadedAssemblyNames,
        string? baseDirectory)
    {
        ArgumentNullException.ThrowIfNull(loadedAssemblyNames);
        var module = record?.Module;
        if (string.IsNullOrWhiteSpace(module))
            return null;
        var sources = record!.InstalledFiles?.Keys.Count(PackageInstaller.IsModuleSourcePath) ?? 0;
        var caveat = sources > 0
            ? $"the {sources} source file(s) the record declares under src/ are compiled into the "
              + "module bundle and cannot be checked file-by-file on the volume — 'active' is not "
              + "'every source file arrived'"
            : "the module's sources are compiled into the bundle and cannot be checked file-by-file "
              + "on the volume — 'active' is not 'every source file arrived'";

        if (activation is null)
            return new ModuleActivationVerdict(packageId, module, ModuleActivationVerdictKind.NotObserved,
                null, sources,
                $"the activation record could not be read, so whether module '{module}' is active "
                + $"was NOT checked — this is not a pass; and {caveat}");

        var entry = activation.Entries.LastOrDefault(e =>
            string.Equals(e.Name, module, StringComparison.OrdinalIgnoreCase));
        if (entry is null || !entry.Enabled)
            return new ModuleActivationVerdict(packageId, module, ModuleActivationVerdictKind.NotActive,
                null, sources,
                entry is null
                    ? $"module '{module}' is declared by the install record but has NO activation "
                      + "entry — the bundle never landed on this volume, or its entry was removed"
                    : $"module '{module}' is declared by the install record but its activation entry "
                      + "is DISABLED — it was uninstalled after the record was written");

        var directory = string.IsNullOrWhiteSpace(baseDirectory)
            ? null
            : ModuleLandingService.ModuleDirectoryFor(baseDirectory, module, entry);
        var loaded = loadedAssemblyNames.Contains(module);
        return loaded
            ? new ModuleActivationVerdict(packageId, module, ModuleActivationVerdictKind.Active,
                directory, sources,
                $"module '{module}' active from '{directory ?? entry.Directory ?? module}'; {caveat}")
            : new ModuleActivationVerdict(packageId, module, ModuleActivationVerdictKind.LandedNotLoaded,
                directory, sources,
                $"module '{module}' is landed at '{directory ?? entry.Directory ?? module}' but "
                + "NOT loaded in this process — it activates on this pod's next restart; not a "
                + $"pass; and {caveat}");
    }

    /// <summary>
    /// <see cref="Observe(IStorageAdapter, JsonSerializerOptions, string, string, PackageManifest, FileFormatParserRegistry, string)"/>
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

    /// <summary>How many missing paths one line names before it stops counting them out.</summary>
    internal const int MaxNamedInALine = 20;

    /// <summary>
    /// 🚨 <b>THE SEVERITY BELONGS TO THE OUTCOME, NOT TO THE DETECTION</b> (MeshWeaver#2387).
    ///
    /// <para><b>The line that lies by being early.</b> Until this existed, the only completeness
    /// line an install ever wrote was emitted BEFORE the repair ran — at
    /// <see cref="LogLevel.Error"/>, saying the install "is being REPAIRED rather than skipped" —
    /// and nothing anywhere ever said whether the repair worked. That Error ships to Loki, the log
    /// watcher mints an incident from it, and because incident identity folds per log CATEGORY it
    /// lands on MeshWeaver#2387 — an issue about a different call site in the same class.</para>
    ///
    /// <para>🚨 <b>Landed is not HELD.</b> The verdict this method describes is taken right after
    /// the write, so a writer that undoes the repair LATER is invisible to it. Measured on
    /// <c>memex.meshweaver.cloud</c>: <c>Feedback/Feedback/Source/FeedbackHandover</c> was named
    /// ABSENT at 22:02:37Z on 2026-09-13 and present at 22:02:45Z — read at the time as a repair
    /// that worked — and was named ABSENT again on ten further boots through 2026-09-16 at the same
    /// module version, pruned after each repair by <c>Feedback/_GitSync</c> importing the sealed
    /// commit whose tree lacks it (MeshWeaver#4259). The signature of a repair that did not hold is
    /// the pre-install detection REPEATING at an unchanged module version.</para>
    ///
    /// <para>🚨 <b>And the case that deserved the Error had no line at all.</b> A completeness
    /// verdict was only ever taken on the SKIP path, where the module hash was unchanged; an
    /// install that actually WROTE was never compared against what landed. Same portal, same boot:
    /// <c>Plugins/Hosting</c> stamped a record declaring 224 files at 22:03:03Z, and
    /// <c>Hosting/Deployment/Source/TriageIntake.cs</c> plus both
    /// <c>Hosting/TriageStatus/Source/*.cs</c> were still absent twelve hours later — eight
    /// NodeTypes sitting at <c>compilationStatus: Error</c> with <c>MISSING SOURCES: N of N</c>,
    /// and not one log line saying the install had not landed whole. The record is stamped, so
    /// nothing asks again until the module version moves.</para>
    ///
    /// <para>Pure — the whole publication is this function, so every arm is pinnable without a
    /// host. Same split, and same reason, as <c>NodeTypeBakeStatus.Classify</c>.</para>
    /// </summary>
    /// <param name="after">The verdict taken AFTER the install wrote. 🚨 Never the one before.</param>
    /// <param name="moduleVersion">The module version the install stamped, named on the line.</param>
    /// <returns>The severity the outcome deserves, and the sentence to log at it.</returns>
    public static LandingReport DescribeLanding(
        InstallCompletenessVerdict after, string? moduleVersion)
    {
        ArgumentNullException.ThrowIfNull(after);
        var module = string.IsNullOrEmpty(moduleVersion) ? "(none)" : moduleVersion;
        var ordinary = DescribeVerdict(after, module);
        // 🚨 A file the install could not READ as a node is a fault of its OWN, and it outranks the
        // absence verdict (#3659). It is an Error whatever the rest of the package did — the package
        // declares a node that will never exist — and it is spelled as a PACKAGING defect, because
        // the one thing that cannot fix it is the remedy every other line here names: reinstalling
        // meets the same bytes and fails the same way, forever.
        return after.UnreadableFilePaths.Count == 0
            ? ordinary
            : new LandingReport(LogLevel.Error, UnreadableSentence(after) + " " + ordinary.Message);
    }

    /// <summary>
    /// The one sentence a file the install could not read as a node gets — its own, never an
    /// ABSENT (#3659). Shared by the post-install landing and the boot sweep so the two cannot
    /// describe the same fault differently.
    /// </summary>
    /// <param name="verdict">The verdict carrying <see cref="InstallCompletenessVerdict.UnreadableFilePaths"/>.</param>
    public static string UnreadableSentence(InstallCompletenessVerdict verdict)
    {
        ArgumentNullException.ThrowIfNull(verdict);
        return $"Package {verdict.PackageId} ships {verdict.UnreadableFilePaths.Count} FILE(s) whose "
               + "extension a parser claims but whose CONTENT the install could not read as a node, "
               + "so the node(s) they declare do not exist and no install will create them: "
               + $"[{string.Join(", ", verdict.UnreadableFilePaths.Take(MaxNamedInALine))}]. This is a "
               + "PACKAGING defect, not mesh damage — reinstalling meets the same bytes and fails "
               + "the same way. The usual cause is an ordinary config file inside a package folder "
               + "(a package.json / tsconfig.json carries no $type, id or nodeType, so it is not a "
               + "node): move it out of the package, or give it a shape the parser recognises "
               + "(MeshWeaver#3659).";
    }

    private static LandingReport DescribeVerdict(InstallCompletenessVerdict after, string module)
    {
        return after.Kind switch
        {
            // The one arm that is a genuine fault: the install ran, the record is stamped, and the
            // mesh is still short. NAMED, because "something is missing" is not actionable.
            InstallCompletenessKind.Incomplete => new LandingReport(
                LogLevel.Error,
                $"Package {after.PackageId} finished installing (module {module}) and "
                + $"{after.Missing.Count} of {after.Declared} declared node(s) are STILL ABSENT "
                + $"from the mesh: [{string.Join(", ", after.Missing.Take(MaxNamedInALine))}]. "
                + $"Counted over: {after.Population}. The install record is stamped, so no later "
                + "install or reconcile will ask again until the module version moves — and a "
                + "NodeType whose declared source node is among these cannot compile "
                + "(MeshWeaver#3485)."),
            InstallCompletenessKind.Complete => new LandingReport(
                LogLevel.Information,
                $"Package {after.PackageId} landed whole (module {module}): all {after.Declared} "
                + $"declared node(s) are present. Counted over: {after.Population}."),
            // 🚨 Undeclared and NotObserved are NOT passes and must not be spelled like one — the
            // rule this whole type is built on. They are also not the Error: nothing was shown to
            // be missing, only that nothing was shown at all.
            _ => new LandingReport(
                LogLevel.Warning,
                $"Package {after.PackageId} finished installing (module {module}) but the OUTCOME "
                + $"was NOT verified ({after.Kind}): {after.Because}. This is not a pass — nothing "
                + "here says the install landed whole (MeshWeaver#3485)."),
        };
    }

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

/// <summary>
/// What an install's LANDING deserves to be reported as — the severity, and the sentence — decided
/// by <see cref="InstallCompleteness.DescribeLanding"/> from the verdict taken after the write.
///
/// <para>A record rather than a bare log call so the decision is a VALUE a test can pin: a check
/// whose only output is a side effect can only be tested by observing the side effect, and the
/// thing that went wrong here was the SEVERITY, which no assertion on behaviour would have
/// caught.</para>
/// </summary>
/// <param name="Level">The severity the outcome deserves.</param>
/// <param name="Message">The line to log at it.</param>
public sealed record LandingReport(LogLevel Level, string Message);

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

    /// <summary>
    /// How many of <see cref="NonNodeFiles"/> are module SOURCES (<c>src/&lt;Module&gt;/…</c>,
    /// #4101) — declared by the lock because a source change must move the module version, compiled
    /// into the module bundle by the pack lane, never written as nodes. Counted separately so the
    /// population line says what they are instead of folding 14 sources in with the README.
    /// Init-only, for the same binary-compatibility reason as <see cref="DeclaredFiles"/>.
    /// </summary>
    public int ModuleSourceFiles { get; init; }

    /// <summary>The <c>src/&lt;Module&gt;</c> directories those sources live under, ordinal-sorted;
    /// empty when the record declares none.</summary>
    public ImmutableSortedSet<string> ModuleSourceDirectories { get; init; } =
        ImmutableSortedSet<string>.Empty.WithComparer(StringComparer.Ordinal);

    /// <summary>
    /// The FILES the record's <see cref="PackageManifest.UnreadableFiles"/> still names — files the
    /// install PARSED and could not turn into a node (#3659). Empty both when the package has none
    /// and when the record predates the installer recording them, which is why the number is
    /// printed on every line rather than inferred from its absence.
    ///
    /// <para>🚨 FILES with their extensions, never the node paths they would have mapped to: the
    /// remedy is to move or fix <c>gui/rn/tsconfig.json</c>, and a line naming <c>gui/rn/tsconfig</c>
    /// points at nothing on disk (Copilot review). See <see cref="InstallCompleteness.UnreadableFilePaths"/>.</para>
    ///
    /// <para>🚨 These are NOT in <see cref="Missing"/> and never will be: a reinstall meets the
    /// same bytes and fails the same way, so reporting them as an absence a reinstall repairs is
    /// the false remedy this field exists to stop. They are reported under their own sentence, at
    /// Error, as what they are — a package that declares a node it can never deliver.</para>
    /// </summary>
    public ImmutableSortedSet<string> UnreadableFilePaths { get; init; } =
        ImmutableSortedSet<string>.Empty.WithComparer(StringComparer.Ordinal);

    /// <summary>What this verdict counted, and over what — one clause, on every line that reports
    /// a verdict, so a wrong population is visible instead of silent.</summary>
    /// <summary>
    /// WHICH install record this verdict was taken over — its path, its node VERSION and the
    /// stamps that identify the source snapshot it was written from. <c>null</c> when the caller
    /// did not name one.
    ///
    /// <para>🚨 <b>Why a count needs this</b> (MeshWeaver#4200). A sweep reported
    /// <c>201 file(s) declared</c> and named two of them ABSENT; reconciling that against the
    /// record took a version-by-version read of <c>Plugins/Store</c> plus a commit-by-commit count
    /// of the source repo, and the answer was that the two record versions straddling the sweep
    /// carry a 193-file map that declares neither name. The number was right about SOMETHING and
    /// there was no way to say what. A declared count whose record cannot be named is the same
    /// defect as a missing denominator: it reads identically whether it was taken over the right
    /// record or a stale one.</para>
    ///
    /// <para>🚨 And the record IS the eventually-consistent half. <c>Observe</c> keeps the
    /// OBSERVED side off a query on purpose — "never a query … a stale negative here would
    /// manufacture a shortfall" — while the DECLARED side arrives through a <c>GetQuery</c> whose
    /// answer nothing identifies. Naming it does not make it fresh; it makes a stale one
    /// detectable.</para>
    /// </summary>
    public string? RecordIdentity { get; init; }

    /// <summary>
    /// <see cref="RecordIdentity"/> for a log line — and, when the caller named none, a sentence
    /// that SAYS so rather than a blank. "Not identified" and "identified as X" must never render
    /// alike, for the same reason <see cref="InstallCompletenessKind.NotObserved"/> is not a pass.
    /// </summary>
    public string Provenance =>
        RecordIdentity is { Length: > 0 } identity
            ? identity
            : "the install record was NOT identified, so this count cannot be reconciled against a "
              + "record version";

    public string Population =>
        $"{DeclaredFiles} file(s) declared, {NonNodeFiles} of them not node files "
        + "(README/manifest/content assets, module sources, or an extension no parser claims)"
        + (ModuleSourceFiles == 0
            ? ""
            : $", {ModuleSourceFiles} of them module sources "
              + $"({string.Join(", ", ModuleSourceDirectories)}), compiled into the module bundle, "
              + "not compared as nodes")
        + (UnreadableFilePaths.Count == 0
            ? ""
            : $", {UnreadableFilePaths.Count} of them a claimed extension the install could NOT read as "
              + "a node, recorded by the install itself and reported separately, not as absences")
        + $" → {Declared} distinct node path(s) compared";

    /// <inheritdoc />
    public override string ToString() =>
        $"{PackageId} [{Kind}] {Present}/{Declared} present ({Population})"
        + (Missing.Count == 0 ? "" : $" — missing: {string.Join(", ", Missing.Take(10))}"
                                     + (Missing.Count > 10 ? $" (+{Missing.Count - 10} more)" : ""));
}

/// <summary>
/// What <see cref="InstallCompleteness.ModuleActivation"/> concluded about a mixed package's
/// compiled module on THIS pod. 🚨 Only <see cref="Active"/> is a pass, and even that pass is
/// about the activation record and the loaded assembly — not about the declared sources, which
/// cannot be checked (#4101).
/// </summary>
public enum ModuleActivationVerdictKind
{
    /// <summary>An enabled activation entry names the module and an assembly of that name is
    /// loaded in this process.</summary>
    Active,

    /// <summary>An enabled entry names it but no assembly of that name is loaded here — it
    /// activates on this pod's next restart. NOT a pass.</summary>
    LandedNotLoaded,

    /// <summary>No enabled activation entry names the module. NOT a pass.</summary>
    NotActive,

    /// <summary>The activation record could not be read. NOT a pass: it was not checked.</summary>
    NotObserved,
}

/// <summary>One mixed package's module-activation verdict (#4101).</summary>
/// <param name="PackageId">The package the record belongs to.</param>
/// <param name="Module">The module the record declares (<see cref="PackageManifest.Module"/>).</param>
/// <param name="Kind">The verdict.</param>
/// <param name="Directory">The module directory the activation entry resolves to
/// (<see cref="ModuleLandingService.ModuleDirectoryFor"/>), when an enabled entry exists and the
/// base directory was known.</param>
/// <param name="DeclaredSourceFiles">How many <c>src/**</c> files the record declares — the
/// population this verdict explicitly does NOT check.</param>
/// <param name="Because">Why, in one sentence, for the log line — always naming what was not
/// checked.</param>
public sealed record ModuleActivationVerdict(
    string PackageId,
    string Module,
    ModuleActivationVerdictKind Kind,
    string? Directory,
    int DeclaredSourceFiles,
    string Because)
{
    /// <summary>🚨 True ONLY for <see cref="ModuleActivationVerdictKind.Active"/>.</summary>
    public bool IsActive => Kind is ModuleActivationVerdictKind.Active;
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
