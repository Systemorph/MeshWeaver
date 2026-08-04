using System.Text.Json.Serialization;

namespace MeshWeaver.Graph;

/// <summary>
/// The record of a repository's most recent SUCCESSFUL CI build, written by the GitHub webhook and
/// read by anything that wants to react to "this repo has a new green build".
///
/// <para><b>Why this type lives in MeshWeaver.Graph.</b> It is deliberately owned by NEITHER side of
/// the flow. <c>MeshWeaver.GitSync</c> writes it (it owns the webhook transport: signature
/// verification, payload parsing, repo resolution) and <c>MeshWeaver.PluginCatalog</c> subscribes to
/// it (it owns plugin semantics: what a module is and when one has actually changed). Putting the
/// contract in either project would force a compile-time dependency on the other — and
/// PluginCatalog already references GitSync, so a handler interface in GitSync could never be
/// implemented there without closing a reference cycle. Both reference <c>MeshWeaver.Graph</c>, so
/// the node is the ONLY coupling: the producer knows nothing about plugins, the consumer knows
/// nothing about webhooks, and a third consumer can be added later without touching either.</para>
///
/// <para><b>This is a FACT, not a command.</b> The node says "repo X built green at sha Y" and
/// nothing more. It is rewritten on every green run — including doc-only commits, reverts, and
/// re-runs of an unchanged tree. Consumers MUST NOT treat an emission as "something changed":
/// deciding that is the consumer's job, against content identity. The plugin catalog compares each
/// module's <c>ModuleManifest.ModuleVersion</c> (a hash over the module's sorted (path, file-hash)
/// pairs) and stays silent for every module whose hash is unchanged. Reacting to the EVENT instead
/// of the CONTENT would push an install to every instance on every green run.</para>
///
/// <para><b>One node per repository, updated in place</b> — not one node per run. Subscribers want
/// "the latest green build of this repo", which is exactly a stream of this node; a node-per-run
/// design would grow without bound and give subscribers a firehose to filter. History, when needed,
/// is already in the node's own version history.</para>
///
/// <para>See <c>Doc/Architecture/PluginUpdateOnGreenBuild</c> for the end-to-end flow and how to
/// configure the webhook.</para>
/// </summary>
public record BuildCompletion
{
    /// <summary>The node type registered for these records.</summary>
    public const string NodeType = "BuildCompletion";

    /// <summary>The path segment build records are anchored under, inside the Admin partition.</summary>
    public const string SatelliteSegment = "_Build";

    /// <summary>
    /// The canonical node path for one repository's build record:
    /// <c>Admin/_Build/{owner}.{repo}</c>.
    ///
    /// <para>The <b>Admin</b> partition because a build record is platform-level infrastructure, not
    /// any one user's or space's data — the same reasoning that puts platform-admin grants there.
    /// Anchored under a real partition root rather than sitting top-level, per the satellite rule.</para>
    /// </summary>
    /// <param name="owner">The repository owner (GitHub org or user).</param>
    /// <param name="repo">The repository name.</param>
    public static string PathFor(string owner, string repo)
        => $"Admin/{SatelliteSegment}/{Sanitize(owner)}.{Sanitize(repo)}";

    /// <summary>Path separators would fabricate node hierarchy, so they never survive into a path
    /// segment. GitHub owner/repo names cannot contain <c>/</c>, but the value arrives from a
    /// webhook payload and is treated as untrusted.</summary>
    private static string Sanitize(string s) => s.Replace('/', '-').Replace('\\', '-');

    /// <summary>The repository this build belongs to, as <c>https://github.com/{owner}/{repo}</c>.
    /// Consumers match their own configured source against this.</summary>
    public string RepositoryUrl { get; init; } = "";

    /// <summary>The branch the workflow ran on (e.g. <c>main</c>). A consumer that only cares about
    /// the default branch filters on this — the webhook does not filter, because which branch
    /// matters is the consumer's decision.</summary>
    public string Branch { get; init; } = "";

    /// <summary>The commit the workflow ran against. This is the ref a consumer fetches content at,
    /// so it reads exactly the tree that was proven green.</summary>
    public string HeadSha { get; init; } = "";

    /// <summary>The workflow's display name (e.g. <c>MeshWeaver Build and Test</c>). A repo may run
    /// several workflows; a consumer that only trusts one filters on this.</summary>
    public string? WorkflowName { get; init; }

    /// <summary>GitHub's numeric run id — the stable handle for fetching logs or artifacts.</summary>
    public long RunId { get; init; }

    /// <summary>The human-facing incrementing run number shown in the Actions UI.</summary>
    public long RunNumber { get; init; }

    /// <summary>When the run completed (UTC), as reported by GitHub.</summary>
    public DateTimeOffset? CompletedAtUtc { get; init; }

    /// <summary>
    /// The run's conclusion. Only <c>success</c> is ever written — a failed or cancelled run may
    /// still have produced artifacts, and treating that as publishable is how a broken build reaches
    /// consumers. The field is persisted anyway so a reader never has to assume what it holds.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public string Conclusion { get; init; } = "success";
}
