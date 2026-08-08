using System.Collections.Immutable;
using System.ComponentModel;
using MeshWeaver.Messaging;

namespace MeshWeaver.PluginCatalog;

/// <summary>
/// What one configured plugin repo carries, and what THIS instance does with it — the answer to
/// "which modules exist on git, which of them are on the mesh, and when did the missing ones appear?"
///
/// <para><b>Why this node exists at all.</b> Before #833 a module with no <c>{Space}/_GitSync</c>
/// entry was simply not on the mesh and <b>nothing said so</b>: memex carried 37 sync configs and
/// zero install records, and two modules from the very same repo (<c>Planning</c>, <c>Ifrs17</c>)
/// were absent purely because nobody had created their entries. Absence was invisible. This record
/// makes it visible: every module the repo ships appears here with a
/// <see cref="ModuleDiscoveryStatus"/> and a reason, whether or not it was provisioned.</para>
///
/// <para><b>One node per configured source, rewritten in place</b> — the same shape (and the same
/// Admin-partition home) as <c>BuildCompletion</c>, for the same reason: subscribers want "what does
/// this repo look like from here, now", not a firehose of per-scan nodes. History is in the node's
/// own version history.</para>
///
/// <para>Written exclusively under <c>ImpersonateAsSystem</c> by
/// <see cref="ModuleDiscoveryService"/> — a scan runs on boot and on a green build, where there is
/// no user at all.</para>
/// </summary>
public record ModuleDiscovery
{
    /// <summary>The node type registered for these records.</summary>
    public const string NodeType = "ModuleDiscovery";

    /// <summary>The path segment discovery records are anchored under, inside the Admin partition.</summary>
    public const string SatelliteSegment = "_Discovery";

    /// <summary>
    /// The canonical node path for one configured source's discovery record:
    /// <c>Admin/_Discovery/{owner}.{repo}</c>.
    ///
    /// <para>The <b>Admin</b> partition because "what does this instance carry from that repo" is
    /// platform-level infrastructure, not any one Space's data — the same reasoning that puts
    /// <c>Admin/_Build/{owner}.{repo}</c> there, and it keeps the record readable when every Space it
    /// talks about is missing. Anchored under a real partition root rather than sitting top-level,
    /// per the satellite rule.</para>
    /// </summary>
    /// <param name="repoPath">The configured repo URL or local path.</param>
    public static string PathFor(string repoPath)
    {
        var (owner, repo) = SplitRepo(repoPath);
        return $"Admin/{SatelliteSegment}/{Sanitize(owner)}.{Sanitize(repo)}";
    }

    /// <summary>
    /// The last two segments of a repo URL or local path, as (owner, repo) — the same identity
    /// <c>BuildCompletion.PathFor</c> keys on, so a repo's build record and its discovery record sit
    /// side by side under two well-known segments and are trivially correlated. A trailing
    /// <c>.git</c> is stripped; a single-segment path yields an empty owner.
    /// </summary>
    /// <param name="repoPath">The configured repo URL or local path.</param>
    /// <returns>The owner and repository name.</returns>
    public static (string Owner, string Repo) SplitRepo(string repoPath)
    {
        var trimmed = (repoPath ?? "").Trim().Replace('\\', '/').TrimEnd('/');
        var slash = trimmed.LastIndexOf('/');
        if (slash < 0)
            return ("", StripGit(trimmed));
        var repo = StripGit(trimmed[(slash + 1)..]);
        var rest = trimmed[..slash];
        var prevSlash = rest.LastIndexOf('/');
        // `git@github.com:owner/repo` has no slash before the owner — split on the scp-style colon.
        var owner = prevSlash >= 0 ? rest[(prevSlash + 1)..] : rest;
        var colon = owner.LastIndexOf(':');
        return (colon >= 0 ? owner[(colon + 1)..] : owner, repo);
    }

    private static string StripGit(string s) =>
        s.EndsWith(".git", StringComparison.OrdinalIgnoreCase) ? s[..^4] : s;

