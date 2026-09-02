using System;
using System.Collections.Immutable;

namespace MeshWeaver.Graph.Configuration;

/// <summary>Where a CI module build stands — the ledger states of <see cref="ModuleBuildRecord"/>.</summary>
public enum ModuleBuildStatus
{
    /// <summary>A run holds this key and is building. A fresh heartbeat makes every other run WAIT.</summary>
    Claimed,
    /// <summary>The bundle exists as a run artifact (built + packed + inspected); tests not yet run.</summary>
    Built,
    /// <summary>The bundle exists and its module's test suite passed.</summary>
    Tested,
    /// <summary>The bundle was handed to the registry.</summary>
    Published,
    /// <summary>The build failed; <see cref="ModuleBuildRecord.Phase"/> and <see cref="ModuleBuildRecord.Blocking"/> say what that means for the next run.</summary>
    Failed,
}

/// <summary>Which CI run holds or held a <see cref="ModuleBuildRecord"/>.</summary>
/// <param name="Repo">GitHub repository of the run (<c>Systemorph/MeshWeaver.Plugins</c>).</param>
/// <param name="RunId">The run id.</param>
/// <param name="Attempt">The run attempt.</param>
/// <param name="Url">The run's URL, for a human reading the record.</param>
/// <param name="Event">The triggering event (<c>pull_request</c>, <c>push</c>, <c>schedule</c>, …).</param>
/// <param name="Lane">The lane key of the reusable-workflow call inside the run (a repo may call the lane twice per run).</param>
public sealed record ModuleBuildRun(
    string Repo, string RunId, string Attempt, string Url, string Event, string? Lane = null);

/// <summary>Where the bundle a record attests can be fetched from — a run artifact, never a branch-scoped cache.</summary>
/// <param name="Repo">The repository whose run uploaded it.</param>
/// <param name="RunId">The run id.</param>
/// <param name="Name">The artifact name (<c>module-bundle-&lt;module&gt;</c>).</param>
/// <param name="ExpiresAt">When GitHub deletes it (the upload's retention).</param>
public sealed record ModuleBuildArtifact(string Repo, string RunId, string Name, DateTime? ExpiresAt);

/// <summary>The test verdict a <see cref="ModuleBuildRecord"/> carries.</summary>
/// <param name="Passed">Passed test count.</param>
/// <param name="Failed">Failed test count.</param>
/// <param name="Names">The FAILED tests' fully qualified names (capped by the writer) — the evidence a follower is handed.</param>
public sealed record ModuleBuildTests(int Passed, int Failed, ImmutableList<string>? Names = null);

/// <summary>
/// Content of a <c>ModuleBuild</c> node — one row of the fleet's CI MODULE BUILD LEDGER at
/// <c>Admin/ModuleBuilds/{Key}</c> (<c>Doc/Architecture/ModuleBuildArchitecture</c> →
/// "Content-addressed outputs").
///
/// <para>The <see cref="Key"/> is the content address of one module build: sha256 over the package
/// id, the package's <c>manifest.lock</c> <c>moduleVersion</c>, the in-repo project closure the lane
/// compiles and tests (tree hashes) plus the <c>moduleVersion</c> of every package that closure or the
/// <c>requires</c> chain reaches, the tester-image and platform-image digests, the platform ref, and
/// the lane's build-recipe version (<c>.github/scripts/module-build-key.py</c>). Same key ⇒ same
/// inputs ⇒ same bytes and the same verdict, so a second run of the same key REUSES the first run's
/// record instead of building again.</para>
///
/// <para><b>The claim IS the mutex.</b> A run claims a key by CREATING this node; creation fails on an
/// existing path, so exactly one run holds a key and every later one reads the holder's record —
/// "already exists" is the follower's success case. A holder proves it is alive with
/// <see cref="HeartbeatAt"/>; a claim whose heartbeat is older than the fleet's job cap (45 min) is
/// dead by construction and may be taken over. Writers are the CI lane's ledger script, through the
/// registry portal's MCP endpoint, as a dedicated CI user holding a partition-admin grant on
/// <c>Admin/ModuleBuilds</c> — never a global admin.</para>
/// </summary>
public sealed record ModuleBuildRecord
{
    /// <summary>The build key — the node id. 64 hex characters.</summary>
    public string Key { get; init; } = "";

