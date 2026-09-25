using System.Globalization;
using MeshWeaver.Hosting;
using MeshWeaver.Mesh.Threading;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.PluginCatalog;

/// <summary>
/// 🚨 <b>The database schema a release expects, readable from OUTSIDE its image</b> — the
/// <c>ExpectedDbVersion</c> field of the release marker (MeshWeaver#4764 (b3); policy
/// <c>db-migration-planned</c>, <c>Doc/Architecture/PlanningADatabaseMigration</c>).
///
/// <para><b>Why it has to be published.</b> <c>DbVersionGate.ExpectedDbVersion</c> is a constant
/// compiled into each portal build (MeshWeaver.Plugins <c>DbVersion.Latest</c>), so a portal running
/// the OLD image cannot read the NEW image's number, and every roll planner was left to discover a
/// schema bump from the crash-loop it caused — measured 2026-09-24/25 on memex: a pod on ci.9332
/// restarting 91 times on <c>db_version=57 &lt; expected 58</c>. With the number published beside the
/// release, a planner compares two numbers BEFORE it moves anything: a target that keeps the schema
/// may roll on any outcome, and a target that moves it rolls only behind a migration that
/// demonstrably ran — or is refused, naming both numbers.</para>
///
/// <para><b>Where it lives, and why there.</b> <c>&lt;root&gt;/_releases/_db/&lt;version&gt;</c>, one
/// file per release whose whole content is the integer. It is a SUBDIRECTORY of the marker
/// directory on purpose: every reader of <c>_releases</c> — portals already deployed included —
/// enumerates FILES only and reads each body as a framework identity, so a second line in the marker
/// or a sibling file would be read as an identity or a version by images that predate this field. A
/// subdirectory is invisible to all of them. The OCI copy of the marker carries the same number as
/// <c>expectedDbVersion</c> in its config.</para>
///
/// <para><b>Absent is not zero.</b> A release published before this field existed has no file, and
/// that reads as UNKNOWN — never as "expects nothing" — so a planner falls back to what it did before
/// (run the migration and let only its outcome decide).</para>
/// </summary>
public static class ReleaseSchemaMarker
{
    /// <summary>The subdirectory of the release-marker directory that holds one file per release.</summary>
    public const string DirectoryName = "_db";

    /// <summary>The file that carries <paramref name="version"/>'s expected schema version, or null
    /// when either argument cannot name one (a version with a path separator is a caller bug and is
    /// never turned into a path).</summary>
    public static string? PathFor(string? publishedRoot, string? version)
    {
        var plain = PlainVersion(version);
        if (string.IsNullOrWhiteSpace(publishedRoot) || plain is null)
            return null;
        return Path.Combine(publishedRoot, SealedPublicationIndex.ReleaseMarkerDirectoryName, DirectoryName, plain);
    }

    /// <summary>
    /// The schema version <paramref name="version"/> expects, or null when it is not published (a
    /// release that predates the field), unreadable, or not an integer. Null is UNKNOWN, never zero.
    /// </summary>
    public static int? Read(string? publishedRoot, string? version, ILogger? logger = null)
    {
        var path = PathFor(publishedRoot, version);
        if (path is null)
            return null;
        try
        {
            if (!File.Exists(path))
                return null;
            var text = File.ReadAllText(path).Trim();
            if (int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var expected) && expected > 0)
                return expected;
            logger?.LogWarning(
                "ReleaseSchemaMarker: {Path} does not hold a positive integer ('{Text}') — the schema {Version} expects is UNKNOWN",
                path, text.Length > 32 ? text[..32] : text, version);
            return null;
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex,
                "ReleaseSchemaMarker: could not read {Path} — the schema {Version} expects is UNKNOWN", path, version);
            return null;
        }
    }

    /// <summary>
    /// What rolling from <paramref name="installedVersion"/> to <paramref name="targetVersion"/> does
    /// to the schema, as far as the published markers say. Pure apart from the two file reads.
    /// </summary>
    public static ReleaseSchemaStep Step(
        string? publishedRoot, string? installedVersion, string targetVersion, ILogger? logger = null)
    {
        var installed = PlainVersion(installedVersion);
        return new ReleaseSchemaStep(
            installed, installed is null ? null : Read(publishedRoot, installed, logger),
            targetVersion, Read(publishedRoot, targetVersion, logger));
    }

    /// <summary>Reactive form of <see cref="Step"/> — the file-system leaf runs on the caller's I/O
    /// pool, never on a hub action block.</summary>
    public static IObservable<ReleaseSchemaStep> ObserveStep(
        IIoPool pool, string? publishedRoot, string? installedVersion, string targetVersion, ILogger? logger = null) =>
        pool.InvokeBlocking(_ => Step(publishedRoot, installedVersion, targetVersion, logger));

    /// <summary>A version as a marker file NAME: build metadata (<c>+sha</c>) dropped, and anything that
    /// could escape the directory refused.</summary>
    private static string? PlainVersion(string? version)
    {
        if (string.IsNullOrWhiteSpace(version))
            return null;
        var plain = version.Split('+')[0].Trim();
        if (plain.Length == 0 || plain is "." or ".." || plain.IndexOfAny(['/', '\\']) >= 0)
            return null;
        return plain;
    }
}

/// <summary>
/// What a roll does to the database schema, read off the two releases' published
/// <see cref="ReleaseSchemaMarker">ExpectedDbVersion</see> markers.
/// </summary>
/// <param name="InstalledVersion">The running release, or null when it cannot be named.</param>
/// <param name="InstalledExpected">The schema the running release expects — which the database has
/// reached, because a pod of that release passed <c>DbVersionGate</c> to be running at all.</param>
/// <param name="TargetVersion">The release the roll moves to.</param>
/// <param name="TargetExpected">The schema the target expects.</param>
public sealed record ReleaseSchemaStep(
    string? InstalledVersion, int? InstalledExpected, string TargetVersion, int? TargetExpected)
{
    /// <summary>Nothing is published for one side or the other — the planner knows nothing more than
    /// before this field existed.</summary>
    public static ReleaseSchemaStep Unknown(string targetVersion) => new(null, null, targetVersion, null);

    /// <summary>Both numbers are published.</summary>
    public bool Known => InstalledExpected is not null && TargetExpected is not null;

    /// <summary>The target expects a NEWER schema than the running release: the image must not move
    /// unless the target's migration demonstrably ran first.</summary>
    public bool MovesSchema => Known && TargetExpected > InstalledExpected;

    /// <summary>The target expects no newer schema than the one the database already has: the image
    /// may move whatever the migration step answered, because there is nothing for it to establish.</summary>
    public bool KeepsSchema => Known && TargetExpected <= InstalledExpected;

    /// <summary>One machine-readable clause for a verdict or a log line.</summary>
    public string Describe() =>
        Known
            ? $"{TargetVersion} expects db_version {TargetExpected}, the running {InstalledVersion} expects {InstalledExpected}"
            : $"the schema {TargetVersion} expects is not published (ExpectedDbVersion marker absent) — "
              + "only the migration's own outcome can decide";
}
