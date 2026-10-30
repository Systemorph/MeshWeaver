// The standing build hosts' block on the operations record (Doc/Architecture/BuildHosts;
// MeshWeaver.Plugins Hosting/StandingBuildHosts, gap G7). Init-only properties only: a new positional
// constructor parameter would replace the record's signature and break every module compiled
// against the old one (the binary-break rule).
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.ComponentModel;

namespace MeshWeaver.Deployment;

/// <summary>
/// The standing build hosts this record runs — one warm host per release channel
/// (<see cref="LatestChannel"/>, <see cref="StableChannel"/>) that claims build runs from this
/// instance's queue and runs every stage in a sandbox. Null on <see cref="DeploymentContent.BuildHosts"/>,
/// or <see cref="Enabled"/> false, means the record runs none and nothing renders.
/// <para><b>Who writes what.</b> The record's author states <see cref="Enabled"/> and, per channel here,
/// <see cref="BuildHostChannel.MaxReplicas"/> — the set fields of an AUTHORED channel are not read. A
/// channel's SET — <see cref="BuildHostChannel.PlatformVersion"/>, <see cref="BuildHostChannel.TesterDigest"/>,
/// <see cref="BuildHostChannel.PortalDigest"/>, <see cref="BuildHostChannel.RunnerDigest"/> — lives in the
/// record's MESH-OWNED <see cref="DeploymentContent.BuildHostSets"/>, written by core's CD (the signed
/// <c>build-host-update-available</c> event, policy <c>build-hosts-follow-latest</c>) through the
/// operations instance, only while <see cref="Enabled"/> is already true and never backwards; GitSync
/// strips it on export and keeps it on import, so git never names a build and the record change applies
/// itself.</para>
/// </summary>
public sealed record BuildHostsBlock
{
    /// <summary>The channel every PR and trunk build runs on; the only one whose runs publish.</summary>
    public const string LatestChannel = "latest";

    /// <summary>The channel a gated client estate runs; a compatibility verdict, never a publication.</summary>
    public const string StableChannel = "stable";

    /// <summary>Whether this record runs standing build hosts at all. False → nothing renders and no
    /// event may change a channel.</summary>
    [Description("Run standing build hosts (one warm host per release channel)")]
    public bool Enabled { get; init; }

    /// <summary>The hosts, by channel name (<see cref="LatestChannel"/>, <see cref="StableChannel"/>).
    /// A channel absent here runs no host.</summary>
    [Description("The hosts by release channel (latest, stable)")]
    public ImmutableSortedDictionary<string, BuildHostChannel> Channels { get; init; }
        = ImmutableSortedDictionary<string, BuildHostChannel>.Empty.WithComparers(StringComparer.Ordinal);

    /// <summary>The channel named <paramref name="channel"/>, or null.</summary>
    public BuildHostChannel? Channel(string channel) =>
        Channels.TryGetValue(channel, out var c) ? c : null;

    /// <summary>The replica ceiling of every VERSIONED deployment (<see cref="BuildHostSetKeys.Version"/>);
    /// it must cover the core shards plus the dependents' gates a candidate runs (Doc/Architecture/DeployTestSwitchLatest §9).</summary>
    [Description("The most replicas each versioned build server scales to")]
    public int MaxReplicasPerVersion { get; init; } = BuildHostChannel.DefaultMaxReplicas;

    /// <summary>Which versioned deployments are kept (Doc/Architecture/DeployTestSwitchLatest §6).</summary>
    [Description("Which versioned build servers are kept")]
    public BuildHostRetention Retention { get; init; } = new();

    /// <summary>
    /// The client id of the hosts' ONE cloud credential: a user-assigned identity granted AcrPull only, with which
    /// the host pulls the sealed plugins publication (MeshWeaver.Plugins Hosting/BuildQueue §6). Rendered as the
    /// chart's <c>buildHosts.workloadIdentity.clientId</c>, which annotates the hosts' ServiceAccount. Null → the
    /// hosts carry no cloud credential, and a host offered a publication cannot pull it. Never the portal's identity:
    /// a host runs untrusted pull-request code.
    /// </summary>
    [Description("Client id of the hosts' pull-only workload identity (AcrPull on the registry, nothing else)")]
    public string? PullIdentityClientId { get; init; }
}

