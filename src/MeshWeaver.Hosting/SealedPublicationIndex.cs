using Microsoft.Extensions.Logging;

namespace MeshWeaver.Hosting;

/// <summary>
/// One source's publication under ONE framework identity, as the registry sealed it for the
/// instance running that identity: which producing repository it came from (when the lane
/// recorded it), the commit it was baked from, and whether it is sealed at all.
/// </summary>
/// <param name="Source">The bake-source segment (<c>plugins</c>, <c>education</c>, …).</param>
/// <param name="Repository">The producing repository as <c>owner/name</c>, from
/// <see cref="SealedPublicationIndex.RepositoryMarkerFileName"/>; null when the seal predates that
/// marker.</param>
/// <param name="SourceCommit">The producing repository's commit the publication was baked from,
/// from <see cref="SealedPublicationIndex.SourceCommitMarkerFileName"/>; null when absent or
/// recorded as unknown.</param>
/// <param name="IsSealed">True when the completion sentinel is present and every bundle it lists
/// is on disk — the same rule the boot seeder judges by.</param>
/// <param name="Refusal">Why <paramref name="IsSealed"/> is false, or null.</param>
public sealed record SealedSource(
    string Source, string? Repository, string? SourceCommit, bool IsSealed, string? Refusal);

/// <summary>
/// Reads what the registry SEALED for one framework identity — the publication set the instance
/// running that identity boots from (<see cref="ShippedPrebuiltBundles.SeedPublishedRoot"/>) —
/// as data a decision can be made on, per source: producing repository, source commit, sealed or
/// not. It is the reading behind "may this instance's sources advance to that commit?"
/// (MeshWeaver.Plugins#1430): an instance whose module set is pinned at boot must not import
/// sources built past the publication it runs, and the seal's own markers are the only statement
/// of which commit that is.
///
/// <para>Markers are written by <c>publish-bake-bundles.sh</c> strictly BEFORE the sentinel, so a
/// sealed directory always carries them. The repository marker is newer than the commit marker;
/// a seal without it is attributed by commit instead (see <c>SealedSyncGate</c>).</para>
///
/// <para>Pure over the file system and never throws: an unreadable root reads as "nothing sealed",
/// which callers treat as "no evidence" — never as permission.</para>
/// </summary>
public static class SealedPublicationIndex
{
    /// <summary>The producing repository's commit, one line — <c>unknown</c> when the lane had none.</summary>
    public const string SourceCommitMarkerFileName = "source-commit.txt";

    /// <summary>The producing repository as <c>owner/name</c>, one line.</summary>
    public const string RepositoryMarkerFileName = "repository.txt";

    /// <summary>
    /// Every source directory under <c>&lt;publishedRoot&gt;/&lt;identity&gt;/</c>, read as
    /// <see cref="SealedSource"/>s. Empty when the root or the identity directory is absent.
    /// </summary>
    public static IReadOnlyList<SealedSource> ReadFor(
        string? publishedRoot, string? identity, ILogger? logger = null)
    {
        if (string.IsNullOrWhiteSpace(publishedRoot) || string.IsNullOrWhiteSpace(identity))
            return [];
        var identityDirectory = Path.Combine(publishedRoot, identity);
        try
        {
            if (!Directory.Exists(identityDirectory))
                return [];
            return Directory.EnumerateDirectories(identityDirectory)
                .OrderBy(d => d, StringComparer.Ordinal)
                .Select(d => ReadSource(d, logger))
                .ToList();
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex,
                "SealedPublicationIndex: could not read the publications under {Directory} — "
                + "treating the identity as having none sealed", identityDirectory);
            return [];
        }
    }

    private static SealedSource ReadSource(string sourceDirectory, ILogger? logger)
    {
        var source = Path.GetFileName(sourceDirectory)!;
        var repository = ReadMarker(Path.Combine(sourceDirectory, RepositoryMarkerFileName));
        var commit = ReadMarker(Path.Combine(sourceDirectory, SourceCommitMarkerFileName));
        if (string.Equals(commit, "unknown", StringComparison.OrdinalIgnoreCase))
            commit = null;
        var sentinel = Path.Combine(sourceDirectory, ShippedPrebuiltBundles.CompletionSentinelFileName);
        if (!File.Exists(sentinel))
            return new SealedSource(source, repository, commit, false, "no completion sentinel");
        try
        {
            var missing = File.ReadAllLines(sentinel)
                .Select(l => l.Trim())
                .Where(l => l.Length > 0)
                .FirstOrDefault(name => !File.Exists(Path.Combine(sourceDirectory, name)));
            return missing is null
                ? new SealedSource(source, repository, commit, true, null)
                : new SealedSource(source, repository, commit, false,
                    $"the seal lists '{missing}', which is not on disk");
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "SealedPublicationIndex: could not read the seal of {Directory}", sourceDirectory);
            return new SealedSource(source, repository, commit, false, $"the seal could not be read: {ex.GetType().Name}");
        }
    }

    private static string? ReadMarker(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            var value = File.ReadAllText(path).Trim();
            return value.Length == 0 ? null : value;
        }
        catch
        {
            return null;
        }
    }
}
