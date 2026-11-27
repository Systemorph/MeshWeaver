using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using MeshWeaver.Mesh;

namespace MeshWeaver.Graph.Configuration;

/// <summary>
/// 🚨 A write that does not touch a source's CODE is not a source change.
///
/// <para>The source-version snapshots (<see cref="NodeTypeDefinition.CurrentSourceVersions"/>,
/// <see cref="NodeTypeDefinition.CompiledSources"/>) are keyed on the owner's
/// <see cref="MeshNode.LastModified"/> (<see cref="NodeTypeDefinition.SourceVersionOf"/>), which every
/// write stamps — an AI description round, an icon, a rename. Each such write therefore moved the
/// snapshot, read as an edit and recompiled the type (measured on the public cloud instance,
/// 2026-11-25: a full compile 3-4 s after a description round, twice, identical diagnostics), and for a
/// failing type re-armed its failure re-drive.</para>
///
/// <para><b>The rule.</b> The emitted bytes depend on the source's code text and on nothing else
/// (<c>NodeTypeSourceFingerprint</c>), so each snapshot generation records beside its versions a
/// digest of every source's code (<see cref="NodeTypeDefinition.CurrentSourceDigests"/>). A source
/// whose digest equals the one recorded for the previous generation KEEPS that generation's version:
/// the snapshot does not move, so <see cref="NodeTypeDefinition.IsDirty"/>, the inputs token and the
/// parked-type retry all stay still. A source whose code changed (or whose digest is unknown, or that
/// is new) takes its own version exactly as before — an unknown digest is never a reason to skip a
/// compile. The version stays an ordered timestamp, so the freshness checks that rely on order
/// (<c>SourceSetFreshness</c>, the release request's snapshot wait) keep working.</para>
/// </summary>
internal static class SourceVersionCarry
{
    /// <summary>The digest of a source's compile-relevant content, or null when the node is not a
    /// typed code node (then nothing is carried).</summary>
    internal static string? DigestOf(MeshNode node) =>
        node.Content is CodeConfiguration cf
            ? Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(cf.Code ?? string.Empty)))
            : null;

    /// <summary>
    /// Folds <paramref name="sources"/> into the version snapshot and its digests, carrying the
    /// previous generation's version for every source whose code is unchanged. Pure.
    /// </summary>
    /// <param name="sources">The live source set.</param>
    /// <param name="priorVersions">The previous generation's versions (null = none).</param>
    /// <param name="priorDigests">The previous generation's digests (null = none).</param>
    internal static (ImmutableDictionary<string, long> Versions, ImmutableDictionary<string, string> Digests) Fold(
        IEnumerable<MeshNode> sources,
        IReadOnlyDictionary<string, long>? priorVersions,
        IReadOnlyDictionary<string, string>? priorDigests)
    {
        var versions = ImmutableDictionary<string, long>.Empty;
        var digests = ImmutableDictionary<string, string>.Empty;
        foreach (var n in sources)
        {
            if (string.IsNullOrEmpty(n.Path))
                continue;
            var digest = DigestOf(n);
            var version = NodeTypeDefinition.SourceVersionOf(n);
            if (digest is not null)
            {
                digests = digests.SetItem(n.Path, digest);
                if (priorDigests is not null && priorVersions is not null
                    && priorDigests.TryGetValue(n.Path, out var before)
                    && string.Equals(before, digest, StringComparison.Ordinal)
                    && priorVersions.TryGetValue(n.Path, out var carried))
                    version = carried;
            }
            versions = versions.SetItem(n.Path, version);
        }
        return (versions, digests);
    }

    /// <summary>True when <paramref name="digests"/> shows the owner's code for <paramref name="path"/>
    /// unchanged, so a newer owner timestamp on it is metadata only (the snapshot wait's tolerance).</summary>
    internal static bool ShowsSameCode(
        IReadOnlyDictionary<string, string>? digests, string path, MeshNode? owner) =>
        owner is not null
        && digests is not null
        && DigestOf(owner) is { } live
        && digests.TryGetValue(path, out var held)
        && string.Equals(held, live, StringComparison.Ordinal);
}
