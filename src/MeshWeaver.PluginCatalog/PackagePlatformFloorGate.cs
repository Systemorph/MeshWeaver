using System.Reactive;
using System.Reactive.Linq;
using MeshWeaver.Data;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using MeshWeaver.Plugin.Packaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.PluginCatalog;

/// <summary>
/// 🚨 <b>The declared <c>minMeshVersion</c> floor on the PACKAGE (source-content) lane</b> —
/// policy <c>package-min-mesh-version</c>, rule R2 of <c>Doc/Architecture/ModuleAdoptionPolicy</c>.
/// A package version is used only when the running platform satisfies its declared floor
/// (<see cref="PlatformFloor"/> against <see cref="PlatformBuildInfo.RunningPlatformVersion"/>).
///
/// <para><b>Why the content lane needs it.</b> A package update was decided by the manifest hash
/// alone, so a new version landed on any instance whatever platform it ran. Measured 2026-09-27:
/// Store 1.16 used <c>IPaymentProvider</c> billing-portal members and Hosting used
/// <c>DeploymentContent.AnnouncementKeySecret</c>; both synced onto instances running
/// <c>3.0.0-ci.9412</c>/<c>9414</c> — images predating those members — and 10 Store plus 4
/// Hosting NodeTypes were left with no usable assembly (CS0117/CS1061). The module lane's link
/// probe cannot see this: NodeType SOURCE compiles in the mesh, after it has landed.</para>
///
/// <para><b>Phase 1 (this type).</b> An UPDATE whose candidate floor is unmet is HELD — nothing is
/// fetched or written, the installed version keeps running (R1), the record carries
/// <see cref="PackageManifest.HeldUpdate"/>, and the update applies on the first reconcile after
/// the platform rolls. A FRESH install whose floor is unmet is REFUSED with
/// <see cref="PackagePlatformFloorException"/>. Phase 2 (not done): the registry retains older
/// versions with their floors and the installer picks the newest one whose floor is satisfied.</para>
/// </summary>
public static class PackagePlatformFloorGate
{
    /// <summary>
    /// The running platform version every catalog floor decision compares against:
    /// <see cref="PlatformBuildInfo.RunningPlatformVersion"/> — unless a TEST registered a
    /// <see cref="RunningPlatformVersionOverride"/> on the mesh (a test process is a local
    /// <c>-dev</c> source build, against which no floor is ordered, so without it a hold could never be
    /// exercised end to end). Production registers none.
    /// </summary>
    /// <param name="hub">The calling hub.</param>
    public static string? RunningVersion(IMessageHub hub) =>
        hub.ServiceProvider.GetService<RunningPlatformVersionOverride>() is { } pinned
            ? pinned.Version
            : PlatformBuildInfo.RunningPlatformVersion;

    /// <summary>The floor verdict for <paramref name="candidate"/> on the platform
    /// <paramref name="hub"/> runs (<see cref="RunningVersion"/>).</summary>
    /// <param name="hub">The calling hub.</param>
    /// <param name="candidate">The package version a source serves.</param>
    public static PlatformFloorVerdict Evaluate(IMessageHub hub, PackageManifest candidate) =>
        PlatformFloor.Evaluate(candidate.MinMeshVersion, RunningVersion(hub));

    /// <summary>The floor DECISION in the shape <see cref="ModuleUpdateDecision"/>'s
    /// <c>floorHold</c> takes, bound to the platform <paramref name="hub"/> runs.</summary>
    /// <param name="hub">The calling hub.</param>
    public static Func<string?, string?> HoldFor(IMessageHub hub) =>
        floor => PlatformFloor.HoldReason(floor, RunningVersion(hub));

    /// <summary>
    /// How long an unchanged hold stays quiet before its blocking ticket is sent again — once a
    /// day, so a hold nobody acted on is re-raised without one ticket per reconcile tick.
    /// </summary>
    public static readonly TimeSpan RedispatchInterval = TimeSpan.FromHours(24);

