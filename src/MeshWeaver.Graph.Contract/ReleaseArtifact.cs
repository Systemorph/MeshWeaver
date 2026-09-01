using System.Runtime.InteropServices;

namespace MeshWeaver.Graph.Configuration;

/// <summary>
/// 🚨 <b>The LINK (#1751): where one release's compiled assemblies live, for one framework identity
/// and one architecture.</b>
///
/// <para>The settled split is: <b>compilation</b> is managed by the assembly (DLL + PDB keyed by node
/// path, carrying the framework identity and the per-type dependency record), <b>node definitions</b>
/// stay in the mesh synced from their repo, and <b>the link between them</b> — which release, which
/// identity, where its assemblies are — is a property of the <c>Release</c> node. This record is that
/// property. Nothing is added to the bundle format; the artifact record is what a consumer reads to
/// decide whether it can adopt, and from where.</para>
///
/// <para><b>Why the architecture is a first-class field even though the identity already folds it
/// in.</b> The same four reference assemblies differ between the amd64 and arm64 variants of ONE
/// image, so a multi-arch image resolves TWO framework identities (<c>main-cd.yml</c>'s
/// <c>publish-bake</c> job says so out loud, and pins the bake to <c>--platform linux/amd64</c>).
/// The identity is therefore necessary and sufficient as the compatibility PROOF — but it is opaque:
/// given <c>s1a2b3c…</c> nobody can tell which architecture produced it. An arm64 install that
/// resolves the other identity currently finds nothing and there is no way to see WHY. Recording the
/// architecture beside the identity turns that silent nothing into a statement a human and a log line
/// can both read: <i>this release has <c>linux-x64</c> under <c>s1a2…</c>; you are <c>linux-arm64</c>
/// under <c>s9f8…</c>; there is no artifact for you.</i> That is #1728's "just give path to correct
/// arch", expressed where the resolution belongs.</para>
///
/// <para>🚨 <b>The architecture NEVER relaxes the identity gate.</b> It narrows, never widens: an
/// artifact is a candidate only when its identity matches EXACTLY
/// (<c>DeclineReason</c>) and its architecture matches
/// too. Recording several artifacts on one release is how a lane that genuinely bakes twice publishes
/// both honestly — it is NOT a licence to re-publish one bake's bytes under a second identity, which
/// the CD lane forbids and which would defeat the whole proof.</para>
/// </summary>
/// <param name="FrameworkIdentity">The resolved framework build identity
/// (<c>s&lt;hash&gt;</c>/<c>g&lt;sha&gt;</c>) the producing process recorded beside these exact bytes —
/// the same value <c>LiveFrameworkMvid</c> reads and
/// <c>DeclineReason</c> compares. 🚨 NOT
/// <see cref="NodeTypeRelease.FrameworkVersion"/>, which is an assembly version string
/// (<c>3.0.0.0</c>) and has never had anything to do with adoption.</param>
/// <param name="Architecture">The portable RID of the producing process
/// (<c>linux-x64</c>, <c>linux-arm64</c>, <c>osx-arm64</c>, …), or
/// <see cref="ReleaseArchitecture.Any"/> when the producer knows the payload is architecture-neutral
/// and says so deliberately.</param>
/// <param name="AssemblyStoreVersion">The <see cref="MeshWeaver.Mesh.MeshNode.Version"/> these bytes
/// were written under in <c>IAssemblyStore</c> — the key
/// <c>TryGetAssemblyPath(nodeTypePath, version)</c> needs. The display
/// <see cref="NodeTypeRelease.Version"/> is a timestamp/hash string and does not round-trip to it.</param>
/// <param name="Collection">Content-collection name holding the bytes (the cross-silo durable
/// reference), or null when the producer had no store.</param>
/// <param name="ContentPath">Path inside <paramref name="Collection"/>.</param>
/// <param name="Url">An absolute, routable address these bytes can be fetched from — the registry
/// route (<c>https://memex.meshweaver.cloud/api/plugins/bundles/…</c>) when the producer knew a
/// reachable host. Optional: a producing silo generally does NOT know the public host it is served
/// under (an ingress, a port-forward and a custom domain all differ), which is why the bundle index
/// builds URLs from the REQUEST. Null therefore means "fetch it the way you reached this node",
/// never "unavailable".</param>
public sealed record ReleaseArtifact(
    string FrameworkIdentity,
    string Architecture,
    long? AssemblyStoreVersion = null,
    string? Collection = null,
    string? ContentPath = null,
    string? Url = null);

