using System.Collections.Immutable;

namespace MeshWeaver.Mesh.Security;

/// <summary>
/// The two WRITES presentation mode makes to a viewer's own profile (issue #1803), as pure
/// transforms — the counterpart of <see cref="TimeZonePreference"/> / <see cref="LocalePreference"/>
/// for the privacy screen, and the read side's mirror (<see cref="PresentationScreen"/> is the read).
///
/// <para>Pure and hub-free on purpose: marking a node and flipping the mode are the only two ways
/// this feature changes anything, so they are the two things that have to be pinned by tests that
/// cannot be wrong about scheduling, identity or storage.</para>
///
/// <para>🚨 Both write the VIEWER's own <c>User</c> node, never the node being marked. That is the
/// whole design: a display preference belongs to the person looking at the screen, and #1803's
/// rejected workaround — renaming the node's display fields — failed precisely because it was
/// global.</para>
/// </summary>
public static class PresentationPreference
{
    /// <summary>
    /// <paramref name="path"/> added to or removed from <paramref name="current"/>, normalized,
    /// case-insensitive, and without reordering the rest.
    ///
    /// <para>Returns the SAME instance when nothing changes, so an idempotent re-mark can be turned
    /// into "write nothing" by the caller — a menu clicked twice must not mint a node version.</para>
    /// </summary>
    /// <param name="current">The viewer's current marks (<see cref="User.HiddenPaths"/>).</param>
    /// <param name="path">The node path being marked or unmarked.</param>
    /// <param name="hide">True to mark, false to unmark.</param>
    public static IReadOnlyList<string> ApplyMark(
        IReadOnlyList<string>? current, string? path, bool hide)
    {
        var marks = current as ImmutableList<string> ?? current?.ToImmutableList();
        marks ??= ImmutableList<string>.Empty;
        var normalized = (path ?? string.Empty).Trim().Trim('/');
        if (normalized.Length == 0)
            return current ?? marks;
        var index = marks.FindIndex(p =>
            string.Equals(p?.Trim().Trim('/'), normalized, StringComparison.OrdinalIgnoreCase));
        if (hide)
            return index >= 0 ? current ?? marks : marks.Add(normalized);
        return index < 0 ? current ?? marks : marks.RemoveAt(index);
    }

    /// <summary>
    /// The viewer's profile with presentation mode set to <paramref name="on"/>. A null profile
    /// (the node exists but its content has not been written yet) yields a fresh one rather than
    /// dropping the write — the toggle must work on a brand-new account.
    /// </summary>
    /// <param name="user">The viewer's profile content.</param>
    /// <param name="on">The mode state being requested.</param>
    public static User SetMode(User? user, bool on)
        => (user ?? new User()) with { PresentationMode = on };
}
