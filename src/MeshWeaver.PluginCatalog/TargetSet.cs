using System.Collections.Immutable;
using MeshWeaver.Plugin.Packaging;

namespace MeshWeaver.PluginCatalog;

/// <summary>One package as the registry SERVES it — what the target set is computed from.</summary>
/// <param name="PackageId">The package id.</param>
/// <param name="Version">Its published SemVer (<c>releasedVersion</c>, else <c>version</c>).</param>
/// <param name="ModuleVersion">Its content hash.</param>
/// <param name="MinMeshVersion">Its platform floor — stamped by Plugins' <c>stamp-floors</c> from
/// <c>resolve-platform.py</c>'s answer on the green run that verified it.</param>
public sealed record ServedPackage(string PackageId, string? Version, string? ModuleVersion, string? MinMeshVersion)
{
    /// <summary>The served row of a feed manifest.</summary>
    /// <param name="manifest">A manifest the registry serves.</param>
    public static ServedPackage Of(PackageManifest manifest) =>
        new(manifest.Id, manifest.ReleasedVersion ?? manifest.Version, manifest.ModuleVersion, manifest.MinMeshVersion);
}

/// <summary>Where one installed package stands against the target set.</summary>
public enum TargetStanding
{
    /// <summary>Installed at the target version.</summary>
    AtTarget = 0,

    /// <summary>A newer target version is served and not installed.</summary>
    Behind = 1,

    /// <summary>The registry does not serve this package — no target to be measured against.</summary>
    NotServed = 2,

    /// <summary>A newer version is served, but its floor is above the target platform — it becomes
    /// the target when its platform is ARMED; until then the installed version is not behind.</summary>
    AwaitingArming = 3,
}

/// <summary>
/// 🚨 <b>THE TARGET SET — one definition for CI and the fleet</b> (maintainer, 2026-10-04: "we must
/// know which version we want to test and update everyone to latest"; core
/// <c>Doc/Architecture/OnePromotionGate</c> → <i>The target set</i>).
///
/// <para><b>Platform.</b> The newest set that is both SEALED and ARMED — what
/// <c>resolve-platform.py --armed</c> resolves in CI. In the mesh that is the newest version tag the
/// self-updater sees (<c>Admin/UpdatePolicy.latestAvailableTag</c>): core's <c>arm</c> job is the only
/// writer of those tags. Measured 2026-10-04: set 9898 was sealed but not armed, and a roll to it
/// failed at the mirror because <c>memex-portal-ai:3.0.0-ci.9898</c> did not exist — a sealed set the
/// fleet cannot run is not a target. Where no armed reading is available (a host without
/// self-update) the platform falls back to the newest floor a SERVED package carries: every floor is
/// <c>resolve-platform.py</c>'s answer, stamped by Plugins' <c>stamp-floors</c> on the green run that
/// verified it, so it reads CI's own output back.</para>
///
/// <para><b>Modules.</b> For each package, the version the registry serves — when its floor is at most
/// the target platform. A served version above it is <see cref="TargetStanding.AwaitingArming"/>.</para>
///
/// <para>An instance has CONVERGED when its running platform is at or above the target platform and
/// every installed package is at its served version. Pure throughout; the fleet view, the instance
/// report and the test-run step all read it from here.</para>
/// </summary>
public static class TargetSet
{
    /// <summary>The target platform: the newest ARMED set when known, else the newest served floor.
    /// Pure.</summary>
    /// <param name="served">The packages the registry serves.</param>
    /// <param name="latestArmed">The newest armed set the instance can see, or null.</param>
    public static string? Platform(IEnumerable<ServedPackage> served, string? latestArmed) =>
        !string.IsNullOrWhiteSpace(latestArmed) && PlatformReleaseOrder.BuildOrdinal(latestArmed.Trim()) is > 0
            ? latestArmed.Trim()
            : Platform(served);

    /// <summary>The newest ordered floor among the served packages, or null when none declares one —
    /// the newest set CI verified a published package on. Pure.</summary>
    /// <param name="served">The packages the registry serves.</param>
    public static string? Platform(IEnumerable<ServedPackage> served)
    {
        ArgumentNullException.ThrowIfNull(served);
        return served
            .Select(s => s.MinMeshVersion?.Trim())
            .Where(v => !string.IsNullOrEmpty(v))
            .Select(v => (Version: v!, Ordinal: PlatformReleaseOrder.BuildOrdinal(v!)))
            .Where(v => v.Ordinal is > 0)
            .OrderByDescending(v => v.Ordinal)
            .Select(v => v.Version)
            .FirstOrDefault();
    }

    /// <summary>Whether <paramref name="running"/> is at or above <paramref name="target"/> (pure).
    /// An unreadable side is NOT converged — "cannot tell" is never "up to date".</summary>
    /// <param name="running">The running platform version.</param>
    /// <param name="target">The target platform.</param>
    public static bool PlatformAtTarget(string? running, string? target)
    {
        if (string.IsNullOrWhiteSpace(target))
            return !string.IsNullOrWhiteSpace(running);
        var verdict = PlatformFloor.Evaluate(target, running);
        return verdict.Kind == PlatformFloorKind.Satisfied;
    }

    /// <summary>How many CI sets the running platform is behind the target (0 when at or above,
    /// null when either side is unordered). Pure.</summary>
    /// <param name="running">The running platform version.</param>
    /// <param name="target">The target platform.</param>
    public static long? SetsBehind(string? running, string? target)
    {
        if (string.IsNullOrWhiteSpace(running) || string.IsNullOrWhiteSpace(target))
            return null;
        var r = PlatformReleaseOrder.BuildOrdinal(running.Trim());
        var t = PlatformReleaseOrder.BuildOrdinal(target.Trim());
        return r is > 0 && t is > 0 ? Math.Max(0, t.Value - r.Value) : null;
    }

    /// <summary>Where one installed package stands (pure).</summary>
    /// <param name="installedModuleVersion">The install record's content hash.</param>
    /// <param name="served">What the registry serves for it, or null.</param>
    /// <param name="targetPlatform">The target platform, or null when unknown.</param>
    public static TargetStanding Standing(string? installedModuleVersion, ServedPackage? served, string? targetPlatform = null) =>
        served is null || string.IsNullOrWhiteSpace(served.ModuleVersion)
            ? TargetStanding.NotServed
            : string.Equals(installedModuleVersion, served.ModuleVersion, StringComparison.Ordinal)
                ? TargetStanding.AtTarget
                : !string.IsNullOrWhiteSpace(targetPlatform)
                  && PlatformFloor.Evaluate(served.MinMeshVersion, targetPlatform).IsHeld
                    ? TargetStanding.AwaitingArming
                    : TargetStanding.Behind;

    /// <summary>The served rows keyed by package id (case-insensitive; the last row for a repeated
    /// id wins). Pure.</summary>
    /// <param name="served">The packages the registry serves.</param>
    public static ImmutableDictionary<string, ServedPackage> ById(IEnumerable<ServedPackage> served) =>
        served
            .Where(s => !string.IsNullOrWhiteSpace(s.PackageId))
            .GroupBy(s => s.PackageId.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToImmutableDictionary(g => g.Key, g => g.Last(), StringComparer.OrdinalIgnoreCase);
}
