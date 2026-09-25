using System.Collections.Immutable;
using MeshWeaver.Data;
using MeshWeaver.Hosting;

namespace MeshWeaver.GitSync;

/// <summary>
/// 🚨 <b>The seal NEVER chooses a source's commit</b> (policy <c>module-sync-per-manifest-hash</c>,
/// coordinated with <c>platform-backwards-compatibility</c>). What was sealed for this instance's
/// framework identity decides only whether a NodeType ADOPTS prebuilt bytes or COMPILES from the
/// synced source — never whether, or at which commit, the sources arrive.
///
/// <para><b>Why it no longer holds.</b> This gate used to advance a module-bearing repository's
/// sources only to the commit sealed under the running identity (MeshWeaver.Plugins#1430) and hold
/// everything else — a whole-REPOSITORY verdict applied to every Space of that repository. Once the
/// seals advanced only for NEWER platforms (the ladder refuses those bytes here), the one seal left
/// for the running identity froze every Space of the repository at an old commit, with no bound:
/// the control instance's <c>Hosting/_GitSync</c> sat <c>Held</c> at a stale Plugins commit, so the
/// migrate-first Roll planner (a Hosting change) never reached the control plane that plans the
/// roll which the hold named as its only remedy — a bootstrap deadlock
/// (<c>Doc/Architecture/PublicationSealStarvation</c> §6.3). Under the compatibility ladder that
/// hold protects nothing: sources compile against the RUNNING platform, and bytes built for a newer
/// platform are declined at adoption, per type, loudly.</para>
///
/// <para><b>What decides instead.</b> Per MODULE, inside the import, keyed on the content hash in
/// the module's <c>manifest.lock</c> (<see cref="ModuleSyncDecision"/>): unchanged ⇒ nothing
/// written; changed ⇒ the module syncs to the incoming commit; a declared platform floor above the
/// running platform ⇒ that ONE module is declined, named, and its siblings sync. See
/// <c>Doc/Architecture/ModuleSyncPerManifestHash</c>.</para>
///
/// <para><b>What is kept, and why the surface stays.</b> The decisions below keep their public
/// shapes — they are called from every unattended lane and pinned by other repositories — and their
/// answers are now always a PROCEED: a green build imports its built commit, a person's import reads
/// exactly what was asked, and a first import resolves the configured branch. The seal reading is
/// still reported (which commit this identity's publication was baked from), because it is what
/// tells an operator whether a synced module will adopt bytes or compile.</para>
/// </summary>
public static class SealedSyncGate
{
    /// <summary>The decision: proceed, or hold with the reason a skipped Space is logged with.
    /// Under policy <c>module-sync-per-manifest-hash</c> the gate's own decisions always proceed;
    /// the shape stays for <see cref="RefusedForUnreadableIndex"/> and for callers in other
    /// repositories.</summary>
    public sealed record Verdict(bool Proceed, string? HoldReason)
    {
        /// <summary>Import at the built commit.</summary>
        public static Verdict Go { get; } = new(true, null);
    }

    /// <summary>
    /// What a first import of a repository may land on, per <see cref="DecideFirstImport"/>. Three
    /// shapes remain distinguishable for callers that read them:
    /// <list type="bullet">
    ///   <item><description><see cref="Commit"/> set — land on that commit.</description></item>
    ///   <item><description>Both null — resolve the configured branch.</description></item>
    ///   <item><description><see cref="HoldReason"/> set — import nothing yet, and say why.</description></item>
    /// </list>
    /// <see cref="DecideFirstImport"/> answers the second shape; the third is produced only by
    /// <see cref="RefusedFirstImportForUnreadableIndex"/>, which no platform lane consults any more.
    /// </summary>
    /// <param name="Commit">The commit to import at, or null.</param>
    /// <param name="HoldReason">Why nothing may be imported yet, or null.</param>
    /// <param name="Reason">Log copy for whichever of the three this is — always populated.</param>
    public sealed record FirstImportPlan(string? Commit, string? HoldReason, string Reason)
    {
        /// <summary>True when an import may run at all.</summary>
        public bool Proceed => HoldReason is null;