/// <summary>
/// The retention rule for versioned build servers (Doc/Architecture/DeployTestSwitchLatest §6): keep what
/// the aliases name (<c>latest</c>, the candidate under test, the PREVIOUS <c>latest</c> for an instant
/// rollback), every version a queued or running attempt recorded, and every version a package cap needs;
/// tear down the rest. Pure.
/// </summary>
public sealed record BuildHostRetention
{
    /// <summary>The most versioned deployments the record may hold at once; a deploy beyond it is refused, never a silent eviction.</summary>
    public const int DefaultMaxVersions = 6;

    /// <summary>Keep the deployment <c>latest</c> named before its last switch, so a rollback is an alias move, not a redeploy.</summary>
    [Description("Keep the previous latest for an instant rollback")]
    public bool KeepPreviousLatest { get; init; } = true;

    /// <summary>The ceiling on versioned deployments held at once.</summary>
    [Description("The most versioned build servers held at once")]
    public int MaxVersions { get; init; } = DefaultMaxVersions;

    /// <summary>
    /// The versions to KEEP: every alias target (<see cref="BuildHostSetKeys.PreviousAlias"/> only while
    /// <see cref="KeepPreviousLatest"/>), plus <paramref name="attemptVersions"/> (queued or running) and
    /// <paramref name="capVersions"/> (each capped package's highest admissible deployment). Pure.
    /// </summary>
    public ImmutableSortedSet<string> Keep(ImmutableSortedDictionary<string, string>? sets,
        IEnumerable<string> attemptVersions, IEnumerable<string> capVersions) =>
        (sets ?? ImmutableSortedDictionary<string, string>.Empty)
            .Where(e => BuildHostSetKeys.AliasName(e.Key) is { } alias && (KeepPreviousLatest || alias != BuildHostSetKeys.PreviousAlias))
            .Select(e => e.Value.Trim())
            .Concat(attemptVersions).Concat(capVersions)
            .Where(BuildHostSetKeys.IsVersion)
            .ToImmutableSortedSet(StringComparer.Ordinal);

    /// <summary>The deployed versions <see cref="Keep"/> does not hold — what a teardown may remove. Pure.</summary>
    public ImmutableSortedSet<string> Teardown(ImmutableSortedDictionary<string, string>? sets,
        IEnumerable<string> attemptVersions, IEnumerable<string> capVersions)
    {
        var keep = Keep(sets, attemptVersions, capVersions);
        return BuildHostSetKeys.Versions(sets).Keys.Where(v => !keep.Contains(v)).ToImmutableSortedSet(StringComparer.Ordinal);
    }
}

/// <summary>
/// The key forms of <see cref="DeploymentContent.BuildHostSets"/> (Doc/Architecture/DeployTestSwitchLatest §3):
/// <c>v/&lt;version&gt;</c> → one versioned build server's set token, and <c>alias/&lt;name&gt;</c> → the
/// VERSION an alias names (one scalar leaf, so a stale switch conflicts as a whole). A bare key is the
/// channel form the hosts used before versioned deployments, still read. Pure.
/// </summary>
public static class BuildHostSetKeys
{
    /// <summary>The prefix of a versioned entry.</summary>
    public const string VersionPrefix = "v/";

    /// <summary>The prefix of an alias entry.</summary>
    public const string AliasPrefix = "alias/";

    /// <summary>The alias every module build resolves.</summary>
    public const string LatestAlias = "latest";

    /// <summary>The alias of the candidate under test (at most one).</summary>
    public const string CandidateAlias = "candidate";

    /// <summary>The alias of the deployment <c>latest</c> named before its last switch — the rollback target.</summary>
    public const string PreviousAlias = "previous";

    /// <summary>The key of version <paramref name="version"/>'s entry.</summary>
    public static string Version(string version) => VersionPrefix + version.Trim();

    /// <summary>The key of alias <paramref name="name"/>.</summary>
    public static string Alias(string name) => AliasPrefix + name.Trim();

    /// <summary>The version a <c>v/</c> key names, or null for any other key.</summary>
    public static string? VersionOf(string key) =>
        key.StartsWith(VersionPrefix, StringComparison.Ordinal) && IsVersion(key[VersionPrefix.Length..]) ? key[VersionPrefix.Length..] : null;

