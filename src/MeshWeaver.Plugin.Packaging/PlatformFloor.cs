namespace MeshWeaver.Plugin.Packaging;

/// <summary>
/// The outcome vocabulary of <see cref="PlatformFloor.Evaluate"/> — OPEN string constants
/// (policy <c>open-vocabulary-string-constants</c>): the kind rides log lines, status rows and
/// install records, and a consumer never switches over it exhaustively. Only <see cref="Held"/>
/// withholds anything; every other kind proceeds.
/// </summary>
public static class PlatformFloorKind
{
    /// <summary>The package declares no floor — there is nothing to check.</summary>
    public const string None = "None";

    /// <summary>The floor is comparable with the running platform and the running platform is at or
    /// above it.</summary>
    public const string Satisfied = "Satisfied";

    /// <summary>
    /// The floor could NOT be ordered against the running platform — an unreadable floor, an
    /// unknown running version, a source build (<c>-dev</c>, or the retired <c>-ci.0</c>), or two shapes that share no
    /// order (an <c>rc</c> label against a <c>ci</c> build). Proceeds, and the reason is logged as
    /// advisory: a comparison nobody could make must never hold a portal (the 2026-09-07 trap,
    /// <c>Doc/Architecture/ModuleAdoptionPolicy</c> R2).
    /// </summary>
    public const string Advisory = "Advisory";

    /// <summary>The floor is comparable with the running platform and strictly ABOVE it — the
    /// candidate is not used; an installed version keeps running.</summary>
    public const string Held = "Held";
}

/// <summary>
/// One floor decision: the kind, and the sentence naming both versions.
/// </summary>
/// <param name="Kind">A <see cref="PlatformFloorKind"/> value.</param>
/// <param name="Floor">The declared floor as given, or null.</param>
/// <param name="Running">The running platform version as given, or null.</param>
/// <param name="Reason">The human sentence — null only for <see cref="PlatformFloorKind.None"/>
/// and <see cref="PlatformFloorKind.Satisfied"/>.</param>
public sealed record PlatformFloorVerdict(string Kind, string? Floor, string? Running, string? Reason)
{
    /// <summary>True only for <see cref="PlatformFloorKind.Held"/>: the candidate must not be used
    /// on this platform.</summary>
    public bool IsHeld => Kind == PlatformFloorKind.Held;
}

/// <summary>
/// 🚨 <b>"Does the running platform satisfy this package version's declared
/// <c>minMeshVersion</c>?" — THE one answer, for every consumer that installs, updates, lands or
/// syncs a package version</b> (policy <c>package-min-mesh-version</c>,
/// <c>Doc/Architecture/ModuleAdoptionPolicy</c> R2). Pure.
///
/// <para><b>The rule.</b> A package version declares <c>minMeshVersion</c>; an installation uses it
/// only when its running platform is at or above that floor. Only a floor that is COMPARABLE with
/// the running version and strictly ABOVE it holds. Everything that cannot be ordered proceeds and
/// is reported as <see cref="PlatformFloorKind.Advisory"/>.</para>
///
/// <para><b>Why a predicate of its own, and not <see cref="PlatformReleaseOrder.Newest"/>.</b> An
/// ORDER and a THRESHOLD need opposite answers for the same two strings: for the self-updater the
/// clean <c>3.0.0</c> outranks <c>3.0.0-ci.7977</c>, while a <c>3.0.0</c> FLOOR must be SATISFIED
/// by <c>3.0.0-ci.7977</c> (<c>Doc/Architecture/SelfUpdateTargetSelection</c> §4). And
/// <see cref="NuGetVersionComparer"/> alone is the 2026-09-07 trap: it ranks <c>ci &lt; rc &lt;
/// clean</c>, so an <c>rc</c> or clean floor could never be met by any <c>ci</c> build and every
/// portal was held on its morning build.</para>
///
/// <para><b>How two versions are compared</b> — the run number where both have one, the numeric
/// core otherwise, and nothing else:</para>
/// <list type="number">
/// <item>Both carry a run number (<see cref="PlatformReleaseOrder.BuildOrdinal"/>): the run number
/// decides, the version line in front of it is ignored (a mislabelled line loses).</item>
/// <item>Either run number is <c>0</c>: a LOCAL source build stamp, not a publication — advisory.</item>
/// <item>Otherwise the NUMERIC cores decide: a floor core above the running core holds, below it is
/// satisfied. On EQUAL cores a clean floor (<c>3.0.0</c>) is satisfied by any build of its line; a
/// floor carrying a label (<c>3.0.0-rc8</c>, <c>3.0.0-ci.N</c> against a release) shares no order
/// with the running version and is advisory — unless neither side carries a label, where they are
/// equal and therefore satisfied.</item>
/// </list>
/// </summary>
public static class PlatformFloor
{
    /// <summary>
    /// Decides one floor against one running platform version. Never throws.
    /// </summary>
    /// <param name="minMeshVersion">The package version's declared floor, or null/blank for none.</param>
    /// <param name="runningPlatformVersion">The running platform version (production passes
    /// <c>PlatformBuildInfo.RunningPlatformVersion</c>), or null when unknown.</param>
    public static PlatformFloorVerdict Evaluate(string? minMeshVersion, string? runningPlatformVersion)
    {
        if (string.IsNullOrWhiteSpace(minMeshVersion))
            return new(PlatformFloorKind.None, null, runningPlatformVersion, null);

        var floor = minMeshVersion.Trim();
        var running = string.IsNullOrWhiteSpace(runningPlatformVersion) ? null : runningPlatformVersion.Trim();

        PlatformFloorVerdict Advisory(string why) => new(PlatformFloorKind.Advisory, floor, running,
            $"declares platform ≥ {floor}; running {running ?? "(unknown)"} — {why}, so the floor "
            + "cannot hold this package (advisory)");

        if (running is null || !PlatformReleaseOrder.TrySplit(running, out var runningCore, out var runningLabel))
            return Advisory("the running platform version could not be read");
        if (!PlatformReleaseOrder.TrySplit(floor, out var floorCore, out var floorLabel))
            return Advisory("the declared floor is not a platform version");

        var floorOrdinal = PlatformReleaseOrder.BuildOrdinal(floor);
        var runningOrdinal = PlatformReleaseOrder.BuildOrdinal(running);
        if (floorOrdinal == 0 || runningOrdinal == 0)
            return Advisory("a source build (-dev, or the retired -ci.0) is not a publication, and is not "
                            + "ordered against a published build");

        // 🚨 LINE FIRST (policy major-lines-run-separately, MeshWeaver.Plugins#5550). The run counter
        // is shared across platform lines, so a run number orders two builds of ONE line and nothing
        // else: a 4.0.13296 floor read as met on 3.1.13396 because 13296 < 13396. A floor on a HIGHER
        // line is never met — the build was made for a platform this one is not. A floor on a LOWER
        // line is not met either, only unordered: it proceeds as advisory, never as "satisfied", and
        // the bundle-level line rule (BundleLine) is what keeps another line's bytes off this process.
        var floorLine = LineOfCore(floorCore);
        var runningLine = LineOfCore(runningCore);
        if (floorLine != runningLine)
            return floorLine > runningLine
                ? new(PlatformFloorKind.Held, floor, running,
                    $"needs platform ≥ {floor}, which is platform line {floorLine}; running {running} "
                    + $"is line {runningLine}, and a floor on another line is never met here — held "
                    + "(run numbers are shared across lines and order nothing between them)")
                : Advisory($"the floor names platform line {floorLine} and this is line {runningLine}; "
                           + "run numbers are shared across lines and order nothing between them");

        int comparison;
        if (floorOrdinal is { } f && runningOrdinal is { } r)
            comparison = f.CompareTo(r);
        else
        {
            comparison = Math.Sign(NuGetVersionComparer.Instance.Compare(floorCore, runningCore));
            if (comparison == 0 && floorLabel.Length > 0)
                // Same core, and the floor carries a label (rc8, or a ci run against a release):
                // the two share no order. Treating it as unmet is exactly the 2026-09-07 hold.
                return runningLabel.Length > 0 || floorOrdinal is not null
                    ? Advisory($"a '{string.Join('.', floorLabel)}' floor and the running build share no "
                               + "order")
                    : Advisory($"a '{string.Join('.', floorLabel)}' floor is not ordered against the "
                               + "release it names");
        }

        return comparison > 0
            ? new(PlatformFloorKind.Held, floor, running,
                $"needs platform ≥ {floor}, running {running} — held until the platform rolls to it")
            : new(PlatformFloorKind.Satisfied, floor, running, null);
    }

