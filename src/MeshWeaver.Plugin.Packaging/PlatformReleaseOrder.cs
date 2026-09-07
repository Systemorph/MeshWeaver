using System.Globalization;

namespace MeshWeaver.Plugin.Packaging;

/// <summary>
/// 🚨 <b>"Is this platform build newer than that one?" — the ONE answer, for every caller that asks
/// (#3542).</b>
///
/// <para><see cref="NuGetVersionComparer"/> beside this file orders version STRINGS, correctly and by
/// the SemVer spec. That is the right answer to a different question. This type answers the question
/// the platform actually has: <b>which of these two builds was published later.</b> The two diverge
/// because a version line is a LABEL a human maintains in <c>Directory.Build.props</c>, while the
/// <c>ci.&lt;run&gt;</c> suffix is the GitHub Actions run number of the delivery workflow that
/// published the image — produced by the machine, monotonic, and never wrong.</para>
///
/// <para><b>Two measured incidents, one wrong assumption — that the `ci` line and the `rc` line are
/// SemVer-comparable.</b></para>
/// <list type="number">
/// <item><b>The self-updater's target selection.</b> <c>Directory.Build.props</c> briefly read
/// <c>3.1.0</c> on 2026-09-05, so ten sets published as <c>3.1.0-ci.7832…7841</c> and were withdrawn.
/// By SemVer those outrank every later, SEALED <c>3.0.0-ci.79xx</c> set for ever; on 2026-09-07 both
/// AKS portals rolled themselves onto <c>3.1.0-ci.7841</c>, three days behind, and could not leave —
/// nothing is ever newer than the highest-sorting tag.</item>
/// <item><b>The module platform floor</b> (<c>ModulePlatformFloor.DeclineReason</c>). SemVer §11.4
/// compares pre-release identifiers as text, so <c>"ci"</c> &lt; <c>"rc"</c> and therefore
/// <c>3.0.0-ci.N &lt; 3.0.0-rc8</c> for <b>every</b> N — measured against the real comparer for
/// N = 1, 7981, 7989 and 999999999. A module declaring a floor of <c>3.0.0-rc8</c> is unsatisfiable
/// by any build of the clean <c>3.0.0-ci</c> line, permanently, and the decline names two versions
/// that look like they are in the right order.</item>
/// </list>
///
/// <para>So the notion lives HERE — in the lowest assembly both call sites already reference
/// (<c>MeshWeaver.PluginCatalog</c> and <c>Memex.Portal.Shared</c> both reference this project
/// directly) — rather than privately inside either of them. Two private ideas of "newer" is how one
/// fix leaves the other call site broken.</para>
///
/// <para>🚨 <b><see cref="Compare"/> is PAIRWISE and is deliberately NOT an
/// <see cref="IComparer{T}"/>.</b> Over a mixed set the relation is not transitive — a slip tag beats
/// a promotion beats a sealed tag beats the slip tag — and an intransitive comparer hands a sort an
/// arbitrary answer. A caller that must ORDER a heterogeneous set bands by
/// <see cref="BuildOrdinal"/><c>.HasValue</c> first and only then compares within a band;
/// <c>VersionSelect.PickTargets</c> is the reference implementation.</para>
/// </summary>
public static class PlatformReleaseOrder
{
    /// <summary>
    /// The pre-release identifiers that mark a delivery CHANNEL and are FOLLOWED by the publishing
    /// run number: <c>3.0.0-ci.7977</c>, the retired <c>3.0.0-rc9.ci.7824</c>, and the unverified
    /// <c>3.0.0-edge.7977</c> (<c>edge-images.yml</c> rewrites <c>.ci.</c> to <c>.edge.</c> and keeps
    /// the same number).
    /// </summary>
    private static readonly string[] ChannelLabels = ["ci", "edge"];

