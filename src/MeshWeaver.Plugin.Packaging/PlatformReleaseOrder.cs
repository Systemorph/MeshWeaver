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
/// arbitrary answer. A caller that must ORDER a heterogeneous set uses <see cref="Newest"/>, which
/// bands by <see cref="BuildOrdinal"/><c>.HasValue</c> first and only then compares within a band.
/// </para>
/// </summary>
public static class PlatformReleaseOrder
{
    /// <summary>
    /// The pre-release identifiers that mark a delivery CHANNEL and are FOLLOWED by the publishing
    /// run number: <c>3.0.0-ci.7977</c>, the retired <c>3.0.0-rc9.ci.7824</c>, and the unverified
    /// <c>3.0.0-edge.7977</c> (<c>edge-images.yml</c> rewrites the <c>ci</c> label to <c>edge</c>
    /// and keeps the same number).
    /// </summary>
    private static readonly string[] ChannelLabels = ["ci", "edge"];

    /// <summary>
    /// 🚨 <b>The label of a build that publishes NO platform version</b> — a local source build, a
    /// pull-request run, a satellite lane compiling core from source: <c>&lt;major&gt;.&lt;minor&gt;.0-dev</c>
    /// (<c>Directory.Build.props</c>, policy <c>platform-semver-versioning</c>). Read as run
    /// <c>0</c>, the same reading the retired local stamp <c>-ci.0</c> had, so every caller that
    /// treats ordinal 0 as "a source build, never ordered against a publication"
    /// (<c>PlatformFloor</c>, <c>PlatformCompatibility.ProducerIsNewer</c>,
    /// <c>NodeTypeCompilationHelpers.IsOrderedPlatformBuild</c>) keeps its meaning without a second
    /// spelling to recognise.
    /// </summary>
    public const string SourceBuildLabel = "dev";

    /// <summary>
    /// True when <paramref name="version"/> is a SOURCE build — <c>X.Y.Z-dev</c>, or the retired
    /// local stamp <c>X.Y.Z-ci.0</c> — i.e. a build that was never published and is never ordered
    /// against one. Its <see cref="BuildOrdinal"/> is <c>0</c>.
    /// </summary>
    public static bool IsSourceBuild(string? version) => BuildOrdinal(version) == 0;

    /// <summary>
    /// 🚨 <b>The first line of the SemVer notation</b> — policy <c>platform-semver-versioning</c>
    /// (<c>Doc/Architecture/PlatformVersioning</c>). From this <c>major.minor</c> on, a continuous
    /// build is published as a PLAIN SemVer version <c>&lt;major&gt;.&lt;minor&gt;.&lt;run&gt;</c>
    /// (<c>3.1.10050</c>): no pre-release label, and the PATCH is the same monotonic CD run number
    /// the <c>-ci.&lt;run&gt;</c> suffix carried before it. Below it a clean version (<c>3.0.0</c>)
    /// is still a PROMOTION with no run number of its own, exactly as it always was.
    ///
    /// <para>The boundary is a declared constant, never inferred from "the patch looks big": a
    /// version is read as a build of the new notation only when its line is at or above this one,
    /// so no tag published under the old notation changes meaning — <c>3.0.0</c>, <c>3.0.0-rc8</c>
    /// and the withdrawn slip <c>3.1.0-ci.7841</c> (whose <c>ci</c> label still decides) read
    /// exactly as before.</para>
    /// </summary>
    public static (int Major, int Minor) SemVerEraStart { get; } = (3, 1);

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
    /// <item><c>3.1.0-dev</c> → <c>0</c> — a source build (<see cref="SourceBuildLabel"/>), never
    /// ordered against a publication.</item>
    /// <item><c>3.0.0</c> → <c>null</c> — an official release is a PROMOTION, a retag of one already
    /// sealed continuous set, so its lineage lives on the sibling tag it was cut from and cannot be
    /// read off the tag at all.</item>
    /// <item><c>3.1.10050</c> → <c>10050</c> — the SemVer notation (<see cref="SemVerEraStart"/>,
    /// policy <c>platform-semver-versioning</c>): the run number is the PATCH, so the old and the new
    /// notation share ONE lineage and interleave by publication order (<c>3.0.0-ci.9999</c> &lt;
    /// <c>3.1.10000</c>).</item>
    /// </list>
    /// </summary>
    public static long? BuildOrdinal(string? version)
    {
        if (!TrySplit(version, out _, out var preRelease))
            return null;

        // A source build (`3.1.0-dev`) carries no publication — run 0, as the retired `-ci.0` stamp.
        if (preRelease is [var only] && string.Equals(only, SourceBuildLabel, StringComparison.OrdinalIgnoreCase))
            return 0;

        for (var i = 0; i < preRelease.Length - 1; i++)
            if (ChannelLabels.Contains(preRelease[i], StringComparer.OrdinalIgnoreCase)
                && long.TryParse(
                    preRelease[i + 1], NumberStyles.None, CultureInfo.InvariantCulture, out var ordinal))
                return ordinal;

        // The SemVer notation (policy `platform-semver-versioning`): the run number IS the patch.
        // Read only AFTER the channel-label loop, so a `-ci.<n>` / `-edge.<n>` suffix still decides
        // wherever one is present — the withdrawn `3.1.0-ci.7841` keeps its 7841.
        return SemVerBuildPatch(version);
    }

