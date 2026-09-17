using System.Collections.Immutable;
using MeshWeaver.Data;
using MeshWeaver.Hosting;
using Microsoft.Extensions.Logging;

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
    /// What an import that was ASKED for a ref lands on once the seal has spoken —
    /// <see cref="FirstImportPlan"/>'s three answers, for a person's <b>Update to latest</b> /
    /// <b>Re-import at this commit</b> (<see cref="DecideRequestedImport"/>, MeshWeaver#3845 hole 3)
    /// and for a green build (<see cref="DecideBuild"/>). The ref that was asked for is kept beside
    /// the one that is used, because the difference between them is exactly what a person has to be
    /// told.
    /// </summary>
    /// <param name="Commit">The commitish to import at — what was asked for, or the sealed commit.
    /// Null only on a hold.</param>
    /// <param name="SealedCommit">The commit this instance's publication of the repository was baked
    /// from, when the gate established one; null when the repository is not attributable.</param>
    /// <param name="HoldReason">Why nothing may be imported, or null.</param>
    /// <param name="Reason">Log copy (English) for whichever answer this is — always populated.</param>
    /// <param name="Notice">The same statement as keyed activity lines, rendered in the VIEWER's
    /// language (#3236); empty when the import runs exactly as asked.</param>
    public sealed record ImportPlan(
        string? Commit, string? SealedCommit, string? HoldReason, string Reason,
        ImmutableList<LogMessage> Notice)
    {
        /// <summary>True when an import may run at all.</summary>
        public bool Proceed => HoldReason is null;

        /// <summary>True when the gate chose the sealed commit over the ref that was asked for.</summary>
        public bool Redirected { get; init; }
    }

    /// <summary>
    /// 🚨 What a PERSON's import may land on — <b>Update to latest</b> (the configured branch) or
    /// <b>Re-import at this commit</b> (a typed commitish). MeshWeaver#3845 hole 3.
    ///
    /// <para><b>Why a person is gated at all.</b> <c>SyncRefContract</c> used to exempt a human:
    /// "only a human-initiated Update may read a branch tip". That rule keyed the protection on WHO
    /// asked, while the harm is keyed on WHAT lands — a module repository's sources on a tree no
    /// bundle for this identity was baked from are declined on their source fingerprint whoever
    /// pressed the button. So the answer is <see cref="DecideFirstImport"/>'s, with the requested ref
    /// in place of "the branch":</para>
    /// <list type="bullet">
    ///   <item><description>the index could not be READ → hold (#3461, "cannot tell" is never "clear
    ///   to proceed");</description></item>
    ///   <item><description>nothing attributable → exactly what was asked (hole 1's adjudication: a
    ///   repository no lane publishes could never be released from a hold);</description></item>
    ///   <item><description>every attributable publication sealed and agreeing on C → import C, and
    ///   say so when C is not what was asked;</description></item>
    ///   <item><description>torn / unknown commit / disagreeing → hold, and say which.</description></item>
    /// </list>
    ///
    /// <para>Attribution is <see cref="BelongsTo"/>'s — the repository marker, or for a seal that
    /// predates it the requested commit or the commit the Space already sits on — so a person's
    /// import and the webhook attribute the same seal to the same repository.</para>
    ///
    /// <para>🚨 There is deliberately no parameter that reads the tip anyway. A bypass would be the
    /// fail-open this decision exists to remove, with a flag's name on it; recovery from a hold is
    /// what the hold names — roll the instance, or fix the publishing lane.</para>
    /// </summary>
    /// <param name="repo">The repository the Space's sync source reads.</param>
    /// <param name="requested">What was asked for: the configured branch, or the typed commitish.</param>
    /// <param name="lastSyncSha">The commit the sync source currently sits on, or null.</param>
    /// <param name="readOutcome">Whether the seal index was READ (<see cref="SealedPublicationIndex.ReadingFor"/>).</param>
    /// <param name="sealedForThisIdentity">What the registry sealed under this instance's identity.</param>
    /// <param name="identity">This instance's framework identity.</param>
    /// <param name="newerLine">The newest publication line above this identity's, or null
    /// (<see cref="SealedPublicationIndex.NewerLineThan"/>) — names the direction of a hold.</param>
    /// <returns>The plan; never null.</returns>
    public static ImportPlan DecideRequestedImport(
        RepoIdentity repo, string requested, string? lastSyncSha, SealedReadOutcome readOutcome,
        IReadOnlyList<SealedSource> sealedForThisIdentity, string identity, PublicationLine? newerLine)
    {
        ArgumentNullException.ThrowIfNull(repo);
        ArgumentException.ThrowIfNullOrWhiteSpace(requested);
        ArgumentNullException.ThrowIfNull(sealedForThisIdentity);

        if (RefusedForUnreadableIndex(readOutcome, identity) is { HoldReason: { } unreadable })
            return new ImportPlan(null, null, WithLine(unreadable, newerLine), unreadable,
            [
                new LogMessage(
                        $"Nothing was imported: the publication index for this instance's framework identity "
                        + $"{identity} could not be read, so the commit its bundles were baked from is unknown.",
                        LogLevel.Warning)
                    .WithKey("activity.gitsync.seal.heldUnreadable", ("identity", identity)),
                // 🚨 EVERY hold names its direction, this one included (review on #4576). An
                // unreadable identity directory beside READABLE release markers is exactly the case
                // where the roll clause is the actionable half — and dropping it here would have made
                // one of the four holds silent about what releases it.
                DirectionLine(repo, identity, newerLine),
            ]);

        // 🚨 A REQUESTED REF IS ONLY A COMMIT WHEN IT LOOKS LIKE ONE (review on #4576). `SameCommit`
        // compares seven-character prefixes, so a branch whose NAME is hex — `abcdef1`, a real and
        // legal branch name — would be read as the sealed commit: the branch would be fetched with no
        // redirect, and it could attribute a marker-less seal to this repository. A branch is not a
        // coordinate; only a commit-shaped ref may take either leg.
        var requestedCommit = IsFullCommitSha(requested) ? requested : null;
        var mine = sealedForThisIdentity
            .Where(s => BelongsTo(s, repo, requestedCommit ?? "", lastSyncSha))
            .ToList();
        if (mine.Count == 0)
            return new ImportPlan(requested, null, null,
                $"no publication of {repo} is sealed for identity {identity} — this instance runs none, "
                + $"so the import reads '{requested}' as asked", []);

        var adopted = AdoptableCommit(mine, repo, identity);
        if (adopted.HoldReason is { } hold)
        {
            var reason = WithLine(hold, newerLine);
            return new ImportPlan(null, null, reason, reason,
                [adopted.Notice!, DirectionLine(repo, identity, newerLine)]);
        }

        var commit = adopted.Commit!;
        if (requestedCommit is not null && SameCommit(requestedCommit, commit))
            return new ImportPlan(requested, commit, null,
                $"'{requested}' is the commit {repo} is sealed at for identity {identity} — imported as asked", []);

        return new ImportPlan(commit, commit, null,
            WithLine(
                $"'{requested}' is not what this instance runs: {repo} is sealed at {Short(commit)} for identity "
                + $"{identity} — importing that commit instead", newerLine),
            [
                LandsOnSealLine(repo, commit, identity, requested),
                DirectionLine(repo, identity, newerLine),
            ])
        {
            Redirected = true,
        };
    }

    /// <summary>
    /// What a GREEN BUILD at <paramref name="headSha"/> lands on — <see cref="Decide(RepoIdentity, string, string?, IReadOnlyList{SealedSource}, string, PublicationLine?)"/>
    /// with its hold turned into a landing wherever one is possible. The webhook was the last
    /// unattended lane that only HELD: the first import (#4212), the seal's arrival
    /// (<c>SealedPublicationSyncReconciler</c>, #4209) and the boot install (#4259) all land on the
    /// sealed commit.
    ///
    /// <para>Where <c>Decide</c> proceeds, this proceeds at <paramref name="headSha"/> — unchanged,
    /// including <c>Decide</c>'s "any sealed publication at the built commit is enough". Where
    /// <c>Decide</c> holds, this lands on the sealed commit C when every attributable publication is
    /// usable and they agree (<see cref="DecideFirstImport"/>'s strictness — a good seal must not
    /// override a torn sibling), and otherwise holds with <c>Decide</c>'s own reason. A landing keeps
    /// that reason as its <see cref="ImportPlan.Reason"/>: the build's commit still did not arrive, and
    /// the hold note a source already at C records is that sentence.</para>
    /// </summary>
    /// <param name="repo">The repository the green build is of.</param>
    /// <param name="headSha">The built commit.</param>
    /// <param name="lastSyncSha">The commit the sync source currently sits on, or null.</param>
    /// <param name="sealedForThisIdentity">What the registry sealed under this instance's identity.</param>
    /// <param name="identity">This instance's framework identity — log copy only.</param>
    /// <param name="newerLine">The newest publication line above this identity's, or null.</param>
    /// <returns>The plan; never null.</returns>
    public static ImportPlan DecideBuild(
        RepoIdentity repo, string headSha, string? lastSyncSha,
        IReadOnlyList<SealedSource> sealedForThisIdentity, string identity, PublicationLine? newerLine)
    {
        ArgumentNullException.ThrowIfNull(repo);
        ArgumentException.ThrowIfNullOrWhiteSpace(headSha);
        ArgumentNullException.ThrowIfNull(sealedForThisIdentity);

        var mine = sealedForThisIdentity
            .Where(s => BelongsTo(s, repo, headSha, lastSyncSha))
            .ToList();
        var verdict = Decide(repo, headSha, lastSyncSha, sealedForThisIdentity, identity, newerLine);
        if (verdict.Proceed)
            return new ImportPlan(headSha,
                mine.Any(s => s.IsSealed && SameCommit(s.SourceCommit, headSha)) ? headSha : null,
                null,
                mine.Count == 0
                    ? $"no publication of {repo} is sealed for identity {identity} — imported at the built commit"
                    : $"{repo} is sealed at the built commit {Short(headSha)} for identity {identity}",
                []);

        var holdReason = verdict.HoldReason ?? "held";
        var adopted = AdoptableCommit(mine, repo, identity);
        if (adopted.Commit is not { } commit)
            return new ImportPlan(null, null, holdReason, holdReason,
                [adopted.Notice!, DirectionLine(repo, identity, newerLine)]);

        return new ImportPlan(commit, commit, null, holdReason,
            [
                LandsOnSealLine(repo, commit, identity, Short(headSha)),
                DirectionLine(repo, identity, newerLine),
            ])
        {
            Redirected = true,
        };
    }

    /// <summary>
    /// The one commit every attributable publication agrees on, or the reason there is none — the
    /// core <see cref="DecideFirstImport"/>, <see cref="DecideRequestedImport"/> and
    /// <see cref="DecideBuild"/> share, so the three can never disagree about which seal is usable.
    /// </summary>
    private static (string? Commit, string? HoldReason, LogMessage? Notice) AdoptableCommit(
        IReadOnlyList<SealedSource> mine, RepoIdentity repo, string identity)
    {
        // 🚨 EVERY attributable publication must be usable, not merely ONE of them. Selecting the
        // sealed ones first and deciding on those would let a good seal OVERRIDE a torn sibling of
        // the same repository — `plugins` sealed at C beside a `plugins-extra` with no completion
        // sentinel would pin the Space at C while part of that repository's bytes are missing here.
        // That is the fail-open this decision exists to remove, one level in. So the refusal is
        // evaluated over `mine`, before any selection narrows it.
        var unusable = mine
            .Where(s => !s.IsSealed || s.SourceCommit is not { Length: > 0 })
            .OrderByDescending(s => s.IsSealed)
            .ThenBy(s => s.Source, StringComparer.Ordinal)
            .ToList();
        if (unusable.Count > 0)
        {
            var witness = unusable[0];
            var others = unusable.Count > 1 ? $" (and {unusable.Count - 1} more of {repo})" : "";
            return witness.IsSealed
                ? (null,
                    $"this instance's publication of '{witness.Source}' ({repo}) is sealed at an unknown "
                    + $"commit (identity {identity}){others} — an unattended import has no commit to land on",
                    new LogMessage(
                            $"Nothing was imported: this instance's publication of '{witness.Source}' ({repo}) "
                            + "is sealed at an unknown commit, so there is no commit to land on.",
                            LogLevel.Warning)
                        .WithKey("activity.gitsync.seal.heldUnknownCommit",
                            ("source", witness.Source), ("repo", repo.ToString())))
                : (null,
                    $"this instance's publication of '{witness.Source}' ({repo}) is not sealed "
                    + $"(identity {identity}: {witness.Refusal}){others} — an unattended import waits for it",
                    new LogMessage(
                            $"Nothing was imported: this instance's publication of '{witness.Source}' ({repo}) "
                            + $"is not sealed for framework identity {identity} ({witness.Refusal}).",
                            LogLevel.Warning)
                        .WithKey("activity.gitsync.seal.heldNotSealed",
                            ("source", witness.Source), ("repo", repo.ToString()), ("identity", identity),
                            ("refusal", witness.Refusal ?? "")));
        }

        // 🚨 Several sealed publications of ONE repository that disagree about the commit is a state
        // no reading here can resolve — picking one would put the Space on a tree half this
        // instance's own bytes were not baked from. Hold and name them, exactly as a torn seal does.
        var commit = mine[0].SourceCommit!;
        if (mine.Any(s => !SameCommit(s.SourceCommit, commit)))
        {
            var commits = string.Join(", ", mine
                .OrderBy(s => s.Source, StringComparer.Ordinal)
                .Select(s => $"'{s.Source}' at {Short(s.SourceCommit!)}"));
            return (null,
                $"this instance's publications of {repo} disagree about the commit (identity {identity}: "
                + commits + ") — an unattended import cannot choose between them",
                new LogMessage(
                        $"Nothing was imported: this instance's publications of {repo} disagree about the "
                        + $"commit ({commits}), so no single commit matches the bundles it runs.",
                        LogLevel.Warning)
                    .WithKey("activity.gitsync.seal.heldDisagree",
                        ("repo", repo.ToString()), ("commits", commits)));
        }
        return (commit, null, null);
    }

    /// <summary>The Warning line a redirect writes: what was asked, what lands, and why.</summary>
    private static LogMessage LandsOnSealLine(RepoIdentity repo, string commit, string identity, string requested)
        => new LogMessage(
                $"{repo} is sealed at {Short(commit)} for this instance (framework identity {identity}) — "
                + $"importing that commit instead of {requested}, so the Space's sources match the bundles "
                + "this instance runs.",
                LogLevel.Warning)
            .WithKey("activity.gitsync.seal.landsOnSeal",
                ("repo", repo.ToString()), ("sealed", Short(commit)), ("identity", identity),
                ("requested", requested));

    /// <summary>
    /// The line that says what RELEASES a hold or a redirect — the direction a845184a89 put on the
    /// webhook's note, as a viewer-localized activity line. Roll when a newer line is sealed that
    /// this instance does not run; otherwise both remedies, never a guess between them.
    /// </summary>
    private static LogMessage DirectionLine(RepoIdentity repo, string identity, PublicationLine? newerLine)
        => newerLine is not null
            ? new LogMessage(
                    $"The registry has since sealed {newerLine.Version} under framework identity "
                    + $"{newerLine.Identity}, which this instance does not run — this Space advances when "
                    + "this instance is ROLLED onto it, not when another publication lands.",
                    LogLevel.Information)
                .WithKey("activity.gitsync.seal.advanceByRoll",
                    ("version", newerLine.Version), ("newerIdentity", newerLine.Identity))
            : new LogMessage(
                    $"This Space advances when a newer commit of {repo} is sealed for framework identity "
                    + $"{identity} (the publishing lane), or when this instance is rolled onto a platform whose "
                    + "publication is. Importing the branch tip instead is not offered: it would put sources "
                    + "ahead of the bundles this instance runs.",
                    LogLevel.Information)
                .WithKey("activity.gitsync.seal.advanceBySeal",
                    ("repo", repo.ToString()), ("identity", identity));

    /// <summary>
    /// 🚨 The PRECONDITION on every verdict below: a reading that FAILED holds everything.
    ///
    /// <para><see cref="Decide(RepoIdentity, string, string?, IReadOnlyList{SealedSource}, string)"/>
    /// answers <see cref="Verdict.Go"/> when no sealed source is attributable to the repository —
    /// correctly, because an instance that runs no publication of it is not this gate's business.
    /// An UNREADABLE index produces the same empty list, so a total reader failure used to pass
    /// EVERY repository: the exact inversion of the rule, with nothing red anywhere.</para>
    ///
    /// <para>This is deliberately NOT a parameter of <c>Decide</c>. The question is not per
    /// repository — if the index could not be read, no per-repository answer is trustworthy — so
    /// the caller asks ONCE per delivery, before any verdict, and holds every source when it
    /// answers. #3461 phase 5 (disposing of the flat compatibility copy) is the documented
    /// trigger, and it is live since that landed: a pointer read while it is being replaced falls
    /// back to a source directory that no longer holds a publication, so the source reads as
    /// unsealed AND unattributable — which this gate would otherwise answer with Go.</para>
    /// </summary>
    /// <param name="outcome">What <see cref="SealedPublicationIndex.ReadingFor"/> reported.</param>
    /// <param name="identity">This instance's framework identity — log copy only.</param>
    /// <returns>A holding <see cref="Verdict"/>, or null when the reading is usable.</returns>
    public static Verdict? RefusedForUnreadableIndex(SealedReadOutcome outcome, string identity)
        => outcome is SealedReadOutcome.Unreadable
            ? new Verdict(false,
                $"the publication index for this instance's framework identity {identity} could "
                + "not be READ (see the SealedPublicationIndex warning above it) — that is an "
                + "absence of measurement, not an empty index, so every source is held rather "
                + "than advanced. 'Cannot tell' is never 'clear to proceed' (#3461)")
            : null;

    /// <summary>
    /// The same precondition for a FIRST import (<see cref="DecideFirstImport"/>), whose two
    /// unattended callers — <c>ModuleDiscoveryService.FirstImport</c> and
    /// <c>InstanceAutoRegistrationService</c>'s boot default install — read the index themselves.
    ///
    /// <para>🚨 It is a separate method rather than an overload of <c>DecideFirstImport</c> on
    /// purpose: an added overload makes every parameterless <c>&lt;see cref&gt;</c> to that name
    /// ambiguous (<c>CS0419</c> under <c>-warnaserror</c>), here and in every repository that pins
    /// this assembly.</para>
    ///
    /// <para>A first import reads WORSE from an unreadable index than a green build does, because
    /// only the repository MARKER can attribute a seal there — no built commit, no
    /// <c>LastSyncCommitSha</c> — so an unattributable source is indistinguishable from "this
    /// instance runs no publication of that repository" and the Space would be populated from the
    /// branch TIP. A held first import leaves a Space with its sync entry and no content, which
    /// the next scan, the next seal arrival or the next boot completes.</para>
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
        => Decide(repo, headSha, lastSyncSha, sealedForThisIdentity, identity, null);

    /// <summary>
    /// <see cref="Decide(RepoIdentity, string, string?, IReadOnlyList{SealedSource}, string)"/>,
    /// plus the one fact that tells the two reasons for a hold apart.
    ///
    /// <para>🚨 A hold's sentence — "built at S, not sealed for this instance (identity I:
    /// 'plugins' is sealed at C)" — is equally consistent with <b>nothing having sealed recently</b>
    /// (the publishing lane is broken; act on the lane) and with <b>seals advancing under a newer
    /// identity this instance does not run</b> (the image is behind; act with a roll). Those call
    /// for opposite work, and the second has twice been read as the first
    /// (MeshWeaver.Plugins#1823, #1798 — both filed against a lane that was green, with an empty
    /// inbox, while memex.meshweaver.cloud sat held at <c>627fb3cd</c> under <c>sd608997…</c> and
    /// the live publication was sealed under <c>s799247a…</c>). Passing
    /// <see cref="SealedPublicationIndex.NewerLineThan"/> makes the note say which, in the same
    /// sentence, to the operator who is already reading it.</para>
    ///
    /// <para>Null <paramref name="newerLine"/> is the honest "cannot place this instance on a
    /// line" — the note then says exactly what it said before, and never guesses a direction.</para>
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
        return new Verdict(false, WithLine(reason, newerLine));
    }

    /// <summary>
    /// What a FIRST import of <paramref name="repo"/> must land on — the adopt-then-sync half of
    /// <see cref="Decide(RepoIdentity, string, string?, IReadOnlyList{SealedSource}, string)"/>, for the one unattended importer that has no built commit to be gated
    /// against (<c>ModuleDiscoveryService.FirstImport</c>, MeshWeaver#3845 hole 2).
    ///
    /// <para><b>Why this is a separate decision and not <see cref="Decide(RepoIdentity, string, string?, IReadOnlyList{SealedSource}, string)"/>.</b> The gate answers
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

        // 🚨 EVERY attributable publication must be usable and they must agree — see
        // AdoptableCommit, which the requested-import and green-build decisions share.
        //
        // This is deliberately STRICTER than `Decide`, which proceeds when ANY sealed source sits at
        // the built commit. The two answer different questions: `Decide` admits a tree a build
        // proved onto a source already carrying content, while this decides what a Space that holds
        // NOTHING YET is first populated with. There is no partial state to preserve here and no
        // second chance to be more careful later, so "cannot tell" is never "clear to proceed".
        var adopted = AdoptableCommit(mine, repo, identity);
        if (adopted.Commit is not { } commit)
            return Hold(adopted.HoldReason!);

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

    /// <summary>
    /// 🚨 Whether a ref a PERSON asked for can be compared to a sealed commit at all: a FULL
    /// 40-character hex sha, and nothing shorter (review on #4576).
    ///
    /// <para><see cref="SameCommit"/> matches on a seven-character prefix, which is right for two
    /// machine-produced shas and wrong for a ref somebody typed: a branch name may legally be hex,
    /// so <c>abcdef1</c> — a branch — would have read as the sealed commit, been fetched AS a branch
    /// with no redirect, and could even have attributed a marker-less seal to this repository. A
    /// branch is a pointer, not a coordinate, and no shape test can tell the two apart below full
    /// length. Requiring the full sha costs nothing: a shortened one simply redirects onto the
    /// sealed commit it names, which is the same tree, and the activity says so.</para>
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
    /// Appends the direction to a hold reason when — and only when — the caller could establish it.
    /// The clause names the REMEDY, because "sealed at an older commit" is what an operator sees and
    /// "roll this instance" is what they have to do; leaving them to join a note on the sync config
    /// to a directory listing under the published root is the step that did not happen twice.
    /// </summary>
    private static string WithLine(string reason, PublicationLine? newerLine)
        => newerLine is null
            ? reason
            : reason
              + $". The registry has since sealed {newerLine.Version} under framework identity "
              + $"{newerLine.Identity}, which this instance does not run — so this source advances "
              + "when this instance's IMAGE does (a roll), NOT when another publication lands";

    private static string Short(string sha) => sha.Length > 8 ? sha[..8] : sha;
}