    /// <summary>
    /// The sentence an install record carries while an update is held (pure). It names the held
    /// version, the floor and the running platform, so it changes exactly when the held STATE
    /// does — which is what makes it the de-duplication key of the blocking ticket.
    /// </summary>
    /// <param name="candidate">The held candidate.</param>
    /// <param name="verdict">Its <see cref="PlatformFloorKind.Held"/> verdict.</param>
    public static string HeldSentence(PackageManifest candidate, PlatformFloorVerdict verdict) =>
        $"held: {VersionOf(candidate)} {FloorHoldMarker} {verdict.Floor}, running {verdict.Running} — "
        + "updates when the platform rolls";

    /// <summary>
    /// Whether <paramref name="heldUpdate"/> is a FLOOR hold (<see cref="HeldSentence"/>) — the only hold
    /// a met floor may clear. A hold for another reason (a sync-owned partition whose sync has not
    /// landed the candidate) is re-decided by its own lane and must keep its since-when; clearing it on
    /// every pass would reset <see cref="PackageManifest.HeldSince"/> and wipe its dispatch stamps each
    /// time (review on MeshWeaver#6065). Pure.
    /// </summary>
    /// <param name="heldUpdate">The record's hold sentence, or null.</param>
    public static bool IsFloorHold(string? heldUpdate) =>
        heldUpdate is { Length: > 0 } sentence
        && sentence.Contains(FloorHoldMarker, StringComparison.Ordinal);

    /// <summary>The phrase every <see cref="HeldSentence"/> carries and no other hold does.</summary>
    internal const string FloorHoldMarker = "needs platform ≥";

    /// <summary>The blocking ticket for one held update (pure).</summary>
    /// <param name="candidate">The held candidate.</param>
    /// <param name="record">The install record as read — the version that keeps running.</param>
    /// <param name="verdict">The <see cref="PlatformFloorKind.Held"/> verdict.</param>
    public static PackageHoldTicket Ticket(
        PackageManifest candidate, PackageManifest record, PlatformFloorVerdict verdict) =>
        new(candidate.Id, VersionOf(candidate), verdict.Floor ?? "", verdict.Running ?? "",
            record.ReleasedVersion ?? record.Version ?? record.ModuleVersion,
            HeldSentence(candidate, verdict),
            $"unblocks when this instance rolls to a platform ≥ {verdict.Floor} (it runs {verdict.Running}); "
            + $"until then {candidate.Id} {record.ReleasedVersion ?? record.Version ?? "(installed)"} keeps running");

    /// <summary>
    /// Whether this reconcile sends the blocking ticket (pure): when the held state changed (a new
    /// sentence), when the last attempt was not accepted, or when the last accepted ticket is older
    /// than <see cref="RedispatchInterval"/>. Never once per tick.
    /// </summary>
    /// <param name="record">The install record as read.</param>
    /// <param name="sentence">The current <see cref="HeldSentence"/>.</param>
    /// <param name="now">Now.</param>
    public static bool ShouldDispatch(PackageManifest record, string sentence, DateTimeOffset now) =>
        !string.Equals(record.HeldUpdate, sentence, StringComparison.Ordinal)
        || record.HeldUpdateDispatchedAt is not { } at
        || now - at >= RedispatchInterval;

    private static string VersionOf(PackageManifest candidate) =>
        candidate.ReleasedVersion ?? candidate.Version ?? candidate.ModuleVersion ?? "the served version";