        /// <summary>True when the import may run and must resolve the branch.</summary>
        public bool AtBranchTip => HoldReason is null && Commit is null;
    }

    private static FirstImportPlan Hold(string reason) => new(null, reason, reason);

    /// <summary>
    /// What an import that was ASKED for a ref lands on (<see cref="DecideRequestedImport"/>) and
    /// what a green build lands on (<see cref="DecideBuild"/>). The ref that was asked for is what is
    /// used; <see cref="SealedCommit"/> reports whether this identity's publication was baked from
    /// exactly that commit.
    /// </summary>
    /// <param name="Commit">The commitish to import at. Null only on a hold.</param>
    /// <param name="SealedCommit">The commit to import at, when a publication sealed for this
    /// identity was baked from exactly it; null otherwise.</param>
    /// <param name="HoldReason">Why nothing may be imported, or null.</param>
    /// <param name="Reason">Log copy (English) for whichever answer this is — always populated.</param>
    /// <param name="Notice">Keyed activity lines rendered in the VIEWER's language (#3236); empty
    /// when the import runs exactly as asked — which is every answer under policy
    /// <c>module-sync-per-manifest-hash</c>.</param>
    public sealed record ImportPlan(
        string? Commit, string? SealedCommit, string? HoldReason, string Reason,
        ImmutableList<LogMessage> Notice)
    {
        /// <summary>True when an import may run at all.</summary>
        public bool Proceed => HoldReason is null;

        /// <summary>True when the gate chose a different commit than the one asked for. Never set
        /// under policy <c>module-sync-per-manifest-hash</c>: the seal does not choose a source's
        /// commit.</summary>
        public bool Redirected { get; init; }
    }

    /// <summary>
    /// What a PERSON's import lands on — <b>Update to latest</b> (the configured branch) or
    /// <b>Re-import at this commit</b> (a typed commitish): exactly what was asked, always.
    ///
    /// <para>The seal used to be asked first and could redirect the import onto the sealed commit,
    /// or hold it (MeshWeaver#3845 hole 3). Under policy <c>module-sync-per-manifest-hash</c> the
    /// sources arrive whatever was sealed; each module is then judged by its manifest hash, and each
    /// NodeType adopts a matching bundle or compiles from the synced source. An unreadable index
    /// therefore holds nothing here either — what cannot be read is whether BYTES can be adopted,
    /// and that is decided (and declined, loudly) per type at adoption.</para>
    /// </summary>
    /// <param name="repo">The repository the Space's sync source reads.</param>
    /// <param name="requested">What was asked for: the configured branch, or the typed commitish.</param>
    /// <param name="lastSyncSha">The commit the sync source currently sits on, or null.</param>
    /// <param name="readOutcome">Whether the seal index was READ (<see cref="SealedPublicationIndex.ReadingFor"/>).</param>
    /// <param name="sealedForThisIdentity">What the registry sealed under this instance's identity.</param>
    /// <param name="identity">This instance's framework identity.</param>
    /// <param name="newerLine">The newest publication line above this identity's, or null — log
    /// copy only.</param>
    /// <returns>The plan; never null, never a hold.</returns>
    public static ImportPlan DecideRequestedImport(
        RepoIdentity repo, string requested, string? lastSyncSha, SealedReadOutcome readOutcome,
        IReadOnlyList<SealedSource> sealedForThisIdentity, string identity, PublicationLine? newerLine)
    {
        ArgumentNullException.ThrowIfNull(repo);
        ArgumentException.ThrowIfNullOrWhiteSpace(requested);
        ArgumentNullException.ThrowIfNull(sealedForThisIdentity);

        // 🚨 A REQUESTED REF IS ONLY A COMMIT WHEN IT LOOKS LIKE ONE (review on #4576). A branch
        // whose NAME is hex is not a coordinate, so only a full sha may be compared with a seal.
        var requestedCommit = IsFullCommitSha(requested) ? requested : null;
        var sealedAt = requestedCommit is not null
            && sealedForThisIdentity.Any(s => s.IsSealed
                && BelongsTo(s, repo, requestedCommit, lastSyncSha)
                && SameCommit(s.SourceCommit, requestedCommit))
            ? requestedCommit
            : null;
        return new ImportPlan(requested, sealedAt, null,
            $"'{requested}' is imported as asked — {SealState(repo, readOutcome, sealedForThisIdentity, identity, newerLine)}",
            []);
    }