    /// <summary>
    /// True when <paramref name="version"/> is a build of the SemVer notation — a plain
    /// <c>&lt;major&gt;.&lt;minor&gt;.&lt;run&gt;</c> on a line at or above
    /// <see cref="SemVerEraStart"/>, optionally carrying the bare <c>edge</c> label an unverified
    /// build is marked with (<c>3.1.10050-edge</c>). Its <see cref="BuildOrdinal"/> is the patch.
    ///
    /// <list type="bullet">
    /// <item><c>3.1.10050</c> → true (ordinal 10050)</item>
    /// <item><c>3.0.0</c>, <c>3.1.0</c>, <c>4.0.0</c> → false — a zero patch is a floor or a
    /// release, never a CD run</item>
    /// <item><c>3.0.0-ci.9999</c>, <c>3.1.0-ci.7841</c>, <c>3.1.0.5</c>, <c>2.5.3</c> → false</item>
    /// </list>
    /// </summary>
    public static bool IsSemVerBuild(string? version) => SemVerBuildPatch(version) is not null;

    private static long? SemVerBuildPatch(string? version)
    {
        if (!TrySplit(version, out var core, out var preRelease))
            return null;
        if (preRelease.Length > 1
            || (preRelease.Length == 1 && !string.Equals(preRelease[0], "edge", StringComparison.OrdinalIgnoreCase)))
            return null;

        var parts = core.Split('.');
        if (parts.Length != 3)
            return null;
        var major = long.Parse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture);
        var minor = long.Parse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture);
        if (major < SemVerEraStart.Major || (major == SemVerEraStart.Major && minor < SemVerEraStart.Minor))
            return null;

        // 🚨 A ZERO patch is NOT a build of the notation: `3.1.0`, `4.0.0`, `999.0.0` are what a
        // declared FLOOR ("needs the 3.1 line") and a deliberately cut release look like, and they
        // must keep being compared by their numeric core. A CD run number is never 0; the local
        // source-build stamp of the new notation is `<major>.<minor>.0-dev` (Directory.Build.props),
        // which BuildOrdinal reads as ordinal 0 before it gets here.
        var patch = long.Parse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture);
        return patch > 0 ? patch : null;
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
    /// 🚨 <b>The ONE total order over a set of platform versions — ASCENDING, so "newest first" is
    /// <c>OrderByDescending(x, PlatformReleaseOrder.Newest)</c>.</b> Every caller that has to RANK
    /// platform builds against each other uses this and nothing else (#3542).
    ///
    /// <para><b>Why it is not <see cref="Compare"/>.</b> Compare answers a PAIRWISE question and
    /// falls back to SemVer whenever one side carries no run number — correct for two versions, and
    /// intransitive over a SET: the slip tag <c>3.1.0-ci.7841</c> beats the promotion <c>3.0.0</c>
    /// (SemVer), the promotion beats the sealed <c>3.0.0-ci.8130</c> (SemVer), and the sealed tag
    /// beats the slip tag (run number). A sort handed a cycle produces an arbitrary answer, which is
    /// how a "fixed" ordering silently keeps picking the wrong build.</para>
    ///
    /// <para><b>The order, and why it is total.</b> Three keys, lexicographically, each a total
    /// order on its own:</para>
    /// <list type="number">
    /// <item><b>the band</b> — a version carrying a <see cref="BuildOrdinal"/> outranks one that does
    /// not. A build the machine published outranks a string nobody can place;</item>
    /// <item><b>the run number</b>, within the lineage band. The version LINE in front of it is
    /// ignored, which is precisely what makes a mislabelled line lose;</item>
    /// <item><b>SemVer</b> (<see cref="NuGetVersionComparer"/>) — the tie-break inside the lineage
    /// band, and the whole of the order in the promotion band, where the version string is the only
    /// key the members share and a deliberately cut release makes it a trustworthy one.</item>
    /// </list>
    ///
    /// <para>🚨 <b>This is an ORDER, never a THRESHOLD.</b> "Is the platform at least X" — a declared
    /// <c>minMeshVersion</c> floor — is a different predicate over the same key and must not be
    /// answered with this comparer: for an updater the clean <c>3.0.0</c> must outrank
    /// <c>3.0.0-ci.7977</c> (or a Stable install can never reach the release it waits for), while a
    /// <c>3.0.0</c> FLOOR must be SATISFIED by <c>3.0.0-ci.7977</c>. Same two strings, opposite
    /// required answers. See <c>Doc/Architecture/SelfUpdateTargetSelection</c> §4.</para>
    /// </summary>
    public static IComparer<string> Newest { get; } = new TotalOrder();

    private sealed class TotalOrder : IComparer<string>
    {
        public int Compare(string? left, string? right)
        {
            var leftOrdinal = BuildOrdinal(left);
            var rightOrdinal = BuildOrdinal(right);

            if (leftOrdinal.HasValue != rightOrdinal.HasValue)
                return leftOrdinal.HasValue ? 1 : -1;

            if (leftOrdinal is { } l && rightOrdinal is { } r && l != r)
                return l.CompareTo(r);

            return Math.Sign(NuGetVersionComparer.Instance.Compare(left, right));
        }
    }

    /// <summary>
    /// A platform version is a NUMERIC dotted core (1–4 components, matching what a .NET assembly
    /// version can carry) optionally followed by a pre-release and/or build metadata. Deliberately
    /// permissive about the core's component count — an unstamped build falls back to the assembly's
    /// <c>3.0.0.0</c>, which is comparable — and deliberately strict about it being NUMERIC, which is
    /// what separates a version from <c>unknown</c>, a git sha, or <c>main</c>.
    /// </summary>
    internal static bool TrySplit(string? version, out string core, out string[] preRelease)
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
