using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using MeshWeaver.Plugin.Packaging;

namespace MeshWeaver.GitSync;

/// <summary>
/// The per-module outcome vocabulary of <see cref="ModuleSyncDecision"/> — OPEN string constants
/// (policy <c>open-vocabulary-string-constants</c>): persisted on the sync config, read by the
/// settings tab and <c>/health</c>, and never switched over exhaustively.
/// </summary>
public static class ModuleSyncOutcomeKind
{
    /// <summary>The module's manifest hash equals the one this Space holds: nothing is written.</summary>
    public const string Unchanged = "Unchanged";

    /// <summary>The module changed (or was never recorded): it syncs to the incoming commit.</summary>
    public const string Synced = "Synced";

    /// <summary>The module declares a platform floor above the running platform: it alone is not
    /// written, and every sibling module still syncs.</summary>
    public const string Declined = "Declined";
}

/// <summary>
/// One module as the INCOMING tree states it: where it lives in the Space, its
/// <c>manifest.lock</c> content hash, and the platform floor it declares.
/// </summary>
/// <param name="Module">The module name (<c>manifest.lock</c>'s <c>module</c>, else its folder).</param>
/// <param name="Root">Space-relative folder the module's <c>manifest.lock</c> sits in — empty when
/// the Space IS the module (its sync subdirectory is the module folder).</param>
/// <param name="ModuleVersion">The manifest's <c>moduleVersion</c> — the content hash — or null when
/// the manifest carries none.</param>
/// <param name="Floor">The module's declared platform floor (<c>content.minMeshVersion</c> of its
/// root <c>index.json</c>), or null when it declares none.</param>
public sealed record ModuleReading(string Module, string Root, string? ModuleVersion, string? Floor)
{
    /// <summary>
    /// The module root's declared package requirements (<c>content.requires</c> of its
    /// <c>index.json</c>, entries shaped <c>AI@^1.21.0</c>) — what
    /// <see cref="ModuleSyncDecision.DeclineUnmetRequirements"/> holds against the loaded
    /// dependencies (MeshWeaver#6067 follow-up). Empty when none are declared. An INIT property: the
    /// record's primary-constructor arity is public surface.
    /// </summary>
    public ImmutableList<string> Requires { get; init; } = [];

    /// <summary>
    /// The module root's floor witness (<c>mesh-floor.lock</c>, <see cref="ModuleFloorWitness"/>), or
    /// null when the tree carries none. An INIT property, for the same reason as <see cref="Requires"/>.
    /// </summary>
    public FloorWitness? Witness { get; init; }

    /// <summary>
    /// The package content hash of the INCOMING sources, recomputed the way the witness's
    /// <c>contentHash</c> was (<see cref="ModuleFloorWitness.ContentHash"/>), or null when it cannot be.
    /// </summary>
    public string? IncomingContentHash { get; init; }

    /// <summary>
    /// 🚨 The <c>moduleVersion</c> the incoming TREE hashes to, recomputed from its own bytes
    /// (<see cref="ModuleFloorWitness.TreeVersion"/>), or null when it cannot be computed. It differs
    /// from <see cref="ModuleVersion"/> exactly while the lock has not been settled for these sources —
    /// the lock is written by a later commit than the one that changes them. An INIT property, for
    /// the same reason as <see cref="Requires"/>.
    /// </summary>
    public string? TreeVersion { get; init; }

    /// <summary>
    /// 🚨 <b>The hash the per-module decision judges and records</b>: what the tree IS
    /// (<see cref="TreeVersion"/>), and only when that cannot be computed what its lock STATES
    /// (<see cref="ModuleVersion"/>). Judging the stated hash alone read a module whose sources had
    /// moved under an unsettled lock as unchanged, recorded the commit as held, and so dropped those
    /// sources from every later diff.
    /// </summary>
    public string? EffectiveVersion => TreeVersion is { Length: > 0 } tree ? tree : ModuleVersion;

    /// <summary>
    /// Whether the lock states the tree it sits in: true when <see cref="ModuleVersion"/> equals
    /// <see cref="TreeVersion"/>, false when the sources moved after the lock was last settled, null
    /// when either hash is missing.
    /// </summary>
    public bool? ManifestIsSettled =>
        TreeVersion is { Length: > 0 } tree && ModuleVersion is { Length: > 0 } stated
            ? string.Equals(tree, stated, StringComparison.Ordinal)
            : null;

