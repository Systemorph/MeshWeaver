using Microsoft.Extensions.Logging;

namespace MeshWeaver.Hosting;

/// <summary>
/// One source's publication under ONE framework identity, as the registry sealed it for the
/// instance running that identity: which producing repository it came from (when the lane
/// recorded it), the commit it was baked from, and whether it is sealed at all.
/// </summary>
/// <param name="Source">The bake-source segment (<c>plugins</c>, <c>education</c>, …).</param>
/// <param name="Repository">The producing repository as <c>owner/name</c>, from
/// <see cref="SealedPublicationIndex.RepositoryMarkerFileName"/>; null when the seal predates that
/// marker.</param>
/// <param name="SourceCommit">The producing repository's commit the publication was baked from,
/// from <see cref="SealedPublicationIndex.SourceCommitMarkerFileName"/>; null when absent or
/// recorded as unknown.</param>
/// <param name="IsSealed">True when the completion sentinel is present and every bundle it lists
/// is on disk — the same rule the boot seeder judges by.</param>
/// <param name="Refusal">Why <paramref name="IsSealed"/> is false, or null.</param>
public sealed record SealedSource(
    string Source, string? Repository, string? SourceCommit, bool IsSealed, string? Refusal);

/// <summary>
/// A platform publication LINE: the framework identity a set of bundles was sealed under, and the
/// newest platform version published on it. The unit <see cref="SealedPublicationIndex.ReleasesOf"/>
/// answers in, named so a hold can state one.
/// </summary>
/// <param name="Identity">The framework identity the publication was sealed under.</param>
/// <param name="Version">The newest platform version published under it, by sealed-publication
/// lineage — never SemVer (see <see cref="SealedPublicationIndex.ReleasesOf"/>).</param>
public sealed record PublicationLine(string Identity, string Version);

/// <summary>
/// Whether a reading of the sealed index is a STATEMENT ABOUT THE WORLD or a failure to look.
///
/// <para>🚨 Both used to be the empty list, and <c>SealedSyncGate</c> reads an empty list as
/// "no seal is attributable to this repository, so this gate is not its business" — i.e. it lets
/// the source ADVANCE. A total reader failure therefore passed every repository, which is the
/// exact inversion of the rule the gate exists to enforce, with nothing red anywhere. The
/// published root's own layout migration (#3461 phase 5, dropping the flat compatibility copy) is
/// the documented trigger: this reader would find no sentinel, report every source unsealed, and
/// silently switch the whole "advance only to the commit sealed for this instance" rule off at the
/// moment it matters most.</para>
/// </summary>
public enum SealedReadOutcome
{
    /// <summary>No published root or no identity was configured — this instance seeds from
    /// nothing, and the gate genuinely does not apply. An empty list here is an ANSWER.</summary>
    NotConfigured,

    /// <summary>The root was enumerated. The list is what is sealed for this identity, and an
    /// empty one means exactly that — including a root that holds no directory for this identity
    /// yet, which is an ordinary state for a freshly-built framework.</summary>
    Read,

    /// <summary>🚨 The root is configured and the reading FAILED — the enumeration threw, a FILE
    /// sits where the identity directory should be, a seal could not be opened, or a source's
    /// publication pointer EXISTS and could not be followed with no sealed publication behind it
    /// (#3461 phase 5: the flat compatibility copy is disposed of once a generation is live, so
    /// that fall-back now finds nothing). The list is the absence of a measurement, never a clean
    /// one — "cannot tell" is never "clear to proceed", and here it is not even per repository: a
    /// source nobody could attribute may be ANY repository's.</summary>
    Unreadable,
}