    /// <summary>
    /// Makes a hold VISIBLE (policy <c>package-min-mesh-version</c>): logs it, sends ONE blocking
    /// ticket through the registered <see cref="IPackageHoldDispatch"/> (the control inbox) per held
    /// state, and stamps <see cref="PackageManifest.HeldUpdate"/>,
    /// <see cref="PackageManifest.HeldUpdateDispatch"/> and
    /// <see cref="PackageManifest.HeldUpdateDispatchedAt"/> on the install record. An unchanged
    /// hold whose ticket was accepted within <see cref="RedispatchInterval"/> writes and sends
    /// nothing. Never faults — the update is held either way, and a ticket or stamp that could not
    /// be delivered is said so, at Warning, on the log and on the record.
    /// </summary>
    /// <param name="hub">The calling hub.</param>
    /// <param name="candidate">The held candidate.</param>
    /// <param name="record">The install record as read.</param>
    /// <param name="verdict">The <see cref="PlatformFloorKind.Held"/> verdict.</param>
    /// <param name="logger">Diagnostics.</param>
    /// <returns>Cold; emits once when the hold is recorded (or nothing needed doing).</returns>
    public static IObservable<Unit> RecordHold(
        IMessageHub hub, PackageManifest candidate, PackageManifest record, PlatformFloorVerdict verdict,
        ILogger? logger)
    {
        var sentence = HeldSentence(candidate, verdict);
        var installed = record.ReleasedVersion ?? record.ModuleVersion ?? "(unknown)";
        var now = DateTimeOffset.UtcNow;
        if (!ShouldDispatch(record, sentence, now))
        {
            logger?.LogInformation(
                "Package {Id}: update still {Held} — installed {Installed} keeps running; {Dispatch}.",
                candidate.Id, sentence, installed, record.HeldUpdateDispatch);
            return Observable.Return(Unit.Default);
        }

        var stateChanged = !string.Equals(record.HeldUpdate, sentence, StringComparison.Ordinal);
        if (stateChanged)
            logger?.LogWarning(
                "Package {Id}: update {Held} — installed {Installed} keeps running (policy "
                + "package-min-mesh-version).",
                candidate.Id, sentence, installed);

        var dispatch = hub.ServiceProvider.GetService<IPackageHoldDispatch>();
        var sent = dispatch is null
            ? Observable.Return<(string Status, DateTimeOffset? At)>((
                "NOT dispatched: no blocking-ticket dispatch (IPackageHoldDispatch) is registered on "
                + "this host, so the control instance is not told", null))
            : dispatch.Dispatch(hub, Ticket(candidate, record, verdict))
                .Take(1)
                .Select(delivered => (Status: $"blocking ticket dispatched: {delivered}", At: (DateTimeOffset?)now))
                .DefaultIfEmpty(("NOT dispatched: the dispatch completed without a delivery", null))
                .Catch((Exception ex) => Observable.Return<(string Status, DateTimeOffset? At)>((
                    $"NOT dispatched: {ex.Message}", null)));

        var accessService = hub.ServiceProvider.GetService<AccessService>();
        var recordPath = $"{PackageInstaller.InstalledPartition}/{candidate.Id}";
        return sent
            .Do(outcome =>
            {
                if (outcome.At is null
                    && (stateChanged || !string.Equals(record.HeldUpdateDispatch, outcome.Status, StringComparison.Ordinal)))
                    logger?.LogWarning(
                        "Package {Id}: the update is held and its blocking ticket was {Status}.",
                        candidate.Id, outcome.Status);
                else if (outcome.At is not null)
                    logger?.LogInformation("Package {Id}: {Status}.", candidate.Id, outcome.Status);
            })
            .SelectMany(outcome =>
                string.Equals(record.HeldUpdate, sentence, StringComparison.Ordinal)
                && string.Equals(record.HeldUpdateDispatch, outcome.Status, StringComparison.Ordinal)
                && record.HeldUpdateDispatchedAt == outcome.At
                    ? Observable.Return(Unit.Default)
                    // RunAsSystem, never Observable.Using (#1790): the install-records partition is
                    // System-owned, and Rx would otherwise leave the subscribing thread latched.
                    : accessService.RunAsSystem(() => hub.GetMeshNodeStream(recordPath)
                            .Update<PackageManifest>(current => current with
                            {
                                HeldUpdate = sentence,
                                HeldUpdateDispatch = outcome.Status,
                                HeldUpdateDispatchedAt = outcome.At,
                                HeldSince = current.HeldSince ?? now,
                            })
                            .Select(_ => Unit.Default))
                        .Take(1)
                        .DefaultIfEmpty(Unit.Default))
            .Catch((Exception ex) =>
            {
                logger?.LogWarning(ex,
                    "Package {Id}: the update is held, but stamping the hold on {Path} failed.",
                    candidate.Id, recordPath);
                return Observable.Return(Unit.Default);
            });
    }