    /// <summary>
    /// What a GREEN BUILD at <paramref name="headSha"/> lands on: the built commit, always. The
    /// build is the proof the tree compiles; whether this identity's bundles were baked from it only
    /// decides adoption, per NodeType (policy <c>module-sync-per-manifest-hash</c>).
    /// </summary>
    /// <param name="repo">The repository the green build is of.</param>
    /// <param name="headSha">The built commit.</param>
    /// <param name="lastSyncSha">The commit the sync source currently sits on, or null.</param>
    /// <param name="sealedForThisIdentity">What the registry sealed under this instance's identity.</param>
    /// <param name="identity">This instance's framework identity — log copy only.</param>
    /// <param name="newerLine">The newest publication line above this identity's, or null — log copy only.</param>
    /// <returns>The plan; never null, never a hold, never a redirect.</returns>
    public static ImportPlan DecideBuild(
        RepoIdentity repo, string headSha, string? lastSyncSha,
        IReadOnlyList<SealedSource> sealedForThisIdentity, string identity, PublicationLine? newerLine)
    {
        ArgumentNullException.ThrowIfNull(repo);
        ArgumentException.ThrowIfNullOrWhiteSpace(headSha);
        ArgumentNullException.ThrowIfNull(sealedForThisIdentity);

        var sealedAtHead = sealedForThisIdentity.Any(s => s.IsSealed
            && BelongsTo(s, repo, headSha, lastSyncSha)
            && SameCommit(s.SourceCommit, headSha));
        return new ImportPlan(headSha, sealedAtHead ? headSha : null, null,
            $"imported at the built commit {Short(headSha)} — "
            + SealState(repo, SealedReadOutcome.Read, sealedForThisIdentity, identity, newerLine, headSha, lastSyncSha),
            []);
    }

    /// <summary>
    /// Whether the seal index could be READ — a fact to REPORT, never a reason to hold sources.
    ///
    /// <para>🚨 The #3461 contract ("cannot tell" is never "clear to proceed") still binds what
    /// actually depends on the index: ADOPTION of prebuilt bytes. An unreadable index adopts nothing
    /// it cannot verify — each type compiles from its synced source, or on a
    /// <c>Modules:RequirePrebuilt</c> mesh parks with its name — and every lane logs the unreadable
    /// reading at Warning. What it no longer does is freeze every source of every repository
    /// (policy <c>module-sync-per-manifest-hash</c>), which is why no platform lane holds on this
    /// answer any more. The method keeps its shape for callers in other repositories.</para>
    /// </summary>
    /// <param name="outcome">What <see cref="SealedPublicationIndex.ReadingFor"/> reported.</param>
    /// <param name="identity">This instance's framework identity — log copy only.</param>
    /// <returns>A <see cref="Verdict"/> naming the unreadable index, or null when it was read.</returns>
    public static Verdict? RefusedForUnreadableIndex(SealedReadOutcome outcome, string identity)
        => outcome is SealedReadOutcome.Unreadable
            ? new Verdict(false,
                $"the publication index for this instance's framework identity {identity} could "
                + "not be READ (see the SealedPublicationIndex warning above it) — that is an "
                + "absence of measurement, not an empty index, so no prebuilt bytes can be verified "
                + "for adoption. 'Cannot tell' is never 'clear to proceed' (#3461)")
            : null;

