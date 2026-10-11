using System.Collections.Immutable;
using System.Reactive;
using System.Reactive.Linq;
using MeshWeaver.Data;
using MeshWeaver.GitSync;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using MeshWeaver.Plugin.Packaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.PluginCatalog;

/// <summary>
/// 🚨 <b>A package UPDATE never moves onto a dependency build that does not meet its declared
/// floor</b> — the installer's half of MeshWeaver#6067, and the root of MeshWeaver#6192.
///
/// <para><b>What was missing.</b> A package's <see cref="PackageManifest.Requires"/> entry
/// (<c>Essentials@^1.12.0</c>) was judged in two places: by the GitSync import before it writes a
/// module's sources (<see cref="ModuleSyncDecision.DeclineUnmetRequirements"/>) and by the module-set
/// proposal after a landing wave (<see cref="ModuleDependencyFloor"/>). The installer judged an
/// update by its content hash and its platform floor only. So a package updated from the registry
/// took a version whose floor the dependency this
/// instance RUNS did not meet: its install record then declared the new floor, every module-set
/// proposal on the deployment was refused for it, and its NodeType sources were compiled against a
/// dependency build that predates what they call.</para>
///
/// <para><b>Measured 2026-10-10/11 on the control instance.</b> Governance 0.15.3 → 0.16.1 declared
/// <c>Essentials@^1.12.0</c> while the Essentials module that ran was 1.11.26 (its partition is kept
/// by a sync source, and its module lands with the seal); AppleWeather 1.5.1 declared
/// <c>AI@^1.27.0</c> beside AI 1.26.2. Neither package ships a module of its own, so nothing on the
/// module lane could withhold them, and <c>[ModuleSet] NOT proposing the landed module set</c> was
/// logged on every reconcile of every replica until the dependency's module landed.</para>
///
/// <para><b>The rule.</b> The candidate's requirements are read against the modules THIS PROCESS has
/// loaded (<see cref="ILoadedPackageModules"/> — loaded, never merely landed: a landing does not run
/// until it is activated). An unmet one HOLDS the update: nothing is fetched or written, the
/// installed version keeps running, the record carries <see cref="PackageManifest.HeldUpdate"/> with
/// both packages and both versions, and the first reconcile after a satisfying dependency is loaded
/// applies it. A dependency with no loaded module here (a content-only package, a module this
/// process did not load) and a range <see cref="PackageRequirement.Satisfies"/> cannot read are NOT
/// judged — absent means "not judged", never "met" or "unmet", exactly as on the GitSync lane.</para>
///
/// <para><b>Where it sits.</b> On the one install orchestrator (<c>CatalogLayoutAreas.InstallOrUpdate</c>),
/// which the boot install, the unattended apply and a click on Update all go through, and in
/// <see cref="PackageUpdateReconciler"/> before a reminder is raised, so an update this instance
/// cannot run is not offered either.</para>
///
/// <para><b>What it does not cover.</b> A re-install of the content already recorded, and a
/// sync-owned partition (its sync source applies the same rule before it writes, and the record must
/// adopt what that owner has landed). A FRESH install is not held either, and not because it is
/// safe: a dependency installed earlier in the same closure is landed, not loaded. It is left out
/// because the image ships its own copy of the platform modules and the registry is routinely ahead
/// of it, so refusing on the loaded reading would fail a new instance's first boot for every default
/// package above that copy; the module-set proposal still names such a floor. The reading is per
/// process: while replicas run different generations of a dependency, one that runs a satisfying
/// generation applies the update for all of them — the same limit the GitSync import's rule has.</para>
/// </summary>
public static class PackageDependencyFloorGate
{
    /// <summary>The phrase every <see cref="HeldSentence"/> carries and no other hold does.</summary>
    internal const string HoldMarker = "its requirement ";