    /// <summary>The former name of <see cref="RequireForInstall"/> — kept as a forwarder for any caller
    /// compiled against it; the gate now covers updates over an existing record too.</summary>
    /// <param name="hub">The installing hub.</param>
    /// <param name="manifest">The package about to be installed.</param>
    /// <param name="logger">Diagnostics.</param>
    [Obsolete("Use RequireForInstall — the gate covers every install, an update over an existing record included.")]
    public static IObservable<Unit> RequireForFreshInstall(IMessageHub hub, PackageManifest manifest, ILogger? logger)
        => RequireForInstall(hub, manifest, logger);

    /// <summary>
    /// The installer's gate for EVERY install — a fresh one and an update over an existing record: faults with <see cref="PackagePlatformFloorException"/>
    /// when <paramref name="manifest"/>'s floor is held on this platform, unless it is a re-install of
    /// the content already recorded here (<see cref="AllowedOverExistingRecord"/>), which heals in
    /// place. The unattended and click lanes hold an update upstream, before anything is fetched
    /// (<c>CatalogLayoutAreas.InstallOrUpdate</c>, <c>PackageUpdateReconciler</c>); this is the
    /// enforcement half for every OTHER caller of the installer — a maintenance refresh included.
    /// </summary>
    /// <param name="hub">The installing hub.</param>
    /// <param name="manifest">The package about to be installed.</param>
    /// <param name="logger">Diagnostics.</param>
    /// <returns>Cold; emits once when the install may proceed.</returns>
    public static IObservable<Unit> RequireForInstall(
        IMessageHub hub, PackageManifest manifest, ILogger? logger)
    {
        var verdict = Evaluate(hub, manifest);
        if (verdict.Kind == PlatformFloorKind.Advisory)
            logger?.LogInformation("Package {Id}: {Advisory}", manifest.Id, verdict.Reason);
        if (!verdict.IsHeld)
            return Observable.Return(Unit.Default);

        var persistence = hub.ServiceProvider.GetService<IStorageAdapter>();
        var existing = persistence is null
            ? Observable.Return<(bool Installed, string? ModuleVersion)>((false, null))
            : persistence.Read($"{PackageInstaller.InstalledPartition}/{manifest.Id}", hub.JsonSerializerOptions)
                .Take(1)
                .DefaultIfEmpty()
                .Select(node => (Installed: node is not null,
                    ModuleVersion: node?.ContentAs<PackageManifest>(hub.JsonSerializerOptions)?.ModuleVersion))
                .Catch((Exception _) => Observable.Return<(bool Installed, string? ModuleVersion)>((false, null)));

        return existing.SelectMany(record =>
        {
            if (AllowedOverExistingRecord(record.Installed, record.ModuleVersion, manifest.ModuleVersion))
                return Observable.Return(Unit.Default);
            var message = record.Installed
                ? $"Package '{manifest.Id}' was not updated: the version the source serves needs "
                  + $"platform ≥ {verdict.Floor}, and this instance runs {verdict.Running}. The installed "
                  + "version keeps running; the update lands once the platform rolls (policy "
                  + "package-min-mesh-version)."
                : $"Package '{manifest.Id}' was not installed: the version the source serves needs "
                  + $"platform ≥ {verdict.Floor}, and this instance runs {verdict.Running}. It installs "
                  + "once the platform rolls (policy package-min-mesh-version).";
            logger?.LogWarning("{Refusal}", message);
            return Observable.Throw<Unit>(new PackagePlatformFloorException(message));
        });
    }