    /// <summary>
    /// The CD run number <paramref name="version"/> was published by — its SEALED-PUBLICATION
    /// LINEAGE — or <c>null</c> for a version that carries none.
    ///
    /// <para>Read as the numeric identifier that FOLLOWS a channel label, so every shape the pipeline
    /// produces answers the same way, both separators included (<c>Directory.Build.props</c> requires
    /// both: a clean line starts the pre-release with <c>-ci.N</c>, a labelled one appends
    /// <c>.ci.N</c>, and the retired rc tags used the latter):</para>
    /// <list type="bullet">
    /// <item><c>3.0.0-ci.7977</c> → <c>7977</c></item>
    /// <item><c>3.0.0-rc9.ci.7824</c> → <c>7824</c></item>
    /// <item><c>3.0.0-edge.7977</c> → <c>7977</c></item>
    /// <item><c>3.0.0-ci.7977+build.638</c> → <c>7977</c> (build metadata is not part of ordering)</item>
    /// <item><c>3.0.0</c> → <c>null</c> — an official release is a PROMOTION, a retag of one already
    /// sealed continuous set, so its lineage lives on the sibling tag it was cut from and cannot be
    /// read off the tag at all.</item>
    /// </list>
    /// </summary>
    public static long? BuildOrdinal(string? version)
    {
        if (!TrySplit(version, out _, out var preRelease))
            return null;

        for (var i = 0; i < preRelease.Length - 1; i++)
            if (ChannelLabels.Contains(preRelease[i], StringComparer.OrdinalIgnoreCase)
                && long.TryParse(
                    preRelease[i + 1], NumberStyles.None, CultureInfo.InvariantCulture, out var ordinal))
                return ordinal;

        return null;
    }

    /// <summary>
    /// Which of two platform versions is newer: negative when <paramref name="left"/> is older, zero
    /// when neither is newer, positive when <paramref name="left"/> is newer — and <b>null when the
    /// question cannot be answered</b>, i.e. either side is not a platform version at all
    /// (<c>"unknown"</c> on an unstamped build, a git sha, a moving pointer like <c>main</c>).
    ///
    /// <para>🚨 The nullable return is the point, and it is the same rule the rest of this change
    /// rests on: a comparison that had to READ something must not report a failed read as a real
    /// answer. A caller decides what "could not tell" means for it — an updater refuses to roll, a
    /// floor check refuses to land on faith — but neither may silently receive "older" or "newer".
    /// The hand-rolled <see cref="NuGetVersionComparer"/> alone would answer <c>1</c> for
    /// <c>Compare("3.0.0", "unknown")</c>, because an unparseable core reads as zero.</para>
    ///
    /// <para><b>The rule.</b> When BOTH sides carry a <see cref="BuildOrdinal"/> they are both
    /// continuous publications, and the run number decides — the version LINE in front of it is
    /// ignored, which is exactly what makes a mislabelled line lose. When either side does not, the
    /// version string is the only key they share, and for a deliberately cut release it is a
    /// trustworthy one, so <see cref="NuGetVersionComparer"/> answers.</para>
    /// </summary>
    public static int? Compare(string? left, string? right)
    {
        if (!TrySplit(left, out _, out _) || !TrySplit(right, out _, out _))
            return null;

        if (BuildOrdinal(left) is { } leftOrdinal && BuildOrdinal(right) is { } rightOrdinal)
            return leftOrdinal.CompareTo(rightOrdinal);

        return Math.Sign(NuGetVersionComparer.Instance.Compare(left, right));
    }

    /// <summary>
    /// True when <paramref name="candidate"/> was published strictly later than
    /// <paramref name="current"/>. A question that cannot be answered is <c>false</c> — never update
    /// on a comparison nobody could make.
    /// </summary>
    public static bool IsNewer(string? candidate, string? current) => Compare(candidate, current) > 0;

    /// <summary>
    /// A platform version is a NUMERIC dotted core (1–4 components, matching what a .NET assembly
    /// version can carry) optionally followed by a pre-release and/or build metadata. Deliberately
    /// permissive about the core's component count — an unstamped build falls back to the assembly's
    /// <c>3.0.0.0</c>, which is comparable — and deliberately strict about it being NUMERIC, which is
    /// what separates a version from <c>unknown</c>, a git sha, or <c>main</c>.
    /// </summary>
    private static bool TrySplit(string? version, out string core, out string[] preRelease)
    {
        core = "";
        preRelease = [];
        if (string.IsNullOrWhiteSpace(version))
            return false;

        var text = version;
        var plus = text.IndexOf('+');
        if (plus >= 0)
            text = text[..plus];

        var dash = text.IndexOf('-');
        core = dash < 0 ? text : text[..dash];
        preRelease = dash < 0 ? [] : text[(dash + 1)..].Split('.');

        var parts = core.Split('.');
        if (parts.Length is < 1 or > 4)
            return false;

        foreach (var part in parts)
            if (!long.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out _))
                return false;

        return true;
    }
}
