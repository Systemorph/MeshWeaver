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
    /// What a first import of a repository may land on, per
    /// <see cref="DecideFirstImport"/>. Exactly three shapes, and they are distinguishable:
    /// <list type="bullet">
    ///   <item><description><see cref="Commit"/> set — land on that commit (the sealed one).</description></item>
    ///   <item><description>Both null — nothing attributable; resolve the branch, as today.</description></item>
    ///   <item><description><see cref="HoldReason"/> set — import nothing yet, and say why.</description></item>
    /// </list>
    /// </summary>
    /// <param name="Commit">The sealed commit to import at, or null.</param>
    /// <param name="HoldReason">Why nothing may be imported yet, or null.</param>
    /// <param name="Reason">Log copy for whichever of the three this is — always populated.</param>
    public sealed record FirstImportPlan(string? Commit, string? HoldReason, string Reason)
    {
        /// <summary>True when an import may run at all.</summary>
        public bool Proceed => HoldReason is null;

        /// <summary>True when the import may run and must resolve the branch (today's behaviour).</summary>
        public bool AtBranchTip => HoldReason is null && Commit is null;
    }

    private static FirstImportPlan Hold(string reason) => new(null, reason, reason);

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

    /// <summary>
    /// What a FIRST import of <paramref name="repo"/> must land on — the adopt-then-sync half of
    /// <see cref="Decide"/>, for the one unattended importer that has no built commit to be gated
    /// against (<c>ModuleDiscoveryService.FirstImport</c>, MeshWeaver#3845 hole 2).
    ///
    /// <para><b>Why this is a separate decision and not <see cref="Decide"/>.</b> The gate answers
    /// "may this source advance to the commit a build just proved?" — it needs that commit. A first
    /// import has neither: the Space was created seconds ago, its config carries no
    /// <c>LastSyncCommitSha</c>, and the trigger is a catalog scan, not a green build. So the two
    /// commit-attribution legs of <see cref="BelongsTo"/> have nothing to compare against and only
    /// the repository MARKER can attribute a seal here. A seal that predates the marker is therefore
    /// not attributable at a first import, and reads as "not this decision's business" — the
    /// conservative direction: it keeps today's behaviour rather than holding on a guess.</para>
    ///
    /// <para><b>What it fixes.</b> <c>FirstImport</c> imports at the BRANCH TIP, unattended, as
    /// System, on boot and on every catalog scan — the exact shape
    /// <see cref="GitHubActivityExtensions.UpdateToProvenCommitFromGitHub"/> exists to remove
    /// ("an unattended import must not [resolve the ref at fetch time], because nothing on the
    /// instance authorised the tree it would receive"). <c>SyncRefContract</c> left it as a named
    /// residue on the grounds that a webhook-less consumer has no <c>BuildCompletion</c> to pin to —
    /// true, and beside the point: such a consumer DOES have the seal on its own disk, and the
    /// sealed commit is a commit a build proved AND whose bytes this identity actually runs. That is
    /// strictly better evidence than a branch tip, and it needs no webhook.</para>
    ///
    /// <para><b>The blast radius is unchanged where the rationale applied.</b> A repository this
    /// instance runs no publication of is not attributable, so it still provisions at the tip. Only
    /// a repository whose publication this instance actually runs is pinned or held — and a held
    /// first import leaves a Space with a sync entry and no content, never live content going dark.</para>
    ///
    /// <para>🚨 <b>How a hold releases, stated exactly, because the loose version of this is wrong.</b>
    /// Three paths re-run the import: the discovery scan's next pass (a config with no
    /// <c>LastSyncCommitSha</c> is its re-import trigger); <c>SealedPublicationSyncReconciler</c>
    /// from <c>ShippedPrebuiltBundles.SeedPublishedRoot</c>, which is boot-time; and — since
    /// <c>Systemorph/MeshWeaver#4209</c> — that same reconciler from
    /// <c>PublicationSealArrivalService</c>, when the publishing lane's
    /// <c>Hosting/PlatformBuilds/&lt;source&gt;</c> announcement arrives. The third one releases a
    /// held FIRST import too, and that is not obvious: <c>SealedPublicationSyncReconciler.Decide</c>
    /// treats a config with NO <c>LastSyncCommitSha</c> as behind the sealed commit, so the gate
    /// proceeds and <c>ImportAtSealedCommit</c> fires.</para>
    ///
    /// <para>🚨 <b>The residual is the WEBHOOK-LESS instance, and it is inherent rather than
    /// missing.</b> There, no announcement arrives and no <c>BuildCompletion</c> is emitted, so the
    /// first and third paths are dead and <b>all of them reduce to the next process start</b> — a
    /// seal completing mid-process is not noticed until then. That is
    /// <c>Systemorph/MeshWeaver#4063</c>'s shape and this hold inherits it. Saying "self-releasing"
    /// without that qualification claims a watcher that, on such an instance, has no event to hear;
    /// saying "only a restart" without #4209 understates it everywhere else.</para>
    /// </summary>
    /// <param name="repo">The repository the module's Space syncs from.</param>
    /// <param name="sealedForThisIdentity">What the registry sealed under this instance's identity.</param>
    /// <param name="identity">This instance's framework identity — log copy only.</param>
    public static FirstImportPlan DecideFirstImport(
        RepoIdentity repo, IReadOnlyList<SealedSource> sealedForThisIdentity, string identity)
    {
        ArgumentNullException.ThrowIfNull(repo);
        ArgumentNullException.ThrowIfNull(sealedForThisIdentity);

        var mine = sealedForThisIdentity
            .Where(s => s.Repository is { Length: > 0 } recorded && repo.Matches(Parse(recorded)))
            .ToList();
        if (mine.Count == 0)
            return new FirstImportPlan(null, null,
                $"no publication of {repo} is sealed for identity {identity} — this instance runs none, "
                + "so an unattended import keeps today's behaviour and resolves the branch");

        // 🚨 EVERY attributable publication must be usable, not merely ONE of them. Selecting the
        // sealed ones first and deciding on those would let a good seal OVERRIDE a torn sibling of
        // the same repository — `plugins` sealed at C beside a `plugins-extra` with no completion
        // sentinel would pin the Space at C while part of that repository's bytes are missing here.
        // That is the fail-open this decision exists to remove, one level in, and it contradicts
        // this method's own contract ("attributable but torn … import nothing"). So the refusal is
        // evaluated over `mine`, before any selection narrows it.
        //
        // This is deliberately STRICTER than `Decide`, which proceeds when ANY sealed source sits at
        // the built commit. The two answer different questions: `Decide` admits a tree a build
        // proved onto a source already carrying content, while this decides what a Space that holds
        // NOTHING YET is first populated with. There is no partial state to preserve here and no
        // second chance to be more careful later, so "cannot tell" is never "clear to proceed".
        var unusable = mine
            .Where(s => !s.IsSealed || s.SourceCommit is not { Length: > 0 })
            .OrderByDescending(s => s.IsSealed)
            .ThenBy(s => s.Source, StringComparer.Ordinal)
            .ToList();
        if (unusable.Count > 0)
        {
            var witness = unusable[0];
            var others = unusable.Count > 1 ? $" (and {unusable.Count - 1} more of {repo})" : "";
            return Hold(witness.IsSealed
                ? $"this instance's publication of '{witness.Source}' ({repo}) is sealed at an unknown "
                  + $"commit (identity {identity}){others} — an unattended import has no commit to land on"
                : $"this instance's publication of '{witness.Source}' ({repo}) is not sealed "
                  + $"(identity {identity}: {witness.Refusal}){others} — an unattended import waits for it");
        }

        // 🚨 Several sealed publications of ONE repository that disagree about the commit is a state
        // no reading here can resolve — picking one would put the Space on a tree half this
        // instance's own bytes were not baked from. Hold and name them, exactly as a torn seal does.
        var commit = mine[0].SourceCommit!;
        if (mine.Any(s => !SameCommit(s.SourceCommit, commit)))
            return Hold(
                $"this instance's publications of {repo} disagree about the commit (identity {identity}: "
                + string.Join(", ", mine
                    .OrderBy(s => s.Source, StringComparer.Ordinal)
                    .Select(s => $"'{s.Source}' at {Short(s.SourceCommit!)}"))
                + ") — an unattended import cannot choose between them");

        return new FirstImportPlan(commit, null,
            $"'{mine[0].Source}' ({repo}) is sealed at {Short(commit)} for identity {identity} — "
            + "an unattended import lands on the commit whose bytes this instance runs");
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
