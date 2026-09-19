using System.Collections.Immutable;

namespace MeshWeaver.Graph;

/// <summary>
/// Whether a partition's content TRACKS an external source on this mesh — at least one CONFIGURED
/// sync source (a <c>{partition}/_GitSync</c> naming a repository, or another provider's
/// equivalent) brings the partition's files here and keeps them current.
///
/// <para><b>Who asks, and why it is a separate seam.</b> The compile control plane
/// (<c>NodeTypeCompilationHelpers</c>' compile watcher) asks it before it compiles a MODULE's
/// content locally: a module is delivered as a prebuilt bundle, and when the bundle is refused
/// (its source fingerprint disagrees with the files this mesh holds) or has not landed for this
/// framework identity, "compile the live source instead" is only honest on a mesh whose copy of
/// that source TRACKS the module's repository. On a partition nothing syncs, the "live source" is
/// whatever an install left behind — last month's files, or four of five — and compiling it
/// produces old code that reads as current (MeshWeaver#3583). The administration GUI asks the
/// richer <c>IPartitionSyncSourceProvider</c> (the same providers, one class per kind); this
/// contract is the one-bit answer the compiler layer can depend on without a reference to the
/// sync layer above it.</para>
///
/// <para><b>Contract.</b> Emits <see langword="true"/> when at least one source of this kind is
/// configured for the partition (a config node with its repository set — a config node that
/// exists but names no repository is NOT tracking anything), <see langword="false"/> otherwise;
/// re-emits when a source is added, removed or reconfigured; never stalls — a partition with no
/// sources emits <see langword="false"/> promptly. A mesh with NO implementation registered has no
/// notion of tracking at all (a local mesh, CI's disposable meshes, the bake host), and the
/// compile gate treats that as "compiling is legal here", exactly as it always was.</para>
/// </summary>
public interface IPartitionSourceTracking
{
    /// <summary>Live answer for <paramref name="partition"/> (the top-level path segment):
    /// whether at least one configured source of this kind tracks it.</summary>
    IObservable<bool> IsTracked(string partition);

    /// <summary>
    /// Whether at least one configured source of this kind WRITES this partition's content — i.e.
    /// imports repo → mesh. The narrower half of <see cref="IsTracked"/>, and the one an
    /// installer's "am I the only writer here?" question needs (MeshWeaver#4588).
    ///
    /// <para>🚨 <b>The two questions differ for an EXPORT-ONLY source, and each consumer needs its
    /// own.</b> A `mesh → repo` source makes the repository a mirror of the mesh and rejects
    /// imports, so nothing can overwrite or prune what an installer wrote: the installer IS the
    /// only writer, and holding an install there would be a false positive. The compile control
    /// plane's question is the opposite way round — with the mesh as the source of truth, its live
    /// source IS current, so that plane must keep reading <see cref="IsTracked"/> and must not be
    /// narrowed by this. One shared bit answering both is how they would come to disagree.</para>
    ///
    /// <para>The default is <see cref="IsTracked"/> — the conservative answer and the pre-#4588
    /// behaviour — so a provider that cannot tell the directions apart keeps treating every tracked
    /// partition as written. Same contract otherwise: live, re-emitting, never stalling.</para>
    /// </summary>
    /// <param name="partition">The partition (the top-level path segment).</param>
    /// <returns>A live observable; <see langword="true"/> while a source of this kind imports into
    /// the partition.</returns>
    IObservable<bool> ImportsContent(string partition) => IsTracked(partition);

    /// <summary>
    /// WHICH repositories import into this partition — the identity half of
    /// <see cref="ImportsContent"/>, for the one question a bit cannot answer: *is the tree this
    /// installer is about to write the tree the partition's own writer is held to?*
    ///
    /// <para>🚨 <b>Why a PROVEN ref still needs this (MeshWeaver#4625).</b> The boot install is
    /// allowed to write a partition another writer keeps current when its ref was PROVEN — the seal
    /// named a commit, so #4259's lane pins both writers. That rests on an assumption nothing
    /// verified: that the seal the installer landed and the commit the partition's writer is held
    /// to are trees of the SAME repository. Where they are not — a package whose
    /// <c>targetPartition</c> is connected to a different repository, or to a different
    /// subdirectory of one — both writers are pinned, both are "proven", and they still land two
    /// different trees into one partition. That is the mix
    /// <c>Doc/Architecture/OnePartitionOneBookkeeping</c> describes, reached by a route its gates
    /// did not close.</para>
    ///
    /// <para>🚨 <b>The DEFAULT is "I cannot say", and a caller must treat that as permission, not
    /// as a mismatch.</b> Holding on an unknown identity would hold EVERY proven install on every
    /// provider that has not implemented this — the fleet's normal shape included — which is the
    /// stated cost that kept #4625 open. So the asymmetry here is the OPPOSITE of gate 1c's, and
    /// deliberately: an unproven ref is cheap to hold (it is re-derived next boot), while an
    /// unknown repository identity is the default answer and holding on it is an outage. Hold only
    /// on a DEFINITE disagreement — both sides known, and different.</para>
    ///
    /// <para>The shape is <see cref="TrackedRepositories"/>, not a bare collection, because
    /// "nothing tracks this partition" and "this provider cannot tell you what tracks it" are
    /// different facts and an empty list would fold them — the same conflation that made a
    /// publication announcement report a clean partition it had never read (MeshWeaver#4620).</para>
    /// </summary>
    /// <param name="partition">The partition (the top-level path segment).</param>
    /// <returns>A live observable of the identities importing into the partition; the default
    /// answers <see cref="TrackedRepositories.Unknown"/>, which obliges no implementer.</returns>
    IObservable<TrackedRepositories> ImportingRepositories(string partition) =>
        System.Reactive.Linq.Observable.Return(TrackedRepositories.Unknown);
}

