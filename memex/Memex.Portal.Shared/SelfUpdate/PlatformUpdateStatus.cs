using System.Reactive.Linq;
using MeshWeaver.Data;
using MeshWeaver.Graph;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;

namespace Memex.Portal.Shared.SelfUpdate;

/// <summary>Whether the running build is the newest one the platform knows about.</summary>
public enum PlatformUpdateAvailability
{
    /// <summary>Nothing can be said: the policy node does not exist (this install never wired
    /// self-update), the read failed, or automatic checks are switched off and no newer build was
    /// ever recorded. Renders as NO update line at all — silence beats an unfounded "up to date".</summary>
    Unknown,

    /// <summary>No build newer than the running one has been detected.</summary>
    UpToDate,

    /// <summary>A newer build exists; <see cref="PlatformUpdateStatus.LatestVersion"/> names it.</summary>
    UpdateAvailable,
}

/// <summary>
/// The one fact an ORDINARY user needs to answer "is this deployment up to date?" — a two-state
/// verdict plus, when an update exists, the version that supersedes the running build. Deliberately
/// the whole projection: the update <see cref="UpdatePolicyContent.Policy"/>, the CI-green flag and
/// the poller's <see cref="UpdatePolicyContent.CheckedAt"/> bookkeeping stay behind the admin gate on
/// <see cref="Settings.UpdatePolicySettingsTab"/>.
///
/// <para><b>Why a projection and not a direct read:</b> both halves of the comparison live on
/// <c>Admin/UpdatePolicy</c>, and the <c>Admin</c> partition is platform-scoped — an ordinary user
/// cannot read it (pinned by <c>NonAdminUpdateStatusTest</c>), and widening that grant would hand
/// every user the whole update policy. So <see cref="Observe"/> reads the node as System and lets
/// only this record out, exactly as <see cref="Settings.PrivacyStatementNode.GetStatement"/> does for
/// the anonymous <c>/privacy</c> page.</para>
/// </summary>
/// <param name="Availability">The verdict.</param>
/// <param name="LatestVersion">The newer version's tag — non-null ONLY for
/// <see cref="PlatformUpdateAvailability.UpdateAvailable"/>.</param>
public record PlatformUpdateStatus(PlatformUpdateAvailability Availability, string? LatestVersion)
{
    /// <summary>Nothing is known — the render-nothing state.</summary>
    public static readonly PlatformUpdateStatus Unknown = new(PlatformUpdateAvailability.Unknown, null);

    /// <summary>
    /// The pure verdict: is <paramref name="runningVersion"/> superseded by the newest tag the poller
    /// has recorded?
    ///
    /// <list type="bullet">
    /// <item>A recorded tag that is strictly newer ⇒ <see cref="PlatformUpdateAvailability.UpdateAvailable"/>
    /// — regardless of policy, because an available update is a fact about the registry, not about
    /// whether this install intends to take it.</item>
    /// <item>Otherwise, with automatic checks OFF (<see cref="UpdatePolicyKind.None"/>) ⇒
    /// <see cref="PlatformUpdateAvailability.Unknown"/>. Nothing is polling, so "up to date" would be a
    /// claim we have no evidence for.</item>
    /// <item>Otherwise ⇒ <see cref="PlatformUpdateAvailability.UpToDate"/>: the poller is running and has
    /// recorded no build newer than this one. (It records only when it finds something newer, so an
    /// empty tag — or one this install has since rolled to — IS the up-to-date signal.)</item>
    /// </list>
    /// </summary>
    public static PlatformUpdateStatus Derive(UpdatePolicyContent policy, string runningVersion)
    {
        var latest = policy.LatestAvailableTag;
        if (!string.IsNullOrEmpty(latest) && VersionSelect.IsNewer(latest, runningVersion))
            return new(PlatformUpdateAvailability.UpdateAvailable, latest);
        return policy.Policy == UpdatePolicyKind.None
            ? Unknown
            : new(PlatformUpdateAvailability.UpToDate, null);
    }

