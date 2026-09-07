using MeshWeaver.Hosting;

namespace MeshWeaver.GitSync;

/// <summary>
/// 🚨 A module-bearing repository's sources advance only to the commit SEALED for this instance
/// (MeshWeaver.Plugins#1430). An instance pins its module set at boot from the publication sealed
/// under its own framework identity; its sources, by contrast, used to follow every green build of
/// the repository — so #1413's Payments split reached both production Stores' <c>Store/*</c> sources
/// while they ran a platform with no Payments module, and four Store types sat in compile Error for
/// nine hours. The green build proves a tree compiles somewhere; the seal proves it compiles HERE.
///
/// <para>Pure: the caller supplies what the registry sealed for this identity
/// (<see cref="SealedPublicationIndex.ReadFor"/>) and this decides, per sync source. Three
/// outcomes, stated as data:</para>
/// <list type="bullet">
///   <item><description>No sealed source is attributable to the repository — the instance runs no
///   publication of it (a course repo, a repo whose seal predates both markers and is on neither
///   known commit): today's behaviour, import at the built commit.</description></item>
///   <item><description>A sealed source of the repository is at the built commit: proceed — the
///   seal and the build agree on the tree.</description></item>
///   <item><description>Otherwise: HOLD, and say why (built at S, sealed at C / not sealed). The
///   source waits for the seal; "cannot tell" is never "clear to proceed".</description></item>
/// </list>
///
/// <para><b>Attribution.</b> A sealed source belongs to the repository when its repository marker
/// names it, or — for seals that predate that marker — when its source commit is the built commit
/// or the commit the sync source already sits on. Once the gate has run, a source only ever sits on
/// sealed commits, so the second leg keeps attributing older seals until they are republished.</para>
/// </summary>
public static class SealedSyncGate
{
    /// <summary>The decision: proceed, or hold with the reason a skipped Space is logged with.</summary>
    public sealed record Verdict(bool Proceed, string? HoldReason)
    {
        /// <summary>Import at the built commit.</summary>
        public static Verdict Go { get; } = new(true, null);
    }

    /// <summary>
    /// Decides whether a sync source of <paramref name="repo"/> may import the green build at
    /// <paramref name="headSha"/>, given what this instance's identity has sealed.
    /// </summary>
    /// <param name="repo">The repository the green build is of.</param>
    /// <param name="headSha">The built commit.</param>
    /// <param name="lastSyncSha">The commit the sync source currently sits on, or null.</param>
    /// <param name="sealedForThisIdentity">What the registry sealed under this instance's framework identity.</param>
    /// <param name="identity">This instance's framework identity — log copy only.</param>
    public static Verdict Decide(
        RepoIdentity repo, string headSha, string? lastSyncSha,
        IReadOnlyList<SealedSource> sealedForThisIdentity, string identity)
    {
        var mine = sealedForThisIdentity
            .Where(s => BelongsTo(s, repo, headSha, lastSyncSha))
            .ToList();
        if (mine.Count == 0)
            return Verdict.Go;
        if (mine.Any(s => s.IsSealed && SameCommit(s.SourceCommit, headSha)))
            return Verdict.Go;
        var witness = mine.OrderByDescending(s => s.IsSealed).ThenBy(s => s.Source, StringComparer.Ordinal).First();
        var reason = witness.IsSealed
            ? $"built at {Short(headSha)}, not sealed for this instance (identity {identity}: "
              + $"'{witness.Source}' is sealed at {(witness.SourceCommit is null ? "an unknown commit" : Short(witness.SourceCommit))})"
            : $"built at {Short(headSha)}, and this instance's publication of '{witness.Source}' is not sealed "
              + $"(identity {identity}: {witness.Refusal})";
        return new Verdict(false, reason);
    }

    /// <summary>Whether a sealed source is the repository's — by its repository marker, or by commit
    /// for a seal that predates the marker. Pure.</summary>
    public static bool BelongsTo(SealedSource sealedSource, RepoIdentity repo, string headSha, string? lastSyncSha)
    {
        if (sealedSource.Repository is { Length: > 0 } recorded)
            return repo.Matches(Parse(recorded));
        return SameCommit(sealedSource.SourceCommit, headSha)
            || (lastSyncSha is { Length: > 0 } && SameCommit(sealedSource.SourceCommit, lastSyncSha));
    }

    /// <summary><c>owner/name</c> → <see cref="RepoIdentity"/>; anything else is an identity that matches nothing.</summary>
    public static RepoIdentity Parse(string ownerSlashName)
    {
        var parts = ownerSlashName.Trim().Split('/', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length == 2 ? new RepoIdentity(parts[0], parts[1]) : new RepoIdentity("", "");
    }

    private static bool SameCommit(string? a, string? b) =>
        !string.IsNullOrEmpty(a) && !string.IsNullOrEmpty(b)
        && (a.StartsWith(b, StringComparison.OrdinalIgnoreCase) || b.StartsWith(a, StringComparison.OrdinalIgnoreCase))
        && Math.Min(a.Length, b.Length) >= 7;

    private static string Short(string sha) => sha.Length > 8 ? sha[..8] : sha;
}
