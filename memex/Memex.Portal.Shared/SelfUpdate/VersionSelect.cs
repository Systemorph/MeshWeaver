using NuGet.Versioning;

namespace Memex.Portal.Shared.SelfUpdate;

/// <summary>
/// Picks the update target from a registry's image tags, per the update policy, and decides whether a
/// target is newer than the running version. SemVer2 ordering via <see cref="NuGetVersion"/> (already
/// in the restore graph via <c>MeshWeaver.NuGet</c>), which orders the platform's tags correctly:
/// <c>3.0.0-ci.50 &lt; 3.0.0-ci.51 &lt; 3.0.0 &lt; 3.1.0</c>. Build metadata (<c>+build.&lt;ticks&gt;</c>
/// carried by the running <c>InformationalVersion</c>) is ignored in comparison, as SemVer requires.
///
/// <para>🔴 This relies on the <c>-ci.&lt;n&gt;</c> build number being MONOTONIC (see
/// <c>Directory.Build.props</c> — fed from the CI run number). A non-monotonic build number would
/// make a newer build sort lower and break "pick the newest".</para>
/// </summary>
public static class VersionSelect
{
    /// <summary>
    /// The best tag to roll to under <paramref name="policy"/>, or <c>null</c> when nothing qualifies.
    /// <see cref="UpdatePolicyKind.Continuous"/> considers every parseable tag (incl. build-numbered
    /// pre-releases); <see cref="UpdatePolicyKind.Stable"/> considers only clean releases
    /// (<c>!IsPrerelease</c>); <see cref="UpdatePolicyKind.None"/> always returns <c>null</c>.
    /// Returns the ORIGINAL tag string (so the image patch uses the exact registry tag).
    /// </summary>
    public static string? PickTarget(IEnumerable<string> tags, UpdatePolicyKind policy)
    {
        if (policy == UpdatePolicyKind.None)
            return null;

        var parsed = tags
            .Select(t => (tag: t, ver: NuGetVersion.TryParse(t, out var v) ? v : null))
            .Where(x => x.ver is not null)
            .Select(x => (x.tag, ver: x.ver!));

        if (policy == UpdatePolicyKind.Stable)
            parsed = parsed.Where(x => !x.ver.IsPrerelease);

        return parsed
            .OrderByDescending(x => x.ver)
            .Select(x => x.tag)
            .FirstOrDefault();
    }

    /// <summary>
    /// True if <paramref name="targetTag"/> is strictly newer than <paramref name="currentVersion"/>.
    /// An unparseable current version (e.g. <c>"unknown"</c> on an unstamped build) returns
    /// <c>false</c> — we never auto-update when we can't establish the running version.
    /// </summary>
    public static bool IsNewer(string targetTag, string currentVersion)
    {
        if (!NuGetVersion.TryParse(targetTag, out var target))
            return false;
        if (!NuGetVersion.TryParse(currentVersion, out var current))
            return false;
        return target > current;
    }
}