    /// <summary>
    /// The status for the CURRENT build, read off <c>Admin/UpdatePolicy</c> as System and projected to
    /// this record. Safe to call from any user-facing surface: nothing but the verdict (and the newer
    /// tag) escapes the Admin partition.
    ///
    /// <para>Absence is detected with the storm-safe existence <c>GetQuery</c> (empty-on-absent) —
    /// NEVER a point <c>GetMeshNodeStream</c> probe of a maybe-absent path, which NotFound-resubscribe-
    /// storms on an install that never wired self-update. Every failure mode (missing node, query
    /// stall, unparseable content) degrades to <see cref="Unknown"/>, which renders nothing: the About
    /// page must never hang and never error over a nice-to-have line.</para>
    ///
    /// <para>🚨 The System scope is opened and closed SYNCHRONOUSLY around each subscribe, which is the
    /// only moment identity is read (the cache snapshots the caller before the pipeline runs). It is
    /// deliberately NOT <c>Observable.Using</c>: impersonation is an <c>AsyncLocal</c>, and
    /// <c>Using</c> disposes its resource on whichever thread the sequence terminates on — so the
    /// restore never reaches the thread that opened it, leaving the CALLER (here: a user-facing layout
    /// render) running as System afterwards. Two scopes for the same reason: the second subscribe
    /// happens on the thread delivering the query's emission, where an outer scope was never in force.</para>
    ///
    /// <para>One-shot (<c>Take(1)</c>) by design: the poller refreshes this at most a few times a day,
    /// and a live subscription would hold a System-impersonated read open for the lifetime of the view.
    /// Re-opening About re-reads it.</para>
    /// </summary>
    /// <param name="hub">Any hub — the read routes to the Admin partition's owner.</param>
    /// <param name="runningVersion">The build to compare against; defaults to
    /// <see cref="ShippedReleaseSeed.InstalledPlatformVersion"/>. Tests pass an explicit version because
    /// under a test host the entry assembly's informational version is not a platform version at all.</param>
    public static IObservable<PlatformUpdateStatus> Observe(IMessageHub hub, string? runningVersion = null)
    {
        var accessService = hub.ServiceProvider.GetService<AccessService>();
        var workspace = hub.GetWorkspace();
        var jsonOptions = hub.JsonSerializerOptions;
        var running = runningVersion ?? ShippedReleaseSeed.InstalledPlatformVersion;

        // Build AND subscribe inside the scope; close it on the same thread the moment Subscribe
        // returns. See the remarks above for why this is not Observable.Using.
        IObservable<T> AsSystem<T>(Func<IObservable<T>> source) =>
            Observable.Create<T>(observer =>
            {
                using (AccessContextScope.AsSystem(accessService))
                    return source().Subscribe(observer);
            });

        return AsSystem(() => workspace
                // Same query KEY the seeding path registers (UpdatePolicyNodeType.EnsureExists), so
                // this shares that registration instead of adding a second synced query.
                .GetQuery(
                    $"{UpdatePolicyNodeType.NodeType}|{UpdatePolicyNodeType.NodePath}",
                    $"path:{UpdatePolicyNodeType.NodePath} nodeType:{UpdatePolicyNodeType.NodeType}")
                .Take(1)
                .Timeout(TimeSpan.FromSeconds(10)))
            .SelectMany(nodes => nodes.Any()
                ? AsSystem(() => workspace.GetMeshNodeStream(UpdatePolicyNodeType.NodePath)
                    .Where(node => node is not null)
                    .Take(1)
                    .Timeout(TimeSpan.FromSeconds(10))
                    .Select(node => Derive(UpdatePolicyNodeType.Parse(node, jsonOptions), running)))
                : Observable.Return(Unknown))
            .Catch<PlatformUpdateStatus, Exception>(_ => Observable.Return(Unknown));
    }
}