/// <summary>
/// The architecture vocabulary a <see cref="ReleaseArtifact"/> is keyed by: portable RIDs of the
/// shape <c>{os}-{arch}</c>, matching the suffix <c>VersionSelect</c> already recognises on per-RID
/// image tags (<c>-(linux|win|osx)-(x64|x86|arm|arm64)</c>) so the platform has ONE spelling.
/// </summary>
public static class ReleaseArchitecture
{
    /// <summary>
    /// The producer asserts these bytes run anywhere the framework identity does — pure IL with no
    /// architecture-specific payload. 🚨 Only a producer may claim this; a CONSUMER never widens its
    /// own architecture to <see cref="Any"/>, because "I do not know" is not "it does not matter".
    /// </summary>
    public const string Any = "any";

    /// <summary>
    /// This process's portable RID. Computed once — it cannot change within a process — and never
    /// from <c>RuntimeInformation.RuntimeIdentifier</c>, whose value carries a distro qualifier on
    /// some hosts (<c>debian.12-x64</c>, <c>linux-musl-x64</c>) and therefore does not compare
    /// across the two ends of a distribution link.
    /// </summary>
    public static string Live { get; } = Describe(
        RuntimeInformation.ProcessArchitecture,
        RuntimeInformation.IsOSPlatform(OSPlatform.Linux) ? "linux"
        : RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "win"
        : RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? "osx"
        : "unknown");

    /// <summary>
    /// Whether an artifact recorded for <paramref name="artifactArchitecture"/> may be used by a
    /// consumer running <paramref name="consumerArchitecture"/>.
    ///
    /// <para>🚨 EXACT, case-insensitively, with exactly one widening: an artifact the producer
    /// declared <see cref="Any"/> matches every consumer. There is deliberately no "close enough"
    /// (x64 for x86, an OS-less match, a prefix match) — a nearest-match rule is how bytes get
    /// adopted from a lane that was never proven compatible, and declining is always safe because
    /// the caller's fallback is to compile.</para>
    /// </summary>
    public static bool Matches(string? artifactArchitecture, string? consumerArchitecture) =>
        !string.IsNullOrWhiteSpace(artifactArchitecture)
        && !string.IsNullOrWhiteSpace(consumerArchitecture)
        && (string.Equals(artifactArchitecture, Any, StringComparison.OrdinalIgnoreCase)
            || string.Equals(artifactArchitecture, consumerArchitecture,
                StringComparison.OrdinalIgnoreCase));

    private static string Describe(Architecture architecture, string os) => architecture switch
    {
        System.Runtime.InteropServices.Architecture.X64 => $"{os}-x64",
        System.Runtime.InteropServices.Architecture.X86 => $"{os}-x86",
        System.Runtime.InteropServices.Architecture.Arm64 => $"{os}-arm64",
        System.Runtime.InteropServices.Architecture.Arm => $"{os}-arm",
        _ => $"{os}-{architecture.ToString().ToLowerInvariant()}",
    };
}

