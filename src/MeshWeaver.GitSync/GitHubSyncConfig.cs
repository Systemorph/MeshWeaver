using System.ComponentModel;
using System.Text.Json.Serialization;

using MeshWeaver.Messaging;

namespace MeshWeaver.GitSync;

/// <summary>
/// One GitHub sync source of a Space, stored as a MeshNode at <c>{spaceId}/_GitSync</c>
/// (the primary source) or <c>{spaceId}/_GitSync/{sourceId}</c> (each additional source).
/// Holds the repository the Space syncs with and the allowed <see cref="Direction"/> —
/// NOT a secret (the per-user OAuth credential lives separately at
/// <c>{userId}/_Provider/GitHub</c> and is never serialized into exported content).
/// Editable by Space admins.
///
/// <para>This record IS the editor: the GitHub Sync settings tab renders it through the
/// standard mesh-node editor (the same data-bound, <c>stream.Update</c>-persisting editor every
/// node uses), so these attributes drive the generated controls. The last-sync fields are written
/// by the sync operation and shown read-only — <see cref="BrowsableAttribute"/> hides them from
/// the editable form.</para>
/// </summary>
public record GitHubSyncConfig
{
    /// <summary>The target repository URL, e.g. <c>https://github.com/owner/repo</c>.</summary>
    [Description("Repository URL")]
    [Translation("de", "Repository-URL")]
    public string? RepositoryUrl { get; init; }

    /// <summary>The branch to commit to. Defaults to <c>main</c>.</summary>
    [Description("Branch")]
    [Translation("de", "Branch")]
    public string Branch { get; init; } = "main";

    /// <summary>
    /// Optional path prefix inside the repo to mirror the Space subtree into
    /// (e.g. <c>content</c>). Empty → the repository root. Mirror semantics apply
    /// only within this subdirectory; files elsewhere in the repo are untouched.
    /// </summary>
    [Description("Subdirectory (optional — blank = repository root)")]
    [Translation("de", "Unterverzeichnis (optional — leer = Repository-Wurzel)")]
    public string? Subdirectory { get; init; }

    /// <summary>
    /// The direction this source is allowed to sync in: bidirectional (default), export-only
    /// (mesh → repo — imports rejected) or import-only (repo → mesh — exports rejected).
    /// Enforced by <see cref="GitHubSyncService"/> on every sync operation.
    /// </summary>
    [Description("Sync direction")]
    [Translation("de", "Synchronisierungsrichtung")]
    public SyncDirection Direction { get; init; } = SyncDirection.Bidirectional;

    /// <summary>
    /// Two-way conflict resolution on <b>import</b> ("Update to latest"). When true, an update
    /// NEVER overwrites (or prunes) a node that was changed on the server SINCE THE LAST SYNC —
    /// the server copy is preserved and carried back to GitHub on the next commit ("newer on the
    /// server wins → GitHub"). When false (the default) import is git-first: the repo overwrites
    /// the live node, which silently loses local edits made between syncs. A <b>forced</b> update
    /// (<c>force: true</c>) overrides this and overwrites regardless — the escape hatch for
    /// deliberately discarding local changes back to the repo state.
    ///
    /// <para>"Since the last sync" is measured against <see cref="LastSyncedAt"/>: a node whose
    /// <c>MeshNode.LastModified</c> is newer than the last recorded sync counts as a local
    /// edit. Only takes effect once a first sync has recorded that timestamp.</para>
    /// </summary>
    [Description("Two-way (don't overwrite nodes changed on the server since the last sync — commit them back instead)")]
    [Translation("de", "Zweiweg (Nodes, die seit der letzten Synchronisierung auf dem Server geändert wurden, nicht überschreiben — stattdessen zurück committen)")]
    public bool TwoWay { get; init; }

    /// <summary>Create <see cref="Branch"/> if it does not exist yet. Default true.
    ///
    /// <para>🚨 <see cref="JsonIgnoreCondition.Never"/> is REQUIRED, not decoration. The hub
    /// serializer runs <c>DefaultIgnoreCondition = WhenWritingDefault</c>, which compares against
    /// <c>default(bool)</c> — i.e. <c>false</c>. A property whose initializer is <c>true</c>
    /// therefore has its <c>false</c> OMITTED on write, and the initializer puts <c>true</c> back on
    /// read: unticking this in the settings tab silently re-ticked itself, and an unattended
    /// provisioning could not express "never create anything in this repo" at all.</para>
    /// </summary>
    [Description("Create the branch if it doesn't exist")]
    [Translation("de", "Branch anlegen, falls er nicht existiert")]
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public bool CreateBranchIfMissing { get; init; } = true;

    /// <summary>Create the repository (private) if it does not exist yet. Default true.
    /// <see cref="JsonIgnoreCondition.Never"/> for the same reason as
    /// <see cref="CreateBranchIfMissing"/> — a <c>= true</c> bool cannot otherwise persist a
    /// <c>false</c>.</summary>
    [Description("Create the repository (private) if it doesn't exist")]
    [Translation("de", "Repository (privat) anlegen, falls es nicht existiert")]
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public bool CreateRepoIfMissing { get; init; } = true;