    /// <summary>
    /// Whether an install whose floor is HELD may still run because a record exists (pure): only a
    /// re-install of the SAME content heals in place. A different version above the floor is
    /// refused even over an existing record — before 2026-10-04 any existing record waved it
    /// through, so a maintenance refresh (RefreshModules → <c>RegistryPackages.Install</c>) could
    /// land exactly the version policy <c>package-min-mesh-version</c> exists to hold (the 09-27
    /// breakage: 14 NodeTypes with no usable assembly).
    /// </summary>
    /// <param name="installed">Whether an install record exists.</param>
    /// <param name="installedModuleVersion">The record's content hash.</param>
    /// <param name="candidateModuleVersion">The candidate's content hash.</param>
    public static bool AllowedOverExistingRecord(
        bool installed, string? installedModuleVersion, string? candidateModuleVersion) =>
        installed
        && !string.IsNullOrWhiteSpace(candidateModuleVersion)
        && string.Equals(installedModuleVersion, candidateModuleVersion, StringComparison.Ordinal);
}

/// <summary>
/// A FRESH package install was refused because the version the source serves declares a
/// <c>minMeshVersion</c> floor above the running platform (policy <c>package-min-mesh-version</c>).
/// Distinct from an install FAILURE: nothing was written.
/// </summary>
public sealed class PackagePlatformFloorException : InvalidOperationException
{
    /// <summary>Initializes a new instance of the <see cref="PackagePlatformFloorException"/> class.</summary>
    public PackagePlatformFloorException()
    {
    }

    /// <summary>Initializes a new instance with the refusal <paramref name="message"/>.</summary>
    /// <param name="message">The speaking refusal reason.</param>
    public PackagePlatformFloorException(string message) : base(message)
    {
    }

    /// <summary>Initializes a new instance with a message and an inner exception.</summary>
    /// <param name="message">The speaking refusal reason.</param>
    /// <param name="innerException">The underlying cause.</param>
    public PackagePlatformFloorException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// ONE blocking ticket: a package update this instance cannot take, because the version the source
/// serves declares a platform floor above the running platform (policy
/// <c>package-min-mesh-version</c>). Carries what would unblock it.
/// </summary>
/// <param name="Package">The package id.</param>
/// <param name="HeldVersion">The version that is held.</param>
/// <param name="Floor">Its declared <c>minMeshVersion</c>.</param>
/// <param name="Running">The running platform version.</param>
/// <param name="InstalledVersion">The version that keeps running, or null when unknown.</param>
/// <param name="Summary">The held sentence (<see cref="PackagePlatformFloorGate.HeldSentence"/>).</param>
/// <param name="Unblocks">What would unblock it.</param>
public sealed record PackageHoldTicket(
    string Package, string HeldVersion, string Floor, string Running, string? InstalledVersion,
    string Summary, string Unblocks);

/// <summary>
/// 🚨 <b>How a held update becomes a BLOCKING TICKET on the control instance</b> — the hook point
/// of policy <c>package-min-mesh-version</c> ("when we cannot advance we file a blocking ticket,
/// through dispatch"). The portal host registers the implementation that posts the signed event
/// into the control inbox on the SAME channel as a self-update announcement
/// (<c>Hosting:ControlInbox:Url</c> + <c>Hosting:ControlInbox:Secret</c>); the catalog only calls
/// it, once per held state (<see cref="PackagePlatformFloorGate.ShouldDispatch"/>). A host with no
/// registration records "NOT dispatched" on the install record and logs it at Warning.
/// </summary>
public interface IPackageHoldDispatch
{
    /// <summary>
    /// Sends one ticket. Emits a short description of the delivery ("accepted by … at …") once;
    /// ERRORS with the reason when there is no route (no control inbox configured) or the inbox did
    /// not accept it. Cold.
    /// </summary>
    /// <param name="hub">The calling hub.</param>
    /// <param name="ticket">The ticket.</param>
    IObservable<string> Dispatch(IMessageHub hub, PackageHoldTicket ticket);
}

/// <summary>
/// 🚨 <b>TEST SEAM ONLY</b> — pins the running platform version the catalog's floor decisions read
/// (<see cref="PackagePlatformFloorGate.RunningVersion"/>). A test process is a local <c>-dev</c>
/// build, against which no floor is ordered, so an end-to-end hold cannot be exercised without it.
/// Production never registers one: the running version is <see cref="PlatformBuildInfo.RunningPlatformVersion"/>.
/// </summary>
/// <param name="Version">The version to report as running.</param>
public sealed record RunningPlatformVersionOverride(string? Version);