/// <summary>
/// 🚨 <b>THE resolution rule for #1751</b>, stated once as a pure function so the producing side (the
/// registry assembling a bundle) and any consuming side reach the identical verdict without two
/// copies of the reasoning — the same shape, and for the same reason, as
/// <c>ReleaseAvailability</c>/<c>PrebuiltAssemblySeeder.DeclineReason</c>.
///
/// <para>Given a NodeType's releases and a consumer's <i>(framework identity, architecture)</i>, it
/// answers with the ONE artifact that was proven built for that pair — or with a sentence saying what
/// WAS on offer instead. It never guesses, never falls back to "the latest release", and never
/// returns a near miss: an artifact that cannot be shown compatible is worth exactly as much as no
/// artifact at all, and the caller's fallback (compile) always works.</para>
/// </summary>
public static class ReleaseArtifactResolver
{
    /// <summary>
    /// Resolves the artifact <paramref name="frameworkIdentity"/> + <paramref name="architecture"/>
    /// may use, across every release of one NodeType.
    ///
    /// <para>Later releases win: the candidates are ordered by <see cref="NodeTypeRelease.CreatedAt"/>
    /// descending, so a re-bake supersedes its predecessor without anything having to delete the old
    /// record (old releases stay on purpose — a live ALC may still hold the previous DLL).</para>
    ///
    /// <para>Pure and total: it does no IO, never throws, and a null/empty input is answered with a
    /// decline rather than an exception.</para>
    /// </summary>
    /// <param name="releases">Every release of the NodeType — typically the <c>Release/</c> children
    /// of its node. Failed releases and releases without artifacts are simply not candidates.</param>
    /// <param name="frameworkIdentity">The CONSUMER's live framework build identity.</param>
    /// <param name="architecture">The CONSUMER's portable RID
    /// (<see cref="ReleaseArchitecture.Live"/>).</param>
    public static ReleaseArtifactMatch Resolve(
        IEnumerable<NodeTypeRelease>? releases,
        string? frameworkIdentity,
        string? architecture)
    {
        if (string.IsNullOrWhiteSpace(frameworkIdentity))
            return ReleaseArtifactMatch.Declined(
                "the consumer did not state a framework identity, so no artifact can be shown "
                + "ABI-compatible with it");
        if (string.IsNullOrWhiteSpace(architecture))
            return ReleaseArtifactMatch.Declined(
                $"the consumer did not state an architecture, so no artifact can be shown runnable "
                + $"on it (framework identity {frameworkIdentity})");

        var candidates = (releases ?? [])
            .Where(r => r is not null)
            .OrderByDescending(r => r.CreatedAt)
            .ToArray();

        var offered = new List<string>();
        foreach (var release in candidates)
        {
            foreach (var artifact in release.Artifacts ?? [])
            {
                if (string.Equals(artifact.FrameworkIdentity, frameworkIdentity, StringComparison.Ordinal)
                    && ReleaseArchitecture.Matches(artifact.Architecture, architecture))
                    return new ReleaseArtifactMatch(artifact, release, null);

                offered.Add($"{artifact.FrameworkIdentity}/{artifact.Architecture}");
            }
        }

        // The two misses are DIFFERENT facts and are reported differently on purpose: "this release
        // predates the link" is a producer to upgrade, "this release is for another lane" is a bake
        // to add. Collapsing them into one "not found" is how #1728 stayed invisible.
        return ReleaseArtifactMatch.Declined(offered.Count == 0
            ? $"no release records an artifact link at all ({candidates.Length} release(s) examined) "
              + $"— nothing can be resolved for framework {frameworkIdentity} on {architecture}"
            // 🚨 "the N releases examined offer", not "this release offers": the set is aggregated
            // across every candidate, and a reason that named a single release would send the reader
            // to the wrong node. This sentence is the whole diagnostic — it is what a bundle miss
            // and both ends' logs carry — so it has to describe exactly what was looked at.
            : $"no artifact for framework {frameworkIdentity} on {architecture}; the "
              + $"{candidates.Length} release(s) examined offer only "
              + $"{string.Join(", ", offered.Distinct(StringComparer.Ordinal))}");
    }
}

/// <summary>One resolution: the artifact a consumer may use, or why it may not use any.</summary>
/// <param name="Artifact">The resolved artifact, or null when declined.</param>
/// <param name="Release">The release the artifact belongs to, or null when declined.</param>
/// <param name="DeclineReason">Why nothing resolved, in one sentence naming what WAS on offer — null
/// only on a hit. 🚨 A decline is a normal outcome (the caller compiles), but it must never be
/// silent: a miss that nobody can see is a miss nobody can count, and the adoption metric is the only
/// evidence the whole lane works.</param>
public sealed record ReleaseArtifactMatch(
    ReleaseArtifact? Artifact, NodeTypeRelease? Release, string? DeclineReason)
{
    /// <summary>Whether an artifact resolved.</summary>
    public bool IsResolved => Artifact is not null;

    /// <summary>A declined resolution carrying its reason.</summary>
    public static ReleaseArtifactMatch Declined(string reason) => new(null, null, reason);
}