    /// <summary>
    /// The last platform major whose builds share ONE line: every major at or below it is that line
    /// (the same value as <c>BundleLine.LegacyLine</c> and <c>ModuleRoot.LastSharedRootMajor</c> in
    /// MeshWeaver.PluginCatalog, which a test holds equal).
    /// </summary>
    public const int LegacyLine = 3;

    /// <summary>The line a platform major belongs to: <see cref="LegacyLine"/> for every major at or
    /// below it, the major itself after it. Pure.</summary>
    /// <param name="major">A platform major.</param>
    public static int LineOf(int major) => major <= LegacyLine ? LegacyLine : major;

    /// <summary>The line of a numeric version core (<c>4.0.0</c> → 4, <c>3.1.0</c> → 3). Pure.</summary>
    private static int LineOfCore(string core)
    {
        var dot = core.IndexOf('.');
        return LineOf(int.TryParse(dot < 0 ? core : core[..dot], System.Globalization.NumberStyles.None,
            System.Globalization.CultureInfo.InvariantCulture, out var major) ? major : 0);
    }

    /// <summary>
    /// Whether ANY floor could be held against <paramref name="runningPlatformVersion"/> (pure):
    /// false for an unknown or unreadable running version and for a source build (<c>-dev</c>, the
    /// retired <c>-ci.0</c>), against which <see cref="Evaluate"/> answers
    /// <see cref="PlatformFloorKind.Advisory"/> whatever the floor says. A reader that would have
    /// to fetch a floor before judging it can skip the fetch when this is false — the answer is
    /// already known to be "proceed".
    /// </summary>
    /// <param name="runningPlatformVersion">The running platform version, or null when unknown.</param>
    public static bool CanHold(string? runningPlatformVersion) =>
        !string.IsNullOrWhiteSpace(runningPlatformVersion)
        && PlatformReleaseOrder.TrySplit(runningPlatformVersion.Trim(), out _, out _)
        && PlatformReleaseOrder.BuildOrdinal(runningPlatformVersion.Trim()) != 0;

    /// <summary>
    /// The held sentence alone — non-null ONLY when <see cref="Evaluate"/> answers
    /// <see cref="PlatformFloorKind.Held"/>. The shape a <c>Func&lt;string?, string?&gt;</c> gate
    /// takes.
    /// </summary>
    public static string? HoldReason(string? minMeshVersion, string? runningPlatformVersion)
    {
        var verdict = Evaluate(minMeshVersion, runningPlatformVersion);
        return verdict.IsHeld ? verdict.Reason : null;
    }
}
