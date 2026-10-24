// The standing build hosts' block on the operations record (Doc/Architecture/BuildHosts;
// MeshWeaver.Plugins Hosting/StandingBuildHosts, gap G7). Init-only properties only: a new positional
// constructor parameter would replace the record's signature and break every module compiled
// against the old one (the binary-break rule).
using System.Collections.Immutable;
using System.ComponentModel;

namespace MeshWeaver.Deployment;

/// <summary>
/// The standing build hosts this record runs — one warm host per release channel
/// (<see cref="LatestChannel"/>, <see cref="StableChannel"/>) that claims build runs from this
/// instance's queue and runs every stage in a sandbox. Null on <see cref="DeploymentContent.BuildHosts"/>,
/// or <see cref="Enabled"/> false, means the record runs none and nothing renders.
/// <para><b>Who writes what.</b> The record's author states <see cref="Enabled"/> and each channel's
/// <see cref="BuildHostChannel.MaxReplicas"/>. A channel's SET — <see cref="BuildHostChannel.PlatformVersion"/>,
/// <see cref="BuildHostChannel.TesterDigest"/>, <see cref="BuildHostChannel.PortalDigest"/>,
/// <see cref="BuildHostChannel.RunnerDigest"/> — is written by
/// core's CD (the signed <c>build-host-update-available</c> event, policy
/// <c>build-hosts-follow-latest</c>) through the operations instance, only while <see cref="Enabled"/> is
/// already true and never backwards; the record change then applies itself.</para>
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

    /// <summary>The set's build-host RUNNER image digest (<c>mw-build-host</c>, <c>sha256:…</c>) — bash,
    /// python3, git, bubblewrap, the .NET SDK and <c>build-run.py</c>, built by core's CD in the same run
    /// as the set, so the runner that drives a stage is the one that ships with its platform.</summary>
    [Description("The set's build-host runner image digest (sha256:…)")]
    public string? RunnerDigest { get; init; }

    /// <summary>The most replicas KEDA may scale this host to (minimum 0, from the queue's demand).</summary>
    [Description("The most replicas the host scales to")]
    public int MaxReplicas { get; init; } = DefaultMaxReplicas;

    /// <summary>True when the channel names a whole set — a version and the three digests in the
    /// <c>sha256:&lt;64 hex&gt;</c> form the chart requires. A channel that is not complete renders no host.</summary>
    public bool IsComplete =>
        !string.IsNullOrWhiteSpace(PlatformVersion) && IsDigest(TesterDigest) && IsDigest(PortalDigest) && IsDigest(RunnerDigest);

    /// <summary><c>sha256:</c> followed by 64 lower-case hex characters.</summary>
    public static bool IsDigest(string? digest) =>
        digest is { Length: 71 } && digest.StartsWith("sha256:", StringComparison.Ordinal)
        && digest.AsSpan(7).IndexOfAnyExcept("0123456789abcdef") < 0;
}