    /// <summary>
    /// 🚨 Whether the declared <see cref="Floor"/> is a FACT about these sources: true when the
    /// witness vouches for exactly the incoming content, false when the sources moved after the last
    /// stamp (the floor is the previous sources' floor), null when that cannot be told — no witness, or
    /// a hash that could not be computed — which no rule reads as either answer.
    /// </summary>
    public bool? FloorVerified =>
        Witness?.ContentHash is { Length: > 0 } stamped && IncomingContentHash is { Length: > 0 } incoming
            ? string.Equals(stamped, incoming, StringComparison.Ordinal)
            : null;
}

/// <summary>
/// One module's outcome for one import, as recorded on the sync config
/// (<see cref="GitHubSyncConfig.ModuleOutcomes"/>) and printed by <c>/health</c>.
/// </summary>
/// <param name="Module">The module name.</param>
/// <param name="Outcome">A <see cref="ModuleSyncOutcomeKind"/> value.</param>
/// <param name="HeldVersion">The manifest hash this Space held before the import, or null.</param>
/// <param name="IncomingVersion">The manifest hash the incoming tree carries, or null.</param>
/// <param name="Reason">Log copy: why this outcome.</param>
public sealed record ModuleSyncOutcome(
    string Module, string Outcome, string? HeldVersion, string? IncomingVersion, string Reason)
{
    /// <summary>The Space-relative folder of the module ("" when the Space is the module).</summary>
    public string Root { get; init; } = "";

    /// <summary>The declared floor, when the module was declined on it.</summary>
    public string? Floor { get; init; }

    /// <summary>
    /// The requirement the module was declined on — <c>AI@^1.21.0</c> — when the decline is a
    /// dependency floor rather than a platform floor (MeshWeaver#6067 follow-up); null otherwise.
    /// </summary>
    public string? UnmetRequirement { get; init; }

    /// <summary>
    /// True when the module was declined because its declared floor is NOT verified for the incoming
    /// sources (<see cref="ModuleSyncDecision.HoldUnverifiedFloors"/>) while this instance runs behind
    /// a newer platform (<see cref="AvailablePlatform"/>). <see cref="Floor"/> stays the module's
    /// DECLARED floor, which such a module does not exceed. False otherwise.
    /// </summary>
    public bool FloorUnverified { get; init; }

    /// <summary>The newer platform this instance knew of when it held a module on an unverified floor
    /// (<see cref="FloorUnverified"/>); null otherwise.</summary>
    public string? AvailablePlatform { get; init; }
}

/// <summary>
/// 🚨 <b>Every module syncs, judged ALONE by the content hash in its <c>manifest.lock</c></b> —
/// policy <c>module-sync-per-manifest-hash</c>, coordinated with
/// <c>platform-backwards-compatibility</c>. Pure; the design is
/// <c>Doc/Architecture/ModuleSyncPerManifestHash</c>.
///
/// <para><b>The rule, per module, in order:</b></para>
/// <list type="number">
///   <item><description><b>Declined</b> — the module declares a platform floor above the running
///   platform (<see cref="PlatformFloor.Evaluate"/> — the ONE floor decision every package consumer
///   uses, policy <c>package-min-mesh-version</c>; an unknown, unreadable or unordered version on
///   either side, or a local <c>-dev</c> source build, is accepted, never declined). The ONE per-module decline:
///   the module's paths are neither written nor pruned, the reason names both versions, and it
///   holds NO sibling module.</description></item>
///   <item><description><b>Unchanged</b> — the hash the incoming TREE has
///   (<see cref="ModuleReading.EffectiveVersion"/>: recomputed from its bytes, the lock's stated
///   <c>moduleVersion</c> only when that cannot be done) equals the one this Space recorded when that
///   module last landed, and the import is not a reconcile or a force: nothing is written. 🚨 Never
///   the stated hash alone: the lock is settled by a later commit than the one that moves the
///   sources, and a module read as unchanged in between loses those sources for good — the no-op
///   records the commit as held, so no later diff carries them.</description></item>
///   <item><description><b>Synced</b> — anything else (changed, never recorded, or a manifest with
///   no hash): the module syncs to the incoming commit. Whether each of its NodeTypes then adopts a
///   prebuilt bundle or compiles from the synced source is decided per type, by the bundle's
///   fingerprint and platform range — never by whether the sources arrive.</description></item>
/// </list>
///
/// <para>A tree with no <c>manifest.lock</c> states no module: no outcome is produced and the
/// import runs exactly as it did before (course repos, content repos).</para>
/// </summary>
public static class ModuleSyncDecision
{
    /// <summary>The manifest sidecar's file name (<c>ModuleManifest.FileName</c> in the catalog;
    /// restated here because GitSync is below the catalog in the dependency graph).</summary>
    public const string ManifestFileName = "manifest.lock";