/// <summary>
/// Reads what the registry SEALED for one framework identity — the publication set the instance
/// running that identity boots from (<see cref="ShippedPrebuiltBundles.SeedPublishedRoot"/>) —
/// as data a decision can be made on, per source: producing repository, source commit, sealed or
/// not. It is the reading behind "may this instance's sources advance to that commit?"
/// (MeshWeaver.Plugins#1430): an instance whose module set is pinned at boot must not import
/// sources built past the publication it runs, and the seal's own markers are the only statement
/// of which commit that is.
///
/// <para>Markers are written by <c>publish-bake-bundles.sh</c> strictly BEFORE the sentinel, so a
/// sealed directory always carries them. The repository marker is newer than the commit marker;
/// a seal without it is attributed by commit instead (see <c>SealedSyncGate</c>).</para>
///
/// <para>Pure over the file system and never throws: an unreadable or absent root reads as
/// "nothing sealed for this identity". That is a statement about THIS reading, not a verdict —
/// the gate that consumes it (<c>SealedSyncGate</c>) treats "no seal attributable to the
/// repository" as "this gate does not apply" and keeps today's behaviour; only a seal that IS
/// attributable and is at another commit, or torn, holds a source.</para>
/// </summary>
public static class SealedPublicationIndex
{
    /// <summary>The producing repository's commit, one line — <c>unknown</c> when the lane had none.</summary>
    public const string SourceCommitMarkerFileName = "source-commit.txt";

    /// <summary>The producing repository as <c>owner/name</c>, one line.</summary>
    public const string RepositoryMarkerFileName = "repository.txt";

    /// <summary>
    /// The directory under the published root holding one marker file per platform release,
    /// named by version, containing that release's framework identity (written by
    /// <c>publish-bake-bundles.sh</c> on every run). Leading underscore so it can never collide
    /// with a framework-identity directory (those are <c>s…</c>/<c>g…</c>). The ONE definition;
    /// <c>PublishedBundleCatalogue.ReleaseMarkerDirectoryName</c> forwards to it.
    /// </summary>
    public const string ReleaseMarkerDirectoryName = "_releases";