    /// <summary>
    /// <see cref="RefusedForUnreadableIndex"/> in the shape of a <see cref="FirstImportPlan"/>.
    /// Kept for callers in other repositories; no platform lane holds a first import on it (policy
    /// <c>module-sync-per-manifest-hash</c>).
    ///
    /// <para>🚨 A separate method rather than an overload of <c>DecideFirstImport</c> on purpose: an
    /// added overload makes every parameterless <c>&lt;see cref&gt;</c> to that name ambiguous
    /// (<c>CS0419</c> under <c>-warnaserror</c>).</para>
    /// </summary>
    /// <param name="outcome">What <see cref="SealedPublicationIndex.ReadingFor"/> reported.</param>
    /// <param name="identity">This instance's framework identity — log copy only.</param>
    /// <returns>A holding <see cref="FirstImportPlan"/>, or null when the reading is usable.</returns>
    public static FirstImportPlan? RefusedFirstImportForUnreadableIndex(
        SealedReadOutcome outcome, string identity)
        => RefusedForUnreadableIndex(outcome, identity) is { HoldReason: { } reason }
            ? Hold(reason)
            : null;

    /// <summary>
    /// Whether a sync source of <paramref name="repo"/> may import the green build at
    /// <paramref name="headSha"/>: always <see cref="Verdict.Go"/> (policy
    /// <c>module-sync-per-manifest-hash</c> — the seal never holds sources).
    /// </summary>
    /// <param name="repo">The repository the green build is of.</param>
    /// <param name="headSha">The built commit.</param>
    /// <param name="lastSyncSha">The commit the sync source currently sits on, or null.</param>
    /// <param name="sealedForThisIdentity">What the registry sealed under this instance's framework identity.</param>
    /// <param name="identity">This instance's framework identity — log copy only.</param>
    public static Verdict Decide(
        RepoIdentity repo, string headSha, string? lastSyncSha,
        IReadOnlyList<SealedSource> sealedForThisIdentity, string identity)
        => Decide(repo, headSha, lastSyncSha, sealedForThisIdentity, identity, null);

    /// <summary>
    /// <see cref="Decide(RepoIdentity, string, string?, IReadOnlyList{SealedSource}, string)"/> with
    /// the newest publication line above this identity's — always <see cref="Verdict.Go"/>.
    ///
    /// <para>A publication produced by a platform NEWER than the running one (the ladder's forbidden
    /// rung) is declined at ADOPTION, per NodeType, naming both versions; the sources still arrive
    /// and compile against the running platform. A module that DECLARES a floor above the running
    /// platform is declined by <see cref="ModuleSyncDecision"/>, alone, inside the import.</para>
    /// </summary>
    /// <param name="repo">The repository the green build is of.</param>
    /// <param name="headSha">The built commit.</param>
    /// <param name="lastSyncSha">The commit the sync source currently sits on, or null.</param>
    /// <param name="sealedForThisIdentity">What the registry sealed under this instance's framework identity.</param>
    /// <param name="identity">This instance's framework identity — log copy only.</param>
    /// <param name="newerLine">The newest publication line above this instance's, or null.</param>
    public static Verdict Decide(
        RepoIdentity repo, string headSha, string? lastSyncSha,
        IReadOnlyList<SealedSource> sealedForThisIdentity, string identity,
        PublicationLine? newerLine)
    {
        ArgumentNullException.ThrowIfNull(repo);
        ArgumentNullException.ThrowIfNull(sealedForThisIdentity);
        return Verdict.Go;
    }

