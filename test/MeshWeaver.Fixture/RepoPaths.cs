using System;
using System.IO;

namespace MeshWeaver.Fixture;

/// <summary>
/// The repository root as a test sees it: the nearest directory at or above the test binary that
/// holds <c>MeshWeaver.slnx</c>.
///
/// <para>This is the ONE lookup. Test files used to carry their own copy — some anchored on the
/// solution file, some on the first <c>.github</c> directory, one on a <c>.git</c> directory — and
/// started from either <see cref="AppContext.BaseDirectory"/> or the working directory. Every copy
/// happened to resolve to the same directory today, but each one was a separate thing that could
/// drift: a <c>.git</c> anchor in particular is a FILE, not a directory, in a git worktree, so a
/// copy that tested it with <see cref="Directory.Exists(string)"/> alone would walk straight past
/// the worktree root.</para>
///
/// <para>The solution file is the anchor because it is unique to this repository's root and is the
/// same kind of filesystem entry in a primary checkout, a worktree and a CI clone.</para>
/// </summary>
public static class RepoPaths
{
    /// <summary>The file whose presence marks the repository root.</summary>
    public const string RootMarker = "MeshWeaver.slnx";

    /// <summary>
    /// The repository root, found by walking up from the test binary. Throws when the binary does not
    /// sit inside the repository — a guard that reads the tree must fail rather than pass having
    /// read nothing.
    /// </summary>
    public static string Root =>
        TryFindRoot()
        ?? throw new InvalidOperationException(
            $"Could not locate the repository root ({RootMarker}) at or above {AppContext.BaseDirectory}.");

    /// <summary>
    /// The repository root, or <c>null</c> when the test binary does not sit inside the repository —
    /// for a test that deliberately skips when the tree is out of reach.
    /// </summary>
    public static string? TryFindRoot() => TryFindRoot(AppContext.BaseDirectory);

    /// <summary>The nearest directory at or above <paramref name="start"/> holding <see cref="RootMarker"/>, or <c>null</c>.</summary>
    public static string? TryFindRoot(string start)
    {
        for (var dir = new DirectoryInfo(start); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, RootMarker)))
                return dir.FullName;
        return null;
    }

    /// <summary>A path under the repository root, segments joined with the platform separator.</summary>
    public static string Combine(params string[] segments) =>
        Path.Combine([Root, .. segments]);
}