    /// <summary>
    /// The REVERSE of the release markers: framework identity → the newest platform version
    /// published under it — <b>newest by SEALED-PUBLICATION LINEAGE</b>
    /// (<see cref="Plugin.Packaging.PlatformReleaseOrder.Newest"/>), the same total order the
    /// self-updater ranks registry tags with. 🚨 Not SemVer: a marker's file NAME is the version
    /// LABEL that publication carried, and a label can be wrong or retired — <c>3.1.0-ci.7841</c>
    /// (the withdrawn 2026-09-05 slip) and <c>3.0.0-rc9.ci.7824</c> (the retired rc line, where
    /// SemVer §11.4 puts the text <c>rc9</c> above <c>ci</c>) both sort ABOVE the later, sealed
    /// <c>3.0.0-ci.8130</c>, so a SemVer reading would place an identity on a stale line and hand
    /// <see cref="ShippedPrebuiltBundles"/> a three-day-old publication as "newest" (#3542). The run
    /// number the marker's own name carries is the only key the machine produced. This is how a
    /// bundle sealed for another
    /// identity is placed on a platform LINE: its manifest names the identity, the marker names
    /// the version, and <see cref="PrebuiltAdoptionPolicy"/> compares lines. An identity no
    /// marker names is absent — the policy then declines it under <c>Family</c> strictness, as a
    /// line it cannot establish. Pure over the file system; never throws (an unreadable root reads
    /// as empty).
    /// </summary>
    public static IReadOnlyDictionary<string, string> ReleasesOf(string? publishedRoot, ILogger? logger = null)
    {
        var byIdentity = new Dictionary<string, string>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(publishedRoot))
            return byIdentity;
        var markers = Path.Combine(publishedRoot, ReleaseMarkerDirectoryName);
        try
        {
            if (!Directory.Exists(markers))
                return byIdentity;
            foreach (var file in Directory.EnumerateFiles(markers))
            {
                var version = Path.GetFileName(file);
                var identity = ReadMarker(file);
                if (string.IsNullOrEmpty(identity) || string.IsNullOrEmpty(version))
                    continue;
                if (!byIdentity.TryGetValue(identity, out var known)
                    || Plugin.Packaging.PlatformReleaseOrder.Newest.Compare(version, known) > 0)
                    byIdentity[identity] = version;
            }
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex,
                "SealedPublicationIndex: could not read the release markers under {Directory} — "
                + "no identity can be placed on a platform line", markers);
        }
        return byIdentity;
    }

    /// <summary>
    /// The newest publication LINE strictly above <paramref name="identity"/>'s, or null when
    /// there is none — the half a hold cannot otherwise state.
    ///
    /// <para>🚨 <b>Why a hold needs this.</b> <c>SealedSyncGate</c> holds a source with "built at
    /// S, not sealed for this instance (identity I: 'plugins' is sealed at C)". That sentence is
    /// true and it is consistent with two OPPOSITE situations, which is why it has twice been read
    /// as the wrong one: either <b>nothing has sealed recently</b> (the publishing lane is broken —
    /// act on the lane), or <b>seals are advancing under a NEWER identity</b> that this instance
    /// does not run (the instance's IMAGE is behind — act with a roll). Measured 2026-09-16 on
    /// memex.meshweaver.cloud: held at <c>627fb3cd</c> for identity <c>sd608997…</c> while the live
    /// publication was sealed under <c>s799247a…</c>; two issues were open reading that same note as
    /// a stalled publication path while the lane was green and the inbox empty
    /// (MeshWeaver.Plugins#1823, #1798). This is the fact that separates them, and the instance can
    /// read it without asking anything: the release markers under the published root name every
    /// line, not only its own.</para>
    ///
    /// <para>Null — say nothing rather than guess — when the root carries no markers, when THIS
    /// identity is on no line the markers place (a hold cannot be compared against an unknown), and
    /// when this identity is already the newest. Ordering is
    /// <see cref="Plugin.Packaging.PlatformReleaseOrder.Newest"/>, the sealed-publication lineage
    /// <see cref="ReleasesOf"/> documents — never SemVer.</para>
    /// </summary>
    /// <param name="publishedRoot">The published bundle root.</param>
    /// <param name="identity">This instance's framework identity.</param>
    /// <param name="logger">Diagnostics.</param>
    /// <returns>The newest line above this identity's, or null.</returns>
    public static PublicationLine? NewerLineThan(
        string? publishedRoot, string? identity, ILogger? logger = null)
    {
        if (string.IsNullOrWhiteSpace(identity))
            return null;
        var releases = ReleasesOf(publishedRoot, logger);
        if (!releases.TryGetValue(identity, out var mine))
            return null;

        PublicationLine? newest = null;
        foreach (var (otherIdentity, version) in releases)
        {
            if (string.Equals(otherIdentity, identity, StringComparison.Ordinal))
                continue;
            if (Plugin.Packaging.PlatformReleaseOrder.Newest.Compare(version, mine) <= 0)
                continue;
            if (newest is null
                || Plugin.Packaging.PlatformReleaseOrder.Newest.Compare(version, newest.Version) > 0)
                newest = new PublicationLine(otherIdentity, version);
        }
        return newest;
    }

    /// <summary>
    /// Every source directory under <c>&lt;publishedRoot&gt;/&lt;identity&gt;/</c>, read as
    /// <see cref="SealedSource"/>s. Empty when the root or the identity directory is absent.
    /// </summary>
    public static IReadOnlyList<SealedSource> ReadFor(
        string? publishedRoot, string? identity, ILogger? logger = null)
        => ReadResolvedFor(publishedRoot, identity, logger).Select(r => r.Source).ToList();

    /// <summary>
    /// 🚨 <see cref="ReadFor"/> plus the publication directory each reading was taken from — so a
    /// caller that ALSO reads bytes can read them from the same publication instead of resolving
    /// the pointer a second time (#3461).
    ///
    /// <para>Two independent resolutions of one pointer are two publications whenever it moves
    /// between them, and <c>ShippedPrebuiltBundles.SeedPublishedRoot</c> is exactly that caller:
    /// it takes this index for the markers the sync reconciler decides on and then enumerates the
    /// bundles to adopt. Straddling a pointer move there hands the reconciler one generation's
    /// commit while the bytes come from another — the same class of disagreement this page's
    /// routing exists to remove, one level up. The snapshot is the fix, and it is the shell
    /// analogue of <c>carry-forward-bundles.sh</c>'s one-publication postcondition.</para>
    /// </summary>
    /// <param name="publishedRoot">The published bundle root.</param>
    /// <param name="identity">The framework identity whose directory to read.</param>
    /// <param name="logger">Diagnostics.</param>
    /// <returns>Each source's reading and the directory it came from.</returns>
    internal static IReadOnlyList<(SealedSource Source, string Directory)> ReadResolvedFor(
        string? publishedRoot, string? identity, ILogger? logger = null)
        => ResolvedReadingFor(publishedRoot, identity, logger).Sources;

    /// <summary>
    /// <see cref="ReadResolvedFor"/> plus WHY the list is the length it is — so a caller can tell
    /// "nothing is sealed for me" from "I could not look". See <see cref="SealedReadOutcome"/>
    /// for why conflating those two silently disables the gate that consumes this (#3461).
    /// </summary>
    internal static (IReadOnlyList<(SealedSource Source, string Directory)> Sources,
                     SealedReadOutcome Outcome) ResolvedReadingFor(
        string? publishedRoot, string? identity, ILogger? logger = null)
    {
        if (string.IsNullOrWhiteSpace(publishedRoot) || string.IsNullOrWhiteSpace(identity))
            return ([], SealedReadOutcome.NotConfigured);
        var identityDirectory = Path.Combine(publishedRoot, identity);
        try
        {
            if (!Directory.Exists(identityDirectory))
                // 🚨 ABSENT and OCCUPIED-BY-SOMETHING-ELSE are different answers, and
                // `Directory.Exists` returns false for both. Nothing at the path is the ordinary
                // state of a framework identity that has not been published for yet — read it
                // cleanly as empty, or every new platform line would hold every source. A FILE (or
                // a broken link) at exactly that path is the opposite: something is there and it
                // is not what this reader can enumerate, which is what a half-finished layout
                // migration looks like from here (#3461 phase 5). Calling that "nothing sealed"
                // is the very conflation this outcome exists to end.
                return ([], File.Exists(identityDirectory)
                    ? SealedReadOutcome.Unreadable
                    : SealedReadOutcome.Read);
            var readings = Directory.EnumerateDirectories(identityDirectory)
                .OrderBy(d => d, StringComparer.Ordinal)
                .Select(d => ReadSource(d, logger))
                .ToList();
            // 🚨 A SOURCE WHOSE POINTER COULD NOT BE FOLLOWED MAKES THE WHOLE READING UNREADABLE
            // (#3461 phase 5). Until the flat compatibility copy was disposed of, that fall-back
            // landed on a sealed publication and the reading was merely a little stale; now it
            // lands on a source directory holding nothing, so the source reads as unsealed AND
            // unattributable — and an unattributable source is exactly what `SealedSyncGate` reads
            // as "this instance runs no publication of that repository", i.e. as a licence to
            // advance. The question is not per repository: a source nobody could attribute may be
            // ANY repository's, so no per-repository verdict taken from this list is trustworthy.
            var faulted = readings.Where(r => r.Unreadable).ToList();
            if (faulted.Count == 0)
                return (readings.Select(r => (r.Source, r.Directory)).ToList(), SealedReadOutcome.Read);
            logger?.LogWarning(
                "SealedPublicationIndex: {Faulted} of {Total} source(s) under {Directory} carry a "
                + "publication pointer this reader could not follow, with no sealed publication "
                + "behind it ({Sources}) — this reading is UNREADABLE, not empty; a caller that "
                + "gates on it must HOLD rather than proceed (#3461)",
                faulted.Count, readings.Count, identityDirectory,
                string.Join(", ", faulted.Select(f => $"{f.Source.Source}: {f.Source.Refusal}")));
            return (readings.Select(r => (r.Source, r.Directory)).ToList(), SealedReadOutcome.Unreadable);
        }
        catch (Exception ex)
        {
            // 🚨 The list is EMPTY and that is not a verdict. It used to be indistinguishable from
            // "nothing sealed", and the gate read that as a licence to advance every repository.
            logger?.LogWarning(ex,
                "SealedPublicationIndex: could not read the publications under {Directory} — "
                + "this reading is UNREADABLE, not empty; a caller that gates on it must HOLD "
                + "rather than proceed (#3461)", identityDirectory);
            return ([], SealedReadOutcome.Unreadable);
        }
    }

    /// <summary>
    /// What is sealed for this identity, and whether the reading is a statement or a failure.
    /// The pair <see cref="ReadFor"/> should have returned from the start.
    /// </summary>
    public static (IReadOnlyList<SealedSource> Sources, SealedReadOutcome Outcome) ReadingFor(
        string? publishedRoot, string? identity, ILogger? logger = null)
    {
        var (sources, outcome) = ResolvedReadingFor(publishedRoot, identity, logger);
        return ([.. sources.Select(r => r.Source)], outcome);
    }

    private static (SealedSource Source, string Directory, bool Unreadable) ReadSource(
        string sourceDirectory, ILogger? logger)
    {
        // 🚨 The SOURCE name is the directory's own, taken BEFORE resolution — a generation is
        // an instance of a publication of 'plugins', never a source called
        // 'Systemorph-MeshWeaver-42-1'. SealedSyncGate attributes seals by this name.
        var source = Path.GetFileName(sourceDirectory)!;
        // 🚨 …and every path below is composed under the RESOLVED directory (#3461, phase 3).
        // This reader is in the portal image and phase 1 missed it: SeedPublishedRoot already
        // reads the BUNDLES through PublicationDirectoryOf, so without this the index and the
        // seeder would read two different publications of one source.
        var pointer = ShippedPrebuiltBundles.ResolvePublicationPointer(sourceDirectory, logger);
        var publication = pointer.Directory;
        var repository = ReadMarker(Path.Combine(publication, RepositoryMarkerFileName));
        var commit = ReadMarker(Path.Combine(publication, SourceCommitMarkerFileName));
        if (string.Equals(commit, "unknown", StringComparison.OrdinalIgnoreCase))
            commit = null;
        var sentinel = Path.Combine(publication, ShippedPrebuiltBundles.CompletionSentinelFileName);
        if (!File.Exists(sentinel))
        {
            // 🚨 THE TWO ABSENCES, and phase 5 is what separated them. A pointer that is simply
            // NOT THERE means the source directory IS the publication (the flat layout, and a
            // prefix nothing has published for yet) — "no completion sentinel" is then a real
            // statement about a real place. A pointer that EXISTS and could not be followed —
            // read mid-replacement, blank, refused, or naming a generation that is not on disk —
            // is the opposite: some producer of this prefix publishes generations, and WHICH one
            // applies could not be read. Since the flat compatibility copy is disposed of once a
            // generation is live (phase 5), the fall-back finds nothing sealed and nothing to
            // attribute the source with, which `SealedSyncGate` would read as "not this gate's
            // business" and answer with Go. So this reading is UNREADABLE, and the caller HOLDS.
            var unreadable = pointer.Fault is not null;
            return (new SealedSource(source, repository, commit, false, unreadable
                ? $"the publication pointer could not be followed ({pointer.Fault}) and no sealed "
                  + "publication sits behind it — which publication applies could not be read"
                : "no completion sentinel"), publication, unreadable);
        }
        try
        {
            var missing = File.ReadAllLines(sentinel)
                .Select(l => l.Trim())
                .Where(l => l.Length > 0)
                .FirstOrDefault(name => !File.Exists(Path.Combine(publication, name)));
            return (missing is null
                ? new SealedSource(source, repository, commit, true, null)
                : new SealedSource(source, repository, commit, false,
                    $"the seal lists '{missing}', which is not on disk"), publication, false);
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "SealedPublicationIndex: could not read the seal of {Directory}", publication);
            return (new SealedSource(source, repository, commit, false,
                $"the seal could not be read: {ex.GetType().Name}"), publication, true);
        }
    }

    private static string? ReadMarker(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            var value = File.ReadAllText(path).Trim();
            return value.Length == 0 ? null : value;
        }
        catch
        {
            return null;
        }
    }
}
