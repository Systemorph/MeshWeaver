using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using MeshWeaver.Messaging;

namespace MeshWeaver.Mesh;

/// <summary>
/// One published package, recorded as a durable fact rather than inferred from a notification.
///
/// <para>🚨 This exists because the release BROADCAST is deliberately reporter-class:
/// <c>FrameworkReleaseBroadcaster</c>'s contract is that "a lost dispatch costs at most one delayed
/// rebake wave — never a hard failure". That property is only safe while nobody accumulates state
/// from the dispatches. A consumer that built its dependency set by adding up events would be
/// permanently wrong after ONE lost dispatch, and nothing would report it — the failure would
/// present as "that repo just never rebuilt".</para>
///
/// <para>So the event is a WAKE-UP and this node is the TRUTH: on wake a consumer QUERIES the
/// current release facts rather than remembering what it was told. A lost event then costs latency,
/// never correctness — the same rule as the delivery verdict refusing to pass on an empty verdict,
/// and as CD's reconciler asking the registry whether a commit's image set is complete rather than
/// trusting that a job ran.</para>
///
/// <para>One fact per PACKAGE, never one per repo: a repo that ships several packages publishes
/// several, and a platform rebuild publishes the platform plus every package rebuilt against it.</para>
/// </summary>
public record Release
{
    /// <summary>Stable identity of this fact: <c>{Repository}/{PackageId}/{Version}</c>.</summary>
    [Browsable(false)]
    [Key]
    public string Id { get; init; } = null!;

    /// <summary>The repository that published it, as <c>owner/name</c>.</summary>
    [Description("The repository that published this package.")]
    [Translation("de", "Das Repository, das dieses Paket veröffentlicht hat.")]
    public string Repository { get; init; } = null!;

    /// <summary>The package identifier, e.g. <c>MeshWeaver.Blazor.EntityViews</c>.</summary>
    [Description("The published package identifier.")]
    [Translation("de", "Die Kennung des veröffentlichten Pakets.")]
    public string PackageId { get; init; } = null!;

    /// <summary>The published version.</summary>
    [Description("The published version.")]
    [Translation("de", "Die veröffentlichte Version.")]
    public string Version { get; init; } = null!;

    /// <summary>
    /// The framework identity these bytes were built against.
    ///
    /// <para>Not decoration: bundles are ADOPTED, not rebuilt, and a consumer can only adopt bytes
    /// built against a framework identity it can resolve. This is the same equality
    /// <c>BakeEquivalenceTest</c> pins between the bake and the gate, carried on the fact so a
    /// consumer can check it without re-deriving it.</para>
    /// </summary>
    [Description("The framework identity these bytes were built against.")]
    [Translation("de", "Die Framework-Identität, gegen die diese Bytes gebaut wurden.")]
    public string? Platform { get; init; }

    /// <summary>The commit the package was built from.</summary>
    [Description("The commit this package was built from.")]
    [Translation("de", "Der Commit, aus dem dieses Paket gebaut wurde.")]
    public string? Commit { get; init; }

    /// <summary>When the publication was OBSERVED — never when a job started.</summary>
    [Description("When the publication was observed.")]
    [Translation("de", "Wann die Veröffentlichung beobachtet wurde.")]
    public DateTime Released { get; init; }

    /// <summary>
    /// The stable id for a package release. Deliberately derived rather than random: the same
    /// publication observed twice (a redelivered webhook, a reconciler sweep) must land on the SAME
    /// node instead of minting a duplicate fact. Idempotent ingest is what lets the wake-up be
    /// unreliable without the truth becoming unreliable too.
    /// </summary>
    public static string IdFor(string repository, string packageId, string version) =>
        // 🚨 Repository and package id are NORMALIZED; version is not.
        //
        // GitHub repositories are case-insensitive — RepoIdentity.Matches compares Owner and Repo
        // with OrdinalIgnoreCase — and so are NuGet package ids. Composing the key from raw strings
        // would let "Systemorph/MeshWeaver" and "systemorph/meshweaver" mint TWO facts for ONE
        // publication, which is precisely the duplication this derived id exists to prevent: the
        // whole reason ingest can tolerate an unreliable wake-up is that observing the same
        // publication twice lands on the same node.
        //
        // The VERSION is deliberately left alone. SemVer states that pre-release identifiers are
        // case-sensitive, so "1.0.0-RC1" and "1.0.0-rc1" are different versions and folding them
        // would merge two genuinely distinct releases — the opposite failure, and the worse one.
        $"{repository.ToLowerInvariant()}/{packageId.ToLowerInvariant()}/{version}";
}