    /// <summary>
    /// Decides every module of an incoming tree.
    /// </summary>
    /// <param name="incoming">The modules the incoming tree carries.</param>
    /// <param name="held">Module → manifest hash this Space recorded when each last landed; null or
    /// empty for a Space that never recorded one.</param>
    /// <param name="runningPlatformVersion">The running platform build, or null when unknown.</param>
    /// <param name="reconcile">True for a reconcile or a force import — the live mesh is measured
    /// against the tree, so no module may be skipped as unchanged.</param>
    /// <returns>One outcome per module, ordinal by module name.</returns>
    public static ImmutableList<ModuleSyncOutcome> Decide(
        IReadOnlyList<ModuleReading> incoming,
        IReadOnlyDictionary<string, string>? held,
        string? runningPlatformVersion,
        bool reconcile)
    {
        ArgumentNullException.ThrowIfNull(incoming);
        return incoming
            .OrderBy(m => m.Module, StringComparer.Ordinal)
            .Select(module => DecideOne(module, held, runningPlatformVersion, reconcile))
            .ToImmutableList();
    }

    private static ModuleSyncOutcome DecideOne(
        ModuleReading module, IReadOnlyDictionary<string, string>? held,
        string? runningPlatformVersion, bool reconcile)
    {
        var heldVersion = held is not null && held.TryGetValue(module.Module, out var h) ? h : null;

        // 🚨 The ONE floor decision every package consumer uses (PlatformFloor — policy
        // package-min-mesh-version): only a floor comparable with the running platform and strictly
        // above it declines; an unreadable or unordered floor, an unknown running version or a
        // local -dev source build proceeds (advisory), which is what keeps an rc or clean floor from
        // holding a ci build (the 2026-09-07 trap).
        var floorVerdict = PlatformFloor.Evaluate(module.Floor, runningPlatformVersion);
        if (floorVerdict.IsHeld)
            return new ModuleSyncOutcome(module.Module, ModuleSyncOutcomeKind.Declined, heldVersion,
                module.EffectiveVersion,
                $"module '{module.Module}' declares platform ≥ {floorVerdict.Floor} but this instance runs "
                + $"{runningPlatformVersion} — it is not written until the platform is rolled forward; "
                + "every other module syncs (policies package-min-mesh-version, "
                + "platform-backwards-compatibility)")
            {
                Root = module.Root,
                Floor = floorVerdict.Floor,
            };

        // 🚨 Judged by what the tree IS, never by what its lock STATES (ModuleReading.EffectiveVersion).
        // The lock is settled by a later commit than the one that changes the sources; judged by the
        // stated hash, a module whose sources had moved read as unchanged, the import was a no-op
        // that recorded the commit as held, and the moved sources were in no later diff.
        var incoming = module.EffectiveVersion;
        if (!reconcile
            && incoming is { Length: > 0 } incomingVersion
            && string.Equals(incomingVersion, heldVersion, StringComparison.Ordinal))
            return new ModuleSyncOutcome(module.Module, ModuleSyncOutcomeKind.Unchanged, heldVersion,
                incomingVersion,
                $"module '{module.Module}' is unchanged at manifest hash {incomingVersion} — nothing written")
            {
                Root = module.Root,
            };

        var unsettled = module.ManifestIsSettled == false
            ? $" (its manifest.lock still states {module.ModuleVersion}: it is not settled for these sources)"
            : "";
        return new ModuleSyncOutcome(module.Module, ModuleSyncOutcomeKind.Synced, heldVersion,
            incoming,
            (reconcile
                ? $"module '{module.Module}' is re-imported (reconcile) at manifest hash {incoming ?? "(none)"}"
                : heldVersion is null
                    ? $"module '{module.Module}' syncs at manifest hash {incoming ?? "(none)"} (no hash recorded before)"
                    : $"module '{module.Module}' changed {heldVersion} → {incoming ?? "(none)"} — it syncs")
            + unsettled)
        {
            Root = module.Root,
        };
    }

