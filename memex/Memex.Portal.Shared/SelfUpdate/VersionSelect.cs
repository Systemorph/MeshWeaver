using System.Text.RegularExpressions;
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
    // The multi-arch container build publishes, per version, a manifest-list tag (e.g. 3.0.0-ci.43) PLUS
    // one image per RID (3.0.0-ci.43-linux-x64, 3.0.0-ci.43-linux-arm64). The RID suffix parses as an extra
    // SemVer pre-release identifier that sorts ABOVE the clean tag (numeric 43 < alphanumeric 43-linux-x64),
    // so without this filter PickTarget rolls to the x64-only image — wrong arch on an arm64 node, and never
    // the intended manifest list. Drop RID-suffixed tags; the manifest list is the canonical deploy tag.
    private static readonly Regex RuntimeIdentifierSuffix =
        new(@"-(linux|win|osx)-(x64|x86|arm|arm64)$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // 🔴 A real platform tag is DOTTED SemVer (3.0.0 / 3.0.0-ci.133). The multi-arch build ALSO pushes a
    // 7-char git-sha tag per version (e.g. 6943991, 4779b4e) and a bare `main` tag — those are NOT deploy
    // targets. But NuGetVersion.TryParse accepts a bare number, so an ALL-DIGIT sha like "6943991" parses
    // as the version 6943991.0.0 and sorts ABOVE every real release (3.x). PickTarget would then pick that
    // sha and pin the self-updater to whatever release it belongs to — the prod symptom where every portal
    // froze on ci.122 (whose sha, 6943991, is all digits) and reverted any manual roll to a newer ci.N
    // (ci.133's sha, 4779b4e, has letters so it never even parsed). Require the MAJOR.MINOR.PATCH dotted
    // shape that a git-sha / `main` can never have.
    private static readonly Regex PlatformVersionTag =
        new(@"^\d+\.\d+\.\d+([-+].*)?$", RegexOptions.Compiled);

    /// <summary>
    /// The best tag to roll to under <paramref name="policy"/>, or <c>null</c> when nothing qualifies.
    /// <see cref="UpdatePolicyKind.Continuous"/> considers every parseable tag (incl. build-numbered
    /// pre-releases); <see cref="UpdatePolicyKind.Stable"/> considers only clean releases
    /// (<c>!IsPrerelease</c>); <see cref="UpdatePolicyKind.None"/> always returns <c>null</c>.
    /// Returns the ORIGINAL tag string (so the image patch uses the exact registry tag).
    /// </summary>
    public static string? PickTarget(IEnumerable<string> tags, UpdatePolicyKind policy, bool requireCiGreen = true)
    {
        if (policy == UpdatePolicyKind.None)
            return null;

        var parsed = tags
            .Where(t => !RuntimeIdentifierSuffix.IsMatch(t))   // exclude per-RID image tags; keep the manifest list
            .Where(t => PlatformVersionTag.IsMatch(t))         // exclude bare git-sha / `main` tags (see PlatformVersionTag)
            .Select(t => (tag: t, ver: NuGetVersion.TryParse(t, out var v) ? v : null))
            .Where(x => x.ver is not null)
            .Select(x => (x.tag, ver: x.ver!));

        if (policy == UpdatePolicyKind.Stable)
            parsed = parsed.Where(x => !x.ver.IsPrerelease);

        // CI-green gate: the verified channel (continuous delivery, which builds+pushes ONLY when the
        // test workflow is green) never carries the `edge` pre-release label. An unverified "edge"
        // channel (publish-on-every-build, e.g. `3.0.0-edge.51`) would. requireCiGreen excludes those,
        // so the install never auto-rolls to a build that hasn't passed CI. Off => edge builds eligible.
        if (requireCiGreen)
            parsed = parsed.Where(x => !IsEdge(x.ver));

        return parsed
            .OrderByDescending(x => x.ver)
            .Select(x => x.tag)
            .FirstOrDefault();
    }

    /// <summary>An UNVERIFIED edge/pre-merge build — identified by an <c>edge</c> SemVer pre-release
    /// label (e.g. <c>3.0.0-edge.51</c>). Verified CD builds use <c>-ci.&lt;n&gt;</c> or a clean release,
    /// never <c>edge</c>.</summary>
    private static bool IsEdge(NuGetVersion version) =>
        version.ReleaseLabels.Any(label => string.Equals(label, "edge", StringComparison.OrdinalIgnoreCase));

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