    /// <summary>
    /// What a FIRST import of <paramref name="repo"/> lands on: the configured branch
    /// (<see cref="FirstImportPlan.AtBranchTip"/>), whatever was sealed.
    ///
    /// <para>A first import used to land on the commit sealed for this identity, or hold when that
    /// seal was torn, unknown, self-contradictory or produced by a newer platform (#4212). Under
    /// policy <c>module-sync-per-manifest-hash</c> the seal no longer chooses a source's commit: a
    /// first import resolves the branch, every later green build lands its built commit, and each
    /// module is judged by its manifest hash. A seal pinned to an old commit would otherwise leave a
    /// webhook-less instance on that tree indefinitely — the freeze this policy removes. The reason
    /// still states what was sealed, so an operator can tell adoption from compile.</para>
    ///
    /// <para>The boot default install (<c>InstanceAutoRegistrationService</c>) asks the same
    /// question, so both unattended writers of a partition agree; where a sync source keeps a
    /// partition current, the install's configured ref is not proven and it defers to that writer
    /// (MeshWeaver#4588) — one partition, one bookkeeping.</para>
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
        var sealedState = mine.Count == 0
            ? $"no publication of {repo} is sealed for identity {identity}"
            : $"{repo}'s publication(s) for identity {identity}: "
              + string.Join(", ", mine.OrderBy(s => s.Source, StringComparer.Ordinal).Select(Describe));
        return new FirstImportPlan(null, null,
            $"{sealedState} — the seal does not choose a source's commit (policy "
            + "module-sync-per-manifest-hash), so the first import resolves the configured branch and "
            + "each module then syncs by its manifest hash");
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

    /// <summary>
    /// 🚨 Whether a ref a PERSON asked for can be compared to a sealed commit at all: a FULL
    /// 40-character hex sha, and nothing shorter (review on #4576) — a branch name may legally be
    /// hex, and no shape test can tell the two apart below full length.
    /// </summary>
    /// <param name="commitish">The ref a caller was asked for.</param>
    /// <returns>True when it can only be a commit.</returns>
    internal static bool IsFullCommitSha(string? commitish)
        => commitish is { Length: 40 } sha && sha.All(char.IsAsciiHexDigit);

    internal static bool SameCommit(string? a, string? b) =>
        !string.IsNullOrEmpty(a) && !string.IsNullOrEmpty(b)
        && (a.StartsWith(b, StringComparison.OrdinalIgnoreCase) || b.StartsWith(a, StringComparison.OrdinalIgnoreCase))
        && Math.Min(a.Length, b.Length) >= 7;

    /// <summary>
    /// What this identity has sealed of <paramref name="repo"/>, as one clause — the fact that tells
    /// an operator whether a synced NodeType will ADOPT bytes or COMPILE. Log copy only.
    /// </summary>
    private static string SealState(
        RepoIdentity repo, SealedReadOutcome readOutcome, IReadOnlyList<SealedSource> sealedForThisIdentity,
        string identity, PublicationLine? newerLine, string? headSha = null, string? lastSyncSha = null)
    {
        if (readOutcome is SealedReadOutcome.Unreadable)
            return $"the publication index for identity {identity} could not be read, so no prebuilt bytes "
                   + "can be verified and every changed NodeType compiles from its synced source";
        var mine = sealedForThisIdentity
            .Where(s => BelongsTo(s, repo, headSha ?? "", lastSyncSha))
            .OrderBy(s => s.Source, StringComparer.Ordinal)
            .ToList();
        var state = mine.Count == 0
            ? $"no publication of {repo} is sealed for identity {identity}"
            : $"{repo}'s publication(s) for identity {identity}: {string.Join(", ", mine.Select(Describe))}";
        var line = newerLine is null
            ? ""
            : $"; the registry has since sealed {newerLine.Version} under identity {newerLine.Identity}";
        return state + line + " — each NodeType adopts a bundle carrying its synced fingerprint, "
               + "otherwise it compiles from the synced source (policy module-sync-per-manifest-hash)";
    }

    private static string Describe(SealedSource s)
        => $"'{s.Source}' "
           + (s.IsSealed
               ? $"sealed at {(s.SourceCommit is { Length: > 0 } c ? Short(c) : "an unknown commit")}"
               : $"not usable here ({s.Refusal ?? "not sealed"})");

    private static string Short(string sha) => sha.Length > 8 ? sha[..8] : sha;
}