    /// <summary>
    /// Gitignore-style patterns for paths (relative to the Space root) that do NOT sync — neither
    /// exported to the repo nor imported from it. Unset → <see cref="SyncIgnore.Default"/>
    /// (<c>Release/</c>: the compile pipeline's release-request bookkeeping, one node per
    /// recompile, forever). Set to an empty list to sync everything; <c>!pattern</c> re-includes
    /// (last match wins); <c>*</c>/<c>**</c>/<c>?</c> globs as in <c>.gitignore</c>.
    /// </summary>
    [Description("Ignore patterns (gitignore-style, relative to the Space root; unset = default: Release/)")]
    [Translation("de", "Ignorier-Muster (gitignore-Stil, relativ zur Space-Wurzel; leer = Standard: Release/)")]
    public string[]? Ignore { get; init; }

    /// <summary>
    /// The two-way CONFLICT HORIZON: when a sync operation last actually RECONCILED mesh and repo —
    /// a commit/export, or an import that landed cleanly with nothing preserved. A node whose
    /// <c>LastModified</c> is newer than this counts as a pending server-side change and is
    /// protected from overwrite (<see cref="TwoWay"/>) and from the prune
    /// (<see cref="SyncDirection.Bidirectional"/>). 🚨 Deliberately NOT advanced by a no-op update
    /// (unchanged content fingerprint) nor by an import that preserved server-newer nodes — either
    /// would move the horizon past pending uncommitted changes and disarm the protection, so a later
    /// push would prune them (issues #675/#677). "Have we seen the commit?" is
    /// <see cref="LastSyncCommitSha"/>, not this field. Set by the sync operation; not user-editable.
    ///
    /// <para>🚨 <b>This is NOT "when did this source last sync", and must never be read — or
    /// labelled — as if it were</b> (issue #3581). The three suppressions above are deliberate and
    /// load-bearing, so a source that syncs every day can legitimately carry a horizon months old:
    /// measured 2026-09-07, <c>Edu/_GitSync</c> read <c>lastSyncedAt</c> 2026-07-11 on memex and
    /// 2026-08-07 on memex-cloud while both carried a <c>lastSyncCommitSha</c> from that morning.
    /// Neither field was wrong; the settings tab rendered the pair as ONE event and so reported a
    /// contradiction. "When did a sync last run here, and what came of it" is
    /// <see cref="LastSyncAttemptAt"/> + <see cref="LastSyncOutcome"/>.</para>
    /// </summary>
    [Browsable(false)]
    public DateTimeOffset? LastSyncedAt { get; init; }

    /// <summary>
    /// The last commit this source has seen/synced — the "up to date?" comparison
    /// (<see cref="GitHubSyncService.AskBranchState"/>) and the base for the git-diff import scope.
    /// Unlike <see cref="LastSyncedAt"/> it is safe to advance on a NO-OP update: it records "the
    /// repo content at this commit is what the mesh already imported", not "we reconciled at this
    /// moment" — so a repo commit touching no node files does not leave the Space forever "behind"
    /// (issue #677). Set by the sync operation; not user-editable.
    /// </summary>
    [Browsable(false)]
    public string? LastSyncCommitSha { get; init; }

    /// <summary>
    /// 🚨 <b>The RECENCY field: when a sync operation last RAN on this source to a verdict.</b>
    /// Stamped by EVERY conclusion — including the ones that deliberately advance nothing else (a
    /// fingerprint-matched no-op, a two-way import that preserved server-newer nodes, an import
    /// that landed nothing). It answers the first question of every "why is this partition
    /// compiling instead of adopting" investigation, which before issue #3581 could only be
    /// answered by comparing the node's own <c>lastModified</c> against pair tags in a container
    /// registry — and <c>lastModified</c> is not that clock either, since
    /// <c>stream.Update</c> does not re-stamp it.
    ///
    /// <para>🚨 Deliberately SEPARATE from <see cref="LastSyncedAt"/>, and the one change that must
    /// never be made is to fold them together: the horizon would then advance on exactly the
    /// outcomes that suppress it today, moving past pending uncommitted server changes, disarming
    /// the two-way protection and letting a later push prune them (issues #675 / #677 / #2229
    /// item C). One field cannot be both "the last instant we reconciled" and "the last instant we
    /// tried".</para>
    ///
    /// <para>Set by the sync operation; not user-editable. Absent on a source that has never
    /// synced, and on one whose last sync predates this field.</para>
    /// </summary>
    [Browsable(false)]
    public DateTimeOffset? LastSyncAttemptAt { get; init; }

    /// <summary>
    /// What the run recorded at <see cref="LastSyncAttemptAt"/>: the importer's own outcome literal
    /// (<c>Imported</c>, <c>ImportedWithErrors</c>, <c>Skipped</c>, <c>Failed</c>) for an import, or
    /// <c>Committed</c> for an export. A WIRE value, not display text — the settings tab localizes
    /// it for the viewer, and nothing branches on it (the baseline decision is
    /// <c>GitHubSyncService.MayAdvanceBaseline</c>'s, taken off the structured result).
    ///
    /// <para>Set by the sync operation; not user-editable.</para>
    /// </summary>
    [Browsable(false)]
    public string? LastSyncOutcome { get; init; }

    /// <summary>
    /// WHY the last attempt did not move the source — the hold reason of the sealed-publication
    /// gate ("built at X, not sealed for this instance …"), or the reconciler's finding — so an
    /// operator reading the config sees the cause rather than only the outcome. Cleared by the
    /// next attempt that lands. Set by the sync operation; not user-editable.
    /// </summary>
    [Browsable(false)]
    public string? LastSyncNote { get; init; }
}