    /// <summary>
    /// 🚨 <b>A package's sources never move onto a dependency build that does not meet their declared
    /// floor</b> (MeshWeaver#6067 follow-up). Every <see cref="ModuleSyncOutcomeKind.Synced"/>
    /// outcome whose module declares a requirement (<see cref="ModuleReading.Requires"/>) that the
    /// LOADED dependency (<paramref name="loaded"/>) does not satisfy becomes
    /// <see cref="ModuleSyncOutcomeKind.Declined"/> — the same per-module decline a platform floor
    /// takes, so its paths are neither written nor pruned, its NodeTypes keep serving their last good
    /// build, the baseline stays (its files remain in the next diff), and the reason names the
    /// requirement, the module that loaded and its version.
    ///
    /// <para><b>Measured 2026-10-04 on the control instance.</b> Hosting 1.56 declared
    /// <c>AI@^1.21.0</c>; its sources were imported and Roslyn-compiled at 20:00:50Z while AI 1.20.4
    /// was the loaded build, and every thread start then threw <c>MissingMethodException</c>
    /// (<c>ThreadPreparation.set_Group</c>). The module-set proposal checked the floor
    /// (<c>ModuleDependencyFloor</c>); the import that put the sources in front of the compiler did
    /// not.</para>
    ///
    /// <para><b>Release.</b> Nothing to arm: the next import judges again, and once the restart that
    /// activates a satisfying dependency has happened, the module syncs. A dependency whose loaded
    /// version is unknown (absent from <paramref name="loaded"/>), and a range this rule cannot read
    /// (<see cref="PackageRequirement.Satisfies"/> → null), are NOT judged — the module syncs as it
    /// did before this rule existed. <see cref="ModuleSyncOutcomeKind.Unchanged"/> and an already
    /// <see cref="ModuleSyncOutcomeKind.Declined"/> outcome pass through untouched. Pure.</para>
    /// </summary>
    /// <param name="outcomes">The outcomes <see cref="Decide"/> produced.</param>
    /// <param name="incoming">The readings they were decided from.</param>
    /// <param name="loaded">Package id → the module this process has loaded for it.</param>
    /// <returns>The outcomes, with every unmet requirement declined.</returns>
    public static ImmutableList<ModuleSyncOutcome> DeclineUnmetRequirements(
        IReadOnlyList<ModuleSyncOutcome> outcomes,
        IReadOnlyList<ModuleReading> incoming,
        IReadOnlyDictionary<string, LoadedPackageModule> loaded)
    {
        ArgumentNullException.ThrowIfNull(outcomes);
        ArgumentNullException.ThrowIfNull(incoming);
        ArgumentNullException.ThrowIfNull(loaded);
        return outcomes.Select(outcome =>
            {
                if (outcome.Outcome != ModuleSyncOutcomeKind.Synced)
                    return outcome;
                var reading = incoming.FirstOrDefault(m =>
                    string.Equals(m.Module, outcome.Module, StringComparison.Ordinal)
                    && string.Equals(m.Root, outcome.Root, StringComparison.Ordinal));
                if (reading is null)
                    return outcome;
                foreach (var requirement in reading.Requires)
                {
                    var dependency = PackageRequirement.DependencyId(requirement);
                    if (dependency.Length == 0
                        || !TryLoaded(loaded, dependency, out var module))
                        continue;
                    if (PackageRequirement.Satisfies(PackageRequirement.RangeOf(requirement), module.Version) != false)
                        continue;
                    return outcome with
                    {
                        Outcome = ModuleSyncOutcomeKind.Declined,
                        Reason = $"module '{outcome.Module}' requires {requirement.Trim()}, but this instance "
                                 + $"runs '{module.Module}' (package '{dependency}') at {module.Version}, "
                                 + "which does not satisfy it — its sources are not written and its "
                                 + "NodeTypes keep serving their last good build until a satisfying "
                                 + $"'{dependency}' is loaded (the import after the restart that "
                                 + "activates it syncs this module); every other module syncs "
                                 + "(MeshWeaver#6067)",
                        UnmetRequirement = requirement.Trim(),
                    };
                }
                return outcome;
            })
            .ToImmutableList();

        static bool TryLoaded(
            IReadOnlyDictionary<string, LoadedPackageModule> map, string id,
            [NotNullWhen(true)] out LoadedPackageModule? module)
        {
            if (map.TryGetValue(id, out var exact))
            {
                module = exact;
                return !string.IsNullOrWhiteSpace(exact.Version);
            }
            module = map.FirstOrDefault(kv => string.Equals(kv.Key, id, StringComparison.OrdinalIgnoreCase)).Value;
            return module is { Version.Length: > 0 };
        }
    }