    /// <summary>The alias an <c>alias/</c> key names, or null for any other key.</summary>
    public static string? AliasName(string key) =>
        key.StartsWith(AliasPrefix, StringComparison.Ordinal) && key.Length > AliasPrefix.Length ? key[AliasPrefix.Length..] : null;

    /// <summary>A platform version: <c>MAJOR.MINOR.PATCH</c> with an optional <c>-pre.release</c> of [0-9A-Za-z.-] that ENDS in a
    /// letter or digit (a trailing <c>.</c> or <c>-</c> would make a Kubernetes name or label that ends in punctuation), at most 48 characters.</summary>
    public static bool IsVersion(string? version)
    {
        if (string.IsNullOrWhiteSpace(version) || version.Length > 48 || version != version.Trim())
            return false;
        var dash = version.IndexOf('-');
        var core = dash < 0 ? version : version[..dash];
        var pre = dash < 0 ? null : version[(dash + 1)..];
        var parts = core.Split('.');
        if (parts.Length != 3 || parts.Any(p => p.Length == 0 || p.AsSpan().IndexOfAnyExcept("0123456789") >= 0))
            return false;
        return pre is null || (pre.Length > 0 && char.IsAsciiLetterOrDigit(pre[^1])
            && pre.AsSpan().IndexOfAnyExcept("0123456789abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ.-") < 0);
    }

    /// <summary>The Kubernetes name of version <paramref name="version"/>'s deployment: <c>build-</c> + the version
    /// lower-cased with dots as dashes (<c>3.1.10412</c> → <c>build-3-1-10412</c>), a valid DNS label. NOT injective —
    /// <c>3.1.1-alpha.1</c> and <c>3.1.1-alpha-1</c> share one name — so whatever adds a version refuses one whose name
    /// another deployed version already has (<see cref="NameCollision"/>).</summary>
    public static string DeploymentName(string version) => "build-" + version.Trim().ToLowerInvariant().Replace('.', '-');

    /// <summary>The deployed version whose <see cref="DeploymentName"/> equals <paramref name="version"/>'s, other than itself; null when none. Pure.</summary>
    public static string? NameCollision(ImmutableSortedDictionary<string, string>? sets, string version) =>
        Versions(sets).Keys.FirstOrDefault(v => v != version && DeploymentName(v) == DeploymentName(version));

    /// <summary>Every versioned entry, by version, whose token parses. Pure.</summary>
    public static ImmutableSortedDictionary<string, BuildHostChannel> Versions(ImmutableSortedDictionary<string, string>? sets) =>
        (sets ?? ImmutableSortedDictionary<string, string>.Empty)
            .Select(e => (Version: VersionOf(e.Key), Set: BuildHostChannel.FromSetToken(e.Value)))
            .Where(x => x.Version is not null && x.Set is not null)
            .ToImmutableSortedDictionary(x => x.Version!, x => x.Set!, StringComparer.Ordinal);

    /// <summary>The version alias <paramref name="name"/> names, or null. Pure.</summary>
    public static string? AliasTarget(ImmutableSortedDictionary<string, string>? sets, string name) =>
        sets is not null && sets.TryGetValue(Alias(name), out var v) && IsVersion(v.Trim()) ? v.Trim() : null;
}

/// <summary>
/// One channel's host: the immutable platform set it runs, by DIGEST (a run records the set that
/// compiled it, and a tag can name different bytes tomorrow), and its replica ceiling.
/// </summary>
public sealed record BuildHostChannel
{
    /// <summary>The ceiling when the record states none.</summary>
    public const int DefaultMaxReplicas = 2;

    /// <summary>The platform set the host runs (<c>3.x.y-ci.N</c> / <c>3.x.N</c>); stamped on every run it claims.</summary>
    [Description("The platform set the host runs")]
    public string? PlatformVersion { get; init; }

    /// <summary>The set's tester image digest (<c>sha256:…</c>).</summary>
    [Description("The set's tester image digest (sha256:…)")]
    public string? TesterDigest { get; init; }

