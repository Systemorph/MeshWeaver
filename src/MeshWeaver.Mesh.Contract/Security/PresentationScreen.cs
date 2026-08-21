using System.Collections.Immutable;

namespace MeshWeaver.Mesh.Security;

/// <summary>
/// The viewer's PRESENTATION SCREEN — which of their own paths the portal must leave off
/// tile, card and completion surfaces while they are sharing their screen (issue #1803).
///
/// <para>🚨 <b>This is display-only, and it is not an access-control mechanism.</b> It grants
/// nothing and it denies nothing: <see cref="Hides"/> is a pure predicate over PATHS with no
/// permission, no <c>AccessContext</c>, no node read and no query involvement, and nothing in this
/// type is reachable from the permission evaluator. A hidden space stays readable, navigable by
/// direct URL, returned by the mesh query engine, and completely unchanged for every OTHER viewer.
/// The marking lives on the viewer's own profile (<see cref="User.HiddenPaths"/>), never on the
/// shared node — which is precisely the failure of the rename workaround #1803 describes: renaming
/// a node to disguise it is GLOBAL, so it hides the name from everyone and has to be undone
/// afterwards.</para>
///
/// <para>Two independent facts make an item disappear, and both are the viewer's own:
/// <list type="number">
///   <item><b>The mode is on</b> (<see cref="Active"/>, from <see cref="User.PresentationMode"/>) —
///     the quick toggle flipped before a screen share.</item>
///   <item><b>The path is marked</b> (<see cref="MarkedPaths"/>, from
///     <see cref="User.HiddenPaths"/>).</item>
/// </list>
/// A marking on its own hides NOTHING — <see cref="Hides"/> is <c>false</c> for every path while
/// <see cref="Active"/> is <c>false</c>. That is what makes the feature fully reversible with one
/// toggle and no restore step, and what keeps a stale marking from quietly removing something from
/// a user's home forever.</para>
///
/// <para><b>Marking a space hides its subtree.</b> Hiding <c>Acme</c> but still listing
/// <c>Acme/Q3-Renewal</c> under "Last edited" would leak the very name the mark exists to keep off
/// the screen — the path IS the name. So containment is by path segment
/// (<c>Acme</c> hides <c>Acme/Q3-Renewal</c>, and never <c>AcmeCorp</c>).</para>
///
/// <para>Pure and immutable: no hub, no circuit, no ambient state — so every surface's behaviour is
/// unit-testable, and a caller can resolve the screen ONCE on the render turn and pass this value
/// down instead of re-reading an <c>AsyncLocal</c> on a later emission (the
/// <c>AccessService.ToDisplayTime</c> rule).</para>
/// </summary>
public sealed record PresentationScreen
{
    /// <summary>
    /// The neutral screen: nothing is hidden. The value every non-viewer path uses — an anonymous
    /// visitor, a hub credential, a viewer with no profile — and the value a viewer has whenever
    /// presentation mode is off.
    /// </summary>
    public static PresentationScreen Off { get; } = new();

    /// <summary>
    /// Whether presentation mode is currently ON for this viewer (<see cref="User.PresentationMode"/>).
    /// While this is <c>false</c>, <see cref="Hides"/> answers <c>false</c> for every path no matter
    /// what <see cref="MarkedPaths"/> holds.
    /// </summary>
    public bool Active { get; init; }

    /// <summary>
    /// The paths the viewer marked as "hide in presentation mode", normalized (trimmed of leading
    /// and trailing <c>/</c>) and compared case-insensitively — the same shape mesh paths are
    /// matched with everywhere else.
    /// </summary>
    public ImmutableHashSet<string> MarkedPaths { get; init; } =
        ImmutableHashSet.Create<string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Builds a screen from the two profile fields. Empty/whitespace entries are dropped and paths
    /// are normalized, so a hand-edited profile cannot produce a mark that matches everything.
    /// </summary>
    /// <param name="active">Whether presentation mode is on.</param>
    /// <param name="markedPaths">The paths the viewer marked as hidden.</param>
    public static PresentationScreen For(bool active, IEnumerable<string>? markedPaths)
    {
        var marks = Normalize(markedPaths);
        // The mode stays observable even with nothing marked — the header indicator reads Active,
        // and a viewer who turns the mode on before marking anything must still see it lit.
        return !active && marks.IsEmpty
            ? Off
            : new PresentationScreen { Active = active, MarkedPaths = marks };
    }