    /// <summary>
    /// The first requirement of <paramref name="candidate"/> that the loaded modules do not meet, or
    /// null when every judged requirement is met. Pure.
    /// </summary>
    /// <param name="candidate">The package version a source serves.</param>
    /// <param name="loaded">Package id → the module this process has loaded for it.</param>
    public static UnmetPackageDependency? Unmet(
        PackageManifest candidate, IReadOnlyDictionary<string, LoadedPackageModule> loaded)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(loaded);
        foreach (var requirement in candidate.Requires)
        {
            var dependency = PackageRequirement.DependencyId(requirement);
            if (dependency.Length == 0 || LoadedOf(loaded, dependency) is not { } module)
                continue;
            if (PackageRequirement.Satisfies(PackageRequirement.RangeOf(requirement), module.Version) == false)
                return new UnmetPackageDependency(requirement.Trim(), dependency, module.Module, module.Version);
        }
        return null;
    }

    private static LoadedPackageModule? LoadedOf(IReadOnlyDictionary<string, LoadedPackageModule> loaded, string id)
    {
        var module = loaded.TryGetValue(id, out var exact)
            ? exact
            : loaded.FirstOrDefault(kv => string.Equals(kv.Key, id, StringComparison.OrdinalIgnoreCase)).Value;
        return module is { Version.Length: > 0 } ? module : null;
    }

    /// <summary>
    /// <see cref="Unmet"/> against what <paramref name="hub"/>'s process has loaded. A mesh with no
    /// <see cref="ILoadedPackageModules"/> (no module host), an empty reading and a reading that
    /// faults all judge nothing — the fault is logged, and the update is decided as it was before
    /// this gate existed.
    /// </summary>
    /// <param name="hub">The calling hub.</param>
    /// <param name="candidate">The package version a source serves.</param>
    /// <param name="logger">Diagnostics.</param>
    /// <returns>Cold; emits exactly once.</returns>
    public static IObservable<UnmetPackageDependency?> Evaluate(
        IMessageHub hub, PackageManifest candidate, ILogger? logger)
    {
        ArgumentNullException.ThrowIfNull(hub);
        ArgumentNullException.ThrowIfNull(candidate);
        if (candidate.Requires.IsEmpty
            || hub.ServiceProvider.GetService<ILoadedPackageModules>() is not { } modules)
            return Observable.Return<UnmetPackageDependency?>(null);
        return modules.Read()
            .Take(1)
            .DefaultIfEmpty(ImmutableDictionary<string, LoadedPackageModule>.Empty)
            .Select(loaded => Unmet(candidate, loaded))
            .Catch((Exception ex) =>
            {
                logger?.LogWarning(ex,
                    "Package update: the loaded modules could not be read, so the requirements of {Id} "
                    + "are NOT judged on this pass.", candidate.Id);
                return Observable.Return<UnmetPackageDependency?>(null);
            });
    }

    /// <summary>
    /// The sentence an install record carries while an update is held for a dependency (pure). It
    /// names the held version, the requirement and what runs, so it changes exactly when the held
    /// state does.
    /// </summary>
    /// <param name="candidate">The held candidate.</param>
    /// <param name="unmet">The requirement that holds it.</param>
    public static string HeldSentence(PackageManifest candidate, UnmetPackageDependency unmet)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(unmet);
        var version = candidate.ReleasedVersion ?? candidate.Version ?? candidate.ModuleVersion ?? "the served version";
        return $"held: {version} — {HoldMarker}{unmet.Requirement} is not met: this instance runs "
            + $"'{unmet.Module}' (package '{unmet.Dependency}') at {unmet.LoadedVersion}; updates once a "
            + $"satisfying '{unmet.Dependency}' is loaded";
    }

    /// <summary>Whether <paramref name="heldUpdate"/> is a dependency hold (<see cref="HeldSentence"/>). Pure.</summary>
    /// <param name="heldUpdate">The record's hold sentence, or null.</param>
    public static bool IsDependencyHold(string? heldUpdate) =>
        heldUpdate is { Length: > 0 } sentence && sentence.Contains(HoldMarker, StringComparison.Ordinal);

    /// <summary>
    /// Makes the hold visible: stamps <see cref="PackageManifest.HeldUpdate"/> and
    /// <see cref="PackageManifest.HeldSince"/> on the install record and logs it — at Warning when the
    /// held state is new, at Information while it is unchanged, so one hold is one warning and not
    /// one per reconcile. Never faults: the update is held either way.
    /// </summary>
    /// <param name="hub">The calling hub.</param>
    /// <param name="candidate">The held candidate.</param>
    /// <param name="record">The install record as read.</param>
    /// <param name="unmet">The requirement that holds it.</param>
    /// <param name="logger">Diagnostics.</param>
    /// <returns>Cold; emits once when the hold is recorded (or nothing needed doing).</returns>
    public static IObservable<Unit> RecordHold(
        IMessageHub hub, PackageManifest candidate, PackageManifest record, UnmetPackageDependency unmet,
        ILogger? logger)
    {
        ArgumentNullException.ThrowIfNull(hub);
        ArgumentNullException.ThrowIfNull(record);
        var sentence = HeldSentence(candidate, unmet);
        var installed = record.ReleasedVersion ?? record.Version ?? record.ModuleVersion ?? "(unknown)";
        if (string.Equals(record.HeldUpdate, sentence, StringComparison.Ordinal))
        {
            logger?.LogInformation(
                "Package {Id}: update still {Held} — installed {Installed} keeps running.",
                candidate.Id, sentence, installed);
            return Observable.Return(Unit.Default);
        }

        logger?.LogWarning(
            "Package {Id}: update {Held} — installed {Installed} keeps running (MeshWeaver#6192).",
            candidate.Id, sentence, installed);
        var recordPath = $"{PackageInstaller.InstalledPartition}/{candidate.Id}";
        var now = DateTimeOffset.UtcNow;
        // RunAsSystem, never Observable.Using (#1790): the install-records partition is System-owned.
        return hub.ServiceProvider.GetRequiredService<AccessService>()
            .RunAsSystem(() => hub.GetMeshNodeStream(recordPath)
                .Update<PackageManifest>(current => AlreadyAt(current, candidate)
                    // 🚨 The record this hold was DECIDED on is a read from before this write: another
                    // replica (whose loaded dependency satisfies the floor) or a click may have
                    // installed the candidate in between. A hold stamped then would sit on the
                    // installed version for good — the next reconcile ends at the equal-hash gate,
                    // before anything clears a hold.
                    ? current
                    : current with
                    {
                        HeldUpdate = sentence,
                        // A hold for ANOTHER reason that this one replaces had its own ticket; this one has none.
                        HeldUpdateDispatch = null,
                        HeldUpdateDispatchedAt = null,
                        HeldSince = IsDependencyHold(current.HeldUpdate) ? current.HeldSince ?? now : now,
                    }))
            .Take(1)
            .Select(_ => Unit.Default)
            .DefaultIfEmpty(Unit.Default)
            .Catch((Exception ex) =>
            {
                logger?.LogWarning(ex,
                    "Package {Id}: the update is held, but stamping the hold on {Path} failed.",
                    candidate.Id, recordPath);
                return Observable.Return(Unit.Default);
            });
    }

    /// <summary>Whether <paramref name="record"/> already describes <paramref name="candidate"/>'s
    /// content — the candidate has landed, so there is no update left to hold. Pure.</summary>
    /// <param name="record">The install record as it stands now.</param>
    /// <param name="candidate">The candidate a hold was decided for.</param>
    public static bool AlreadyAt(PackageManifest record, PackageManifest candidate)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(candidate);
        return !string.IsNullOrEmpty(candidate.ModuleVersion)
            && string.Equals(record.ModuleVersion, candidate.ModuleVersion, StringComparison.Ordinal);
    }

    /// <summary>
    /// The requirement that held an update is met: clear the hold on the record and hand back the
    /// current record. A record carrying no dependency hold is returned untouched. Never faults.
    /// </summary>
    /// <param name="hub">The calling hub.</param>
    /// <param name="record">The install record as read.</param>
    /// <param name="logger">Diagnostics.</param>
    /// <returns>Cold; emits the record the decision continues with.</returns>
    public static IObservable<PackageManifest> ClearMetHold(IMessageHub hub, PackageManifest record, ILogger? logger)
    {
        ArgumentNullException.ThrowIfNull(hub);
        ArgumentNullException.ThrowIfNull(record);
        if (!IsDependencyHold(record.HeldUpdate))
            return Observable.Return(record);
        logger?.LogInformation(
            "Package update: {Id} — the loaded dependency now satisfies the requirement that held it "
            + "({Held}); the hold is cleared and the update is decided again.", record.Id, record.HeldUpdate);
        var cleared = record with { HeldUpdate = null, HeldSince = null };
        var recordPath = $"{PackageInstaller.InstalledPartition}/{record.Id}";
        return hub.ServiceProvider.GetRequiredService<AccessService>()
            .RunAsSystem(() => hub.GetMeshNodeStream(recordPath)
                .Update<PackageManifest>(current => IsDependencyHold(current.HeldUpdate)
                    ? current with { HeldUpdate = null, HeldSince = null }
                    : current))
            .Take(1)
            .Select(_ => cleared)
            .DefaultIfEmpty(cleared)
            .Catch((Exception ex) =>
            {
                logger?.LogWarning(ex,
                    "Package update: clearing the met dependency hold on {Path} failed.", recordPath);
                return Observable.Return(cleared);
            });
    }
}

/// <summary>
/// One requirement of a package update that this instance does not meet (MeshWeaver#6192): the
/// candidate declares <paramref name="Requirement"/>, and the process runs <paramref name="Module"/>
/// at <paramref name="LoadedVersion"/>, outside it.
/// </summary>
/// <param name="Requirement">The requirement as declared (<c>Essentials@^1.12.0</c>).</param>
/// <param name="Dependency">The required package's id.</param>
/// <param name="Module">The dependency's module (entry-assembly name).</param>
/// <param name="LoadedVersion">The version of that module this process has loaded.</param>
public sealed record UnmetPackageDependency(
    string Requirement, string Dependency, string Module, string LoadedVersion);