    /// <summary>The Store package id (<c>AI</c>, <c>Import</c>, …).</summary>
    public string Package { get; init; } = "";

    /// <summary>The module's entry assembly name (<c>MeshWeaver.AI</c>).</summary>
    public string Module { get; init; } = "";

    /// <summary>The package's <c>manifest.lock</c> <c>moduleVersion</c> — the content hash the key folds in.</summary>
    public string ModuleVersion { get; init; } = "";

    /// <summary>The package's derived SemVer at build time (<c>manifest.lock</c> <c>version</c>).</summary>
    public string? Version { get; init; }

    /// <summary>The Systemorph/MeshWeaver ref the module was built and tested against, exactly as the lane received it in
    /// <c>platform-ref</c> — a commit sha when the caller resolved its pin first (every satellite does), a branch or tag
    /// name otherwise. Folded into the key verbatim, so two spellings of one commit are two keys.</summary>
    public string PlatformRef { get; init; } = "";

    /// <summary>The digest of the tester image that compiled it.</summary>
    public string? TesterDigest { get; init; }

    /// <summary>The digest of the platform (portal) image it compiled against.</summary>
    public string? PlatformDigest { get; init; }

    /// <summary>
    /// The framework identity the PLATFORM host resolves for that image's <c>/app</c> — the
    /// <c>s…</c> surface identity <c>FrameworkBuildIdentity</c> computes, as the tester's
    /// <c>framework-identity</c> verb reads it off the extracted portal <c>/app</c>. Not the tester
    /// image's own identity: the tester compiles, the portal adopts, and the bundle is keyed to the
    /// host that adopts it (#3130). Written when the lane has resolved it (the pack job), null on a
    /// bare claim.
    /// </summary>
    public string? PlatformIdentity { get; init; }

    /// <summary>The ledger state.</summary>
    public ModuleBuildStatus Status { get; init; }

    /// <summary>
    /// The phase a <see cref="ModuleBuildStatus.Failed"/> record failed in: <c>compile</c>,
    /// <c>pack</c>, <c>test</c>, <c>publish</c>, or <c>workspace</c> (the one-workspace build
    /// aborted on ANOTHER module's error before this one was compiled).
    /// </summary>
    public string? Phase { get; init; }

    /// <summary>
    /// Whether this record blocks a later run of the same key. Same inputs give the same compile
    /// result, so a <c>compile</c> failure blocks; a <c>test</c> failure blocks only from the second
    /// failed attempt on (one re-claim is allowed, so a flaky suite does not pin the fleet); every
    /// other failure and every cancelled build is reclaimable.
    /// </summary>
    public bool Blocking { get; init; }

    /// <summary>How many runs have claimed this key (the first claim is 1).</summary>
    public int Attempts { get; init; }

    /// <summary>The run holding (or that last held) this key.</summary>
    public ModuleBuildRun? Run { get; init; }

    /// <summary>When the current holder claimed the key.</summary>
    public DateTime? ClaimedAt { get; init; }

    /// <summary>The holder's last sign of life; a claim older than the job cap is reclaimable.</summary>
    public DateTime? HeartbeatAt { get; init; }

    /// <summary>When the holder reached a terminal status.</summary>
    public DateTime? FinishedAt { get; init; }

    /// <summary>sha256 of the packed bundle (<c>.module.nupkg</c>).</summary>
    public string? BundleSha256 { get; init; }

    /// <summary>Where the bundle can be downloaded from for reuse.</summary>
    public ModuleBuildArtifact? BundleArtifact { get; init; }

    /// <summary>The test verdict, when the suite ran.</summary>
    public ModuleBuildTests? Tests { get; init; }

    /// <summary>The failure text (log tail), when <see cref="Status"/> is <see cref="ModuleBuildStatus.Failed"/>.</summary>
    public string? Failure { get; init; }

    /// <summary>The previous holder's outcome, kept when a key is re-claimed (evidence for the reader, not authority).</summary>
    public ModuleBuildRecord? Previous { get; init; }
}
