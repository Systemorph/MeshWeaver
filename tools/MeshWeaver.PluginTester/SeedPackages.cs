using System.Collections.Immutable;
using System.Text;
using MeshWeaver.GitSync;
using MeshWeaver.Plugin.Packaging;

namespace MeshWeaver.PluginTester;

/// <summary>
/// Materializes the UPSTREAM packages a bake seed carries into the gate's repo snapshot, so the
/// gate INSTALLS its dependencies instead of expecting the repo to carry their source.
///
/// <para>🚨 <b>Why stamping assemblies alone was not an install.</b> A seed's bytes "only stamp
/// nodes that already exist" (<see cref="BundleReader.Manifest.Content"/>): without the upstream's
/// NODE DEFINITIONS in the mesh, its NodeTypes never register, and every satellite module typed by
/// them dies at install. Measured 2026-08-27 on the first run that ever reached this point
/// (Reinsurance run 33092019158): <c>Install of 'RiskTransfer' failed: NodeType(s) not registered:
/// Edu/Lesson, Edu/Exercise, Edu/Page, Edu/CourseInvite, Edu/Quiz</c> — the Edu bundle sat in the
/// seed with its assemblies adopted and its nodes nowhere. MeshWeaver#2478 put the definitions in
/// the bundle precisely so a consumer can reconstruct the package; this is that consumer.</para>
///
/// <para>The discriminator between "mine" and "upstream" is the repo itself: a bundle whose package
/// id already has a top-level folder in the snapshot is the repo's OWN module (its bundle sits in
/// the same seed directory because the gate seeds its own bake too) and is left alone — the gate
/// judges the repo's tree, never a bundle's copy of it. Everything else came from an upstream
/// publication and is materialized for INSTALL: its types register from the seed's node
/// definitions, its assemblies adopt from the seed's bytes (no recompile), and
/// <see cref="PluginGateRunner"/> excludes it from gating and from
/// <see cref="BakeOutput"/> persistence — consumed, never re-emitted and never judged here.</para>
/// </summary>
public static class SeedPackages
{
    /// <summary>The merged snapshot plus which package ids arrived from the seed.</summary>
    public sealed record Materialized(RepoSnapshot Snapshot, ImmutableHashSet<string> UpstreamIds);

    /// <summary>
    /// Merges the seed's upstream package trees into <paramref name="repo"/>. Pure over its
    /// inputs apart from reading the seed's bundle files; a null seed returns the snapshot
    /// unchanged with an empty upstream set.
    /// </summary>
    public static Materialized Materialize(RepoSnapshot repo, BakeSeed? seed, TextWriter output)
    {
        var none = ImmutableHashSet<string>.Empty.WithComparer(StringComparer.Ordinal);
        if (seed is null)
            return new Materialized(repo, none);

        var present = repo.Files
            .Select(f => f.Path.IndexOf('/') is > 0 and var i ? f.Path[..i] : null)
            .Where(id => id is not null)
            .Select(id => id!)
            .ToImmutableHashSet(StringComparer.Ordinal);

        var added = new List<RepoFile>();
        var upstream = ImmutableHashSet.CreateBuilder<string>(StringComparer.Ordinal);
        foreach (var bundle in seed.Bundles)
        {
            BundleReader.Manifest? manifest;
            IReadOnlyList<BundleReader.ContentFile> files;
            try
            {
                (manifest, files) = BundleReader.ReadContent(File.ReadAllBytes(bundle));
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException)
            {
                // BakeSeed.Read already proved every bundle's manifest readable; a content read
                // failing here is a torn file, and installing half a package would be worse than
                // naming the miss. The install of whatever depends on it then fails LOUDLY.
                output.WriteLine(
                    $"::warning::{Path.GetFileName(bundle)}: content unreadable "
                    + $"({ex.GetType().Name}: {ex.Message}) — its package will not be installed");
                continue;
            }
            var id = manifest?.Plugin;
            if (string.IsNullOrWhiteSpace(id))
                continue;
            if (present.Contains(id))
                continue; // the repo's own module — under gate from its tree, never from a bundle
            if (files.Count == 0)
            {
                output.WriteLine(
                    $"::warning::{Path.GetFileName(bundle)} carries no node definitions "
                    + "(assemblies-only bundle, pre MeshWeaver#2478) — "
                    + $"'{id}' cannot be installed from it");
                continue;
            }
            foreach (var file in files)
                added.Add(Decode($"{id}/{file.RelativePath}", file.Bytes));
            upstream.Add(id);
        }

        var ids = upstream.ToImmutable();
        if (ids.Count == 0)
            return new Materialized(repo, ids);

        output.WriteLine(
            $"seed: materialized {ids.Count} upstream package(s) to INSTALL (never rebuild): "
            + string.Join(", ", ids.OrderBy(x => x, StringComparer.Ordinal)));
        var merged = repo with
        {
            Files = repo.Files
                .Concat(added)
                .OrderBy(f => f.Path, StringComparer.Ordinal)
                .ToImmutableList(),
        };
        return new Materialized(merged, ids);
    }

    // The SAME strict UTF-8 classification LocalNodeRepo applies to the repo's own files — which
    // files are text must not fork between the two ways a package reaches the gate.
    private static RepoFile Decode(string path, byte[] bytes) =>
        LocalNodeRepo.TryDecodeUtf8(bytes, out var text)
            ? new RepoFile(path, text)
            : new RepoFile(path, string.Empty, bytes);
}