    /// <summary>Path separators would fabricate node hierarchy, so they never survive into a path
    /// segment. The value comes from deployment configuration, but it is a free-form string.</summary>
    private static string Sanitize(string s)
    {
        var cleaned = new string((s ?? "").Select(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' ? c : '-')
            .ToArray()).Trim('-');
        return cleaned.Length == 0 ? "source" : cleaned;
    }

    /// <summary>The repository this record describes (<c>PluginCatalog:Sources:N:RepoPath</c>).</summary>
    [Description("Repository")]
    [Translation("de", "Repository")]
    public string RepositoryUrl { get; init; } = "";

    /// <summary>The configured source's display name.</summary>
    [Description("Source")]
    [Translation("de", "Quelle")]
    public string SourceName { get; init; } = "";

    /// <summary>The git ref the modules were enumerated at (a branch, <c>HEAD</c>, or a commit sha
    /// when the scan was triggered by a green build).</summary>
    [Description("Git ref")]
    [Translation("de", "Git-Ref")]
    public string GitRef { get; init; } = "";

    /// <summary>Whether the source had auto-sync on for this scan — i.e. whether a missing module
    /// was provisioned or merely reported.</summary>
    [Description("Auto-sync discovered modules")]
    [Translation("de", "Entdeckte Module automatisch synchronisieren")]
    public bool AutoSync { get; init; }

    /// <summary>When this record was last written (UTC).</summary>
    [Description("Last scanned")]
    [Translation("de", "Zuletzt geprüft")]
    public DateTimeOffset LastScannedAt { get; init; }

    /// <summary>Every module the repo ships at <see cref="GitRef"/>, plus any module this instance
    /// still syncs that the repo no longer carries (<see cref="ModuleDiscoveryStatus.Orphaned"/>),
    /// ordered by id.</summary>
    public ImmutableList<DiscoveredModule> Modules { get; init; } = ImmutableList<DiscoveredModule>.Empty;
}

/// <summary>One module of a configured plugin repo, as this instance sees it.</summary>
public record DiscoveredModule
{
    /// <summary>The module id — the repo folder name, and the Space id it maps to.</summary>
    public string Id { get; init; } = "";

    /// <summary>The module's display name from its root node, when it has one.</summary>
    public string? Name { get; init; }

    /// <summary>What this instance did (or did not do) with the module.</summary>
    public ModuleDiscoveryStatus Status { get; init; }

    /// <summary>The speaking reason behind <see cref="Status"/> — a refusal's cause, a failure's
    /// message, the first import's outcome. Never null for a status that is not self-explanatory:
    /// the whole point of the record is that nothing is a silent skip.</summary>
    public string? Detail { get; init; }

    /// <summary>When this instance provisioned the module (UTC), carried forward across scans so the
    /// record answers "what appeared, and when?".</summary>
    public DateTimeOffset? ProvisionedAt { get; init; }

    /// <summary>When this instance first saw the module in the repo (UTC), carried forward across
    /// scans — so a module that sat un-provisioned for a week says so.</summary>
    public DateTimeOffset? FirstSeenAt { get; init; }
}

/// <summary>What a discovery scan concluded about one module. Every value except
/// <see cref="Synced"/> and <see cref="Installed"/> is something an operator may want to act on.</summary>
public enum ModuleDiscoveryStatus
{
    /// <summary>The instance carries a <c>{Space}/_GitSync</c> for this module — nothing to do.</summary>
    Synced,

    /// <summary>The instance carries a plugin-catalog install record for this module (the other way
    /// content arrives) — nothing to do.</summary>
    Installed,

    /// <summary>Missing here, and auto-sync is OFF — reported, nothing written. This is the state
    /// <c>Planning</c> and <c>Ifrs17</c> were in, silently, before #833.</summary>
    Discovered,

    /// <summary>Missing here, and this scan created its Space + <c>_GitSync</c> as SYSTEM.</summary>
    Provisioned,

    /// <summary>A node already occupies the module's path but carries no <c>_GitSync</c> for this
    /// repo. NOT auto-provisioned: wiring a <c>_GitSync</c> onto somebody's existing Space makes the
    /// partition system-owned and retracts its owner's grants, which an unattended scan must never
    /// do to content it did not create.</summary>
    Occupied,

    /// <summary>Refused by the free-vs-commercial rule (#830): a priced module needs an entitled
    /// global admin, and an unattended scan has no authorizing principal.</summary>
    Refused,

    /// <summary>Provisioning was attempted and threw. <see cref="DiscoveredModule.Detail"/> carries
    /// the reason.</summary>
    Failed,

    /// <summary>This instance syncs the module from this repo, but the repo no longer ships it.
    /// Deliberately NOT auto-deleted — surfaced so a human decides.</summary>
    Orphaned,
}