    /// <summary>
    /// Reads every module an incoming tree states: each <c>manifest.lock</c> (at the Space root or
    /// any depth), its <c>moduleVersion</c>, and the declared floor from the module root's
    /// <c>index.json</c> (<c>content.minMeshVersion</c>). Pure and tolerant: an unparsable manifest
    /// still names its module (its folder) with no hash, so it syncs — it is never skipped as
    /// unchanged on a reading that failed. Text-only overload: every file's bytes are its UTF-8 text.
    /// </summary>
    /// <param name="files">The tree's files as (Space-relative path, text content).</param>
    /// <returns>The modules, ordinal by root.</returns>
    public static ImmutableList<ModuleReading> Read(IEnumerable<(string Path, string Content)> files)
    {
        ArgumentNullException.ThrowIfNull(files);
        return Read(files.Select(f => new RepoFile(f.Path, f.Content)));
    }

    /// <summary>
    /// 🚨 <b>A floor nobody stamped for these sources is not a floor — on an instance that is BEHIND
    /// (policy <c>package-min-mesh-version</c>; <c>Doc/Architecture/ModuleSyncPerManifestHash</c>).</b>
    /// Every <see cref="ModuleSyncOutcomeKind.Synced"/> outcome whose reading's declared floor is NOT
    /// verified for the incoming sources (<see cref="ModuleReading.FloorVerified"/> is false — the
    /// witness vouches for different content: the sources moved after the last stamp) becomes
    /// <see cref="ModuleSyncOutcomeKind.Declined"/> when this instance knows a NEWER platform than the
    /// one it runs (<paramref name="newerPlatform"/>). The sources were written against main, which may
    /// need any platform up to that newer one — core API they compile against, or a renderer only a
    /// newer image ships — and the stale floor cannot say which. The declined module keeps serving its
    /// last good build, its baseline stays, and the next import after the stamp or after the roll
    /// judges it again.
    ///
    /// <para><b>Measured 2026-10-09 on memex.systemorph.com</b> (running 3.0.0-ci.10310, 3.0.0-ci.10319
    /// available): MeshWeaver.Plugins@73e5065d carried the approvals inbox on the data-bound row
    /// selection (Plugins#3214) while <c>Hosting</c> still declared <c>3.0.0-ci.10305</c>, the floor of
    /// its previous sources. The import synced it; the 3.0.0-ci.10310 image had no renderer for the
    /// selection, and nothing in the inbox could be selected. The stamp for exactly that content was
    /// <c>3.0.0-ci.10317</c>.</para>
    ///
    /// <para><b>What it does NOT hold</b> (policy <c>sources-sync-on-push</c> — never wait for a green or
    /// sealed build): an instance that runs the newest platform it knows of, or that knows of none
    /// (<paramref name="newerPlatform"/> null), takes every push as before; a verified floor
    /// (<see cref="ModuleReading.FloorVerified"/> true) is judged by <see cref="Decide"/> alone; and a
    /// reading that cannot be verified either way (null — no witness, a hash that could not be
    /// computed) is not judged. Unchanged and already-declined outcomes pass through. Pure.</para>
    /// </summary>
    /// <param name="outcomes">The outcomes <see cref="Decide"/> (and the requirement rule) produced.</param>
    /// <param name="incoming">The readings they were decided from.</param>
    /// <param name="runningPlatformVersion">The platform this instance runs, for the reason.</param>
    /// <param name="newerPlatform">A platform NEWER than the running one that this instance knows is
    /// available, or null when it runs the newest it knows of (or cannot tell).</param>
    /// <returns>The outcomes, with every unverified floor held on a lagging instance.</returns>
    public static ImmutableList<ModuleSyncOutcome> HoldUnverifiedFloors(
        IReadOnlyList<ModuleSyncOutcome> outcomes,
        IReadOnlyList<ModuleReading> incoming,
        string? runningPlatformVersion,
        string? newerPlatform)
    {
        ArgumentNullException.ThrowIfNull(outcomes);
        ArgumentNullException.ThrowIfNull(incoming);
        if (string.IsNullOrWhiteSpace(newerPlatform))
            return [.. outcomes];
        return outcomes.Select(outcome =>
            {
                if (outcome.Outcome != ModuleSyncOutcomeKind.Synced)
                    return outcome;
                var reading = incoming.FirstOrDefault(m =>
                    string.Equals(m.Module, outcome.Module, StringComparison.Ordinal)
                    && string.Equals(m.Root, outcome.Root, StringComparison.Ordinal));
                if (reading is null || reading.FloorVerified != false)
                    return outcome;
                return outcome with
                {
                    Outcome = ModuleSyncOutcomeKind.Declined,
                    Floor = reading.Floor,
                    FloorUnverified = true,
                    AvailablePlatform = newerPlatform,
                    Reason = $"module '{outcome.Module}' changed after its platform floor was last stamped "
                             + $"(it declares {reading.Floor ?? "no floor"}, stamped for content "
                             + $"{reading.Witness?.ContentHash} verified on {reading.Witness?.VerifiedOn ?? "an unknown set"}; "
                             + $"the incoming content is {reading.IncomingContentHash}), so that floor says nothing "
                             + "about these sources — and this instance runs "
                             + $"{runningPlatformVersion ?? "an unknown platform"} while {newerPlatform} is available. "
                             + "Its sources are not written and its NodeTypes keep serving their last good build "
                             + "until the floor is stamped for them or the platform rolls forward; every other "
                             + "module syncs (policy package-min-mesh-version)",
                };
            })
            .ToImmutableList();
    }