    /// <summary>The set's portal image digest (<c>sha256:…</c>) — the reference set and the gate's runtime.</summary>
    [Description("The set's portal image digest (sha256:…)")]
    public string? PortalDigest { get; init; }

    /// <summary>The set's core TEST image digest (<c>mw-core-tests</c>, <c>sha256:…</c>) — core's test projects built
    /// from the set's commit in the same CD run; a candidate's core suites run from it (Doc/Architecture/DeployTestSwitchLatest §2).
    /// Optional: a set without it serves module builds, and only a candidate test needs it.</summary>
    [Browsable(false)]
    public string? CoreTestsDigest { get; init; }

    /// <summary>The set's build-host RUNNER image digest (<c>mw-build-host</c>, <c>sha256:…</c>) — bash,
    /// python3, git, bubblewrap, the .NET SDK and <c>build-run.py</c>, built by MeshWeaver.Deploy's <c>build-host.yml</c>
    /// against the set's own commit and tagged with the set's version, so the runner that drives a stage is the one
    /// that ships with its platform.</summary>
    [Description("The set's build-host runner image digest (sha256:…)")]
    public string? RunnerDigest { get; init; }

    /// <summary>The most replicas KEDA may scale this host to (minimum 0, from the queue's demand).</summary>
    [Description("The most replicas the host scales to")]
    public int MaxReplicas { get; init; } = DefaultMaxReplicas;

    /// <summary>
    /// The FLOOR KEDA keeps this host at, however empty the queue (the chart's <c>minReplicaCount</c>; default 0 — scale
    /// to zero). DECLARED, never derived: a floor of one keeps a host warm so an admitted run is claimed without a
    /// node and a pod cold start (Doc/Architecture/BuildHosts → "Declared host floor"). Never above
    /// <see cref="MaxReplicas"/>; the chart refuses that.
    /// </summary>
    [Description("The fewest replicas the host keeps, even with an empty queue")]
    public int MinReplicas { get; init; }

    /// <summary>True when the channel names a whole set — a version and the three digests in the
    /// <c>sha256:&lt;64 hex&gt;</c> form the chart requires. A channel that is not complete renders no host.</summary>
    public bool IsComplete =>
        !string.IsNullOrWhiteSpace(PlatformVersion) && IsDigest(TesterDigest) && IsDigest(PortalDigest) && IsDigest(RunnerDigest);

    /// <summary>
    /// The set as ONE token — <c>&lt;platformVersion&gt;;&lt;testerDigest&gt;;&lt;portalDigest&gt;;&lt;runnerDigest&gt;</c>, plus
    /// <c>;&lt;coreTestsDigest&gt;</c> only when the set names one (so a four-part token written before it still reads
    /// back identically), a missing part empty — the form <see cref="DeploymentContent.BuildHostSets"/> stores, so a
    /// write replaces a set whole or not at all. Pure.
    /// </summary>
    public string ToSetToken()
    {
        var four = string.Join(';', PlatformVersion?.Trim() ?? "", TesterDigest?.Trim() ?? "", PortalDigest?.Trim() ?? "", RunnerDigest?.Trim() ?? "");
        return string.IsNullOrWhiteSpace(CoreTestsDigest) ? four : four + ";" + CoreTestsDigest.Trim();
    }

    /// <summary>The set a <see cref="ToSetToken"/> token names, or null when it is not four or five parts. Pure.</summary>
    public static BuildHostChannel? FromSetToken(string? token)
    {
        var parts = (token ?? "").Split(';');
        if (parts.Length is not (4 or 5))
            return null;
        static string? Part(string p) => string.IsNullOrWhiteSpace(p) ? null : p.Trim();
        return new BuildHostChannel
        {
            PlatformVersion = Part(parts[0]), TesterDigest = Part(parts[1]), PortalDigest = Part(parts[2]), RunnerDigest = Part(parts[3]),
            CoreTestsDigest = parts.Length == 5 ? Part(parts[4]) : null,
        };
    }

    /// <summary><c>sha256:</c> followed by 64 lower-case hex characters.</summary>
    public static bool IsDigest(string? digest) =>
        digest is { Length: 71 } && digest.StartsWith("sha256:", StringComparison.Ordinal)
        && digest.AsSpan(7).IndexOfAnyExcept("0123456789abcdef") < 0;
}