    /// <summary>
    /// Builds the screen a <see cref="User"/> profile describes. A null profile (no mesh user node
    /// yet) is <see cref="Off"/>.
    /// </summary>
    /// <param name="profile">The viewer's own profile content.</param>
    public static PresentationScreen From(User? profile)
        => profile is null ? Off : For(profile.PresentationMode, profile.HiddenPaths);

    /// <summary>
    /// Whether <paramref name="path"/> must be left off tile / card / completion surfaces for this
    /// viewer right now — the mode is on AND the path is marked or sits inside a marked subtree.
    ///
    /// <para>🚨 Never a permission check. A <c>true</c> here means "do not PAINT this"; it says
    /// nothing about whether the viewer may read the node, and no caller may use it to decide a
    /// read, a write or a route.</para>
    /// </summary>
    /// <param name="path">The mesh path of the item about to be rendered.</param>
    public bool Hides(string? path)
    {
        if (!Active || MarkedPaths.IsEmpty || string.IsNullOrWhiteSpace(path))
            return false;
        var normalized = NormalizeOne(path);
        if (normalized.Length == 0)
            return false;
        if (MarkedPaths.Contains(normalized))
            return true;
        // Ancestor containment by SEGMENT: "Acme" hides "Acme/Q3-Renewal" and never "AcmeCorp".
        // Walking the candidate's own ancestors is O(depth) and independent of how many marks
        // there are, so a viewer with a long list costs the same as one with a single mark.
        for (var slash = normalized.LastIndexOf('/'); slash > 0; slash = normalized.LastIndexOf('/', slash - 1))
            if (MarkedPaths.Contains(normalized[..slash]))
                return true;
        return false;
    }

    /// <summary>
    /// The items that may be painted — <paramref name="items"/> minus everything
    /// <see cref="Hides"/>. Deferred like every other LINQ operator; materialize it where you need
    /// a list.
    /// </summary>
    /// <typeparam name="T">The item type (a node, a card, a completion, …).</typeparam>
    /// <param name="items">The items a surface is about to render.</param>
    /// <param name="pathOf">Reads the mesh path an item stands for; items with no path are kept.</param>
    public IEnumerable<T> Filter<T>(IEnumerable<T> items, Func<T, string?> pathOf)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(pathOf);
        return !Active || MarkedPaths.IsEmpty ? items : items.Where(item => !Hides(pathOf(item)));
    }

    /// <summary>
    /// The paths that may be painted, in their original order and original spelling — the string
    /// overload of <see cref="Filter{T}"/>, for surfaces (pinned tiles) whose input IS a path list
    /// and which must not put a hidden path into the query string they emit.
    /// </summary>
    /// <param name="paths">The paths a surface is about to render.</param>
    public IReadOnlyList<string> Retain(IEnumerable<string>? paths)
        => paths is null ? [] : Filter(paths, p => p).ToList();

    private static ImmutableHashSet<string> Normalize(IEnumerable<string>? paths)
    {
        if (paths is null)
            return ImmutableHashSet.Create<string>(StringComparer.OrdinalIgnoreCase);
        var builder = ImmutableHashSet.CreateBuilder<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in paths)
        {
            if (string.IsNullOrWhiteSpace(path))
                continue;
            var normalized = NormalizeOne(path);
            if (normalized.Length > 0)
                builder.Add(normalized);
        }
        return builder.ToImmutable();
    }

    private static string NormalizeOne(string path) => path.Trim().Trim('/');
}