    /// <summary>
    /// Reads every module an incoming tree states (see the text overload), and for each one also its
    /// floor witness (<c>mesh-floor.lock</c>) and the content hash of the incoming sources
    /// (<see cref="ModuleFloorWitness.ContentHash"/>) — what <see cref="HoldUnverifiedFloors"/> reads
    /// to tell a stamped floor from a stale one. A binary file contributes its raw bytes.
    /// </summary>
    /// <param name="files">The tree's files, Space-relative.</param>
    /// <returns>The modules, ordinal by root.</returns>
    public static ImmutableList<ModuleReading> Read(IEnumerable<RepoFile> files)
    {
        ArgumentNullException.ThrowIfNull(files);
        var all = files.ToList();
        var byPath = all
            .GroupBy(f => f.Path, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().Content, StringComparer.OrdinalIgnoreCase);
        return all
            .Where(f => IsManifestPath(f.Path))
            .Select(f =>
            {
                var root = f.Path.Length == ManifestFileName.Length
                    ? ""
                    : f.Path[..(f.Path.Length - ManifestFileName.Length - 1)];
                var (module, version) = ParseManifest(f.Content);
                var name = module is { Length: > 0 }
                    ? module
                    : root.Length > 0 ? root[(root.LastIndexOf('/') + 1)..] : "(root)";
                var indexPath = root.Length == 0 ? "index.json" : root + "/index.json";
                var index = byPath.GetValueOrDefault(indexPath);
                var floor = index is { } floorJson ? ParseFloor(floorJson) : null;
                var witnessPath = root.Length == 0
                    ? ModuleFloorWitness.FileName
                    : root + "/" + ModuleFloorWitness.FileName;
                var witness = byPath.GetValueOrDefault(witnessPath) is { } witnessJson
                    ? ModuleFloorWitness.Parse(witnessJson)
                    : null;
                return new ModuleReading(name, root, version, floor)
                {
                    Requires = index is { } requiresJson ? ParseRequires(requiresJson) : [],
                    Witness = witness,
                    TreeVersion = ModuleFloorWitness.TreeVersion(FilesUnder(all, root), f.Content),
                    IncomingContentHash = witness is null
                        ? null
                        : ModuleFloorWitness.ContentHash(FilesUnder(all, root), f.Content),
                };
            })
            .OrderBy(m => m.Root, StringComparer.Ordinal)
            .ToImmutableList();
    }