/// <summary>
/// What a provider can say about WHICH repositories import into a partition.
///
/// <para>🚨 <see cref="Known"/> exists so that "nothing imports here" and "I cannot tell you what
/// imports here" are never the same value. A caller that folds them either holds installs it should
/// not (treating unknown as a mismatch) or waves through the very case the check exists for
/// (treating unknown as agreement).</para>
/// </summary>
/// <param name="Known">Whether this provider could answer at all.</param>
/// <param name="Identities">The importing repositories, each normalised by
/// <see cref="Normalize"/> — meaningful only when <see cref="Known"/>.</param>
public sealed record TrackedRepositories(bool Known, ImmutableHashSet<string> Identities)
{
    /// <summary>The default: this provider does not report identities. Obliges no implementer and
    /// licences no hold.</summary>
    public static TrackedRepositories Unknown { get; } =
        // 🚨 ImmutableHashSet, not `[]` (review on #4649): an array behind an
        // IReadOnlyCollection can be recovered and MUTATED by a caller, and this instance is a
        // process-wide static every provider reading shares. Immutable is also the house rule.
        new(false, ImmutableHashSet<string>.Empty);

    /// <summary>A definite answer — possibly empty, which then means "nothing imports here".</summary>
    /// <param name="identities">The importing repositories.</param>
    /// <returns>A known reading.</returns>
    public static TrackedRepositories Of(IEnumerable<string> identities) =>
        new(true, identities
            .Select(i => Normalize(i))
            .Where(i => i.Length > 0)
            .ToImmutableHashSet(StringComparer.OrdinalIgnoreCase));

    /// <summary>
    /// One comparable identity from a repository URL and an optional subdirectory —
    /// <c>owner/repo</c>, lowercased, with <c>#subdir</c> appended when one is configured.
    ///
    /// <para>The subdirectory is PART of the identity: a package sealed from
    /// <c>Systemorph/MeshWeaver.Plugins</c> and a partition synced from
    /// <c>Systemorph/MeshWeaver.Plugins/Hosting</c> are two different trees, and #4625 names
    /// exactly that as one of its two shapes. Case-insensitive because GitHub treats owner and
    /// repository names case-insensitively.</para>
    /// </summary>
    /// <param name="repositoryUrlOrSlug">A repository URL or an <c>owner/repo</c> slug.</param>
    /// <param name="subdirectory">The configured subdirectory, if any.</param>
    /// <returns>The normalised identity, or an empty string when nothing could be parsed.</returns>
    public static string Normalize(string? repositoryUrlOrSlug, string? subdirectory = null)
    {
        if (string.IsNullOrWhiteSpace(repositoryUrlOrSlug))
            return string.Empty;
        var text = repositoryUrlOrSlug.Trim();
        // Tolerate a URL, an scp-style remote, a trailing .git and a trailing slash — the identity
        // is owner/repo whichever spelling the config carries.
        var cut = text.IndexOf("://", StringComparison.Ordinal);
        if (cut >= 0)
            text = text[(cut + 3)..];
        if (text.StartsWith("git@", StringComparison.OrdinalIgnoreCase))
            text = text[4..].Replace(':', '/');
        text = text.TrimEnd('/');
        if (text.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
            text = text[..^4];
        var segments = text.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length < 2)
            return string.Empty;
        var slug = $"{segments[^2]}/{segments[^1]}".ToLowerInvariant();
        var sub = subdirectory?.Trim().Trim('/');
        return string.IsNullOrEmpty(sub) ? slug : $"{slug}#{sub.ToLowerInvariant()}";
    }

    /// <summary>
    /// Whether this reading DEFINITELY disagrees with <paramref name="candidate"/> — the only
    /// condition that may hold an install.
    ///
    /// <para>False when this provider could not answer, when it reports nothing importing, when the
    /// candidate identity is unparseable, and of course when the candidate is among them. Every one
    /// of those is "no evidence of a mismatch", and none of them is evidence of agreement either —
    /// which is exactly why this returns a HOLD decision and not a verdict about correctness.</para>
    /// </summary>
    /// <param name="candidate">The normalised identity the installer would write from.</param>
    /// <returns>True only when both sides are known and none of them matches.</returns>
    public bool DefinitelyDisagreesWith(string? candidate)
        => Known
           && Identities.Count > 0
           && candidate is { Length: > 0 }
           && !Identities.Contains(candidate);
}