    /// <summary>The files under a module root, root-relative, with their raw bytes (a text file's
    /// bytes are its UTF-8 encoding — the transports decode strictly, so that round-trips).</summary>
    private static IEnumerable<(string Path, byte[] Bytes)> FilesUnder(IReadOnlyList<RepoFile> files, string root)
        => files
            .Where(f => root.Length == 0 || f.Path.StartsWith(root + "/", StringComparison.Ordinal))
            .Select(f => (root.Length == 0 ? f.Path : f.Path[(root.Length + 1)..],
                f.Binary ?? System.Text.Encoding.UTF8.GetBytes(f.Content)));

    /// <summary>
    /// The manifest hashes an import may RECORD once it concluded: every module that synced or was
    /// unchanged at its incoming hash; a declined module keeps what the Space held before (it did
    /// not land), and a module that is gone from the tree is dropped.
    /// </summary>
    /// <param name="outcomes">The import's per-module outcomes.</param>
    /// <returns>Module → manifest hash.</returns>
    public static ImmutableDictionary<string, string> Recorded(IEnumerable<ModuleSyncOutcome> outcomes)
    {
        ArgumentNullException.ThrowIfNull(outcomes);
        var builder = ImmutableDictionary.CreateBuilder<string, string>(StringComparer.Ordinal);
        foreach (var o in outcomes)
        {
            var version = o.Outcome == ModuleSyncOutcomeKind.Declined ? o.HeldVersion : o.IncomingVersion;
            if (version is { Length: > 0 })
                builder[o.Module] = version;
        }
        return builder.ToImmutable();
    }

    /// <summary>True when <paramref name="relativePath"/> is a <c>manifest.lock</c> at any depth.</summary>
    public static bool IsManifestPath(string relativePath)
        => string.Equals(relativePath, ManifestFileName, StringComparison.OrdinalIgnoreCase)
           || relativePath.EndsWith("/" + ManifestFileName, StringComparison.OrdinalIgnoreCase);

    private static (string? Module, string? Version) ParseManifest(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var r = doc.RootElement;
            if (r.ValueKind != JsonValueKind.Object)
                return (null, null);
            return (
                r.TryGetProperty("module", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString() : null,
                r.TryGetProperty("moduleVersion", out var v) && v.ValueKind == JsonValueKind.String
                    && !string.IsNullOrWhiteSpace(v.GetString())
                    ? v.GetString()
                    : null);
        }
        catch (JsonException)
        {
            return (null, null);
        }
    }

    private static ImmutableList<string> ParseRequires(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var r = doc.RootElement;
            if (r.ValueKind != JsonValueKind.Object
                || !r.TryGetProperty("content", out var content)
                || content.ValueKind != JsonValueKind.Object
                || !content.TryGetProperty("requires", out var requires)
                || requires.ValueKind != JsonValueKind.Array)
                return [];
            return [.. requires.EnumerateArray()
                .Select(e => e.ValueKind == JsonValueKind.String ? e.GetString() : null)
                .OfType<string>()
                .Where(text => !string.IsNullOrWhiteSpace(text))
                .Select(text => text.Trim())];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static string? ParseFloor(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var r = doc.RootElement;
            return r.ValueKind == JsonValueKind.Object
                   && r.TryGetProperty("content", out var content)
                   && content.ValueKind == JsonValueKind.Object
                   && content.TryGetProperty("minMeshVersion", out var floor)
                   && floor.ValueKind == JsonValueKind.String
                   && !string.IsNullOrWhiteSpace(floor.GetString())
                ? floor.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
