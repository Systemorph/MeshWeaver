using System.Collections.Generic;
using MeshWeaver.Layout.Composition;

namespace MeshWeaver.Graph;

/// <summary>
/// Supplies the left-hand navigation for pages a module OWNS, in place of core's default
/// "the node's own children" list.
///
/// <para><b>Why this is a seam and not a widening.</b> Core's default lists the current node's
/// children, which is right for a doc or a space and wrong for a course: a reader standing in
/// lesson 2 sees lesson 2's sub-nodes and nothing else, so they cannot tell how long the course is,
/// what is coming, or that they are nearly done. But core must not learn what a lesson is to fix
/// that. The course semantics — what counts as a module, which sub-namespaces hold lessons, which
/// satellites to hide — already live in the courses module, and re-deriving them here would put a
/// copy in the wrong project.</para>
///
/// <para><b>The default stands.</b> A provider that does not claim a node returns null and core
/// renders exactly what it renders today, so docs and spaces are untouched and a deployment without
/// the module simply loses the richer menu rather than breaking.</para>
///
/// <para><b>Reactive by construction.</b> A whole-course index is a QUERY, and a query is an
/// <see cref="IObservable{T}"/> — so the seam hands back a stream, never a materialised list. That
/// is what lets a provider walk up to the course root and read its subtree without blocking the
/// render (see AsynchronousCalls.md: nothing on a hub may await). The stream feeds straight into
/// the area's <c>CombineLatest</c>, so an added, renamed or re-ordered lesson re-renders the index
/// live.</para>
/// </summary>
public interface INodeNavigationProvider
{
    /// <summary>
    /// The navigation to show for the page <paramref name="host"/> renders, as a live stream, or
    /// <c>null</c> to decline outright — core then falls back to its default child list. Declining
    /// is the normal answer for nodes this module does not own; it must be cheap (a path shape
    /// check, never a query) and must never throw.
    ///
    /// <para>A provider that can only decide by querying returns a stream and emits <c>null</c>
    /// (or an empty <see cref="NodeNavigation.Entries"/>) for the pages it does not claim. The
    /// stream MUST emit promptly — core combines it with the node and permission streams, so one
    /// that stays silent would hold the whole page back.</para>
    /// </summary>
    /// <param name="host">The layout-area host rendering the page; its hub address IS the node path.</param>
    IObservable<NodeNavigation?>? GetNavigation(LayoutAreaHost host);
}

/// <summary>
/// A supplied left-hand navigation: the heading plus the entries beneath it.
/// </summary>
/// <param name="Title">The menu heading — the thing being indexed (e.g. the course name), not the
/// page being read.</param>
/// <param name="Entries">The top-level entries, in render order.</param>
public record NodeNavigation(string Title, IReadOnlyList<NodeNavigationEntry> Entries)
{
    /// <summary>
    /// The node the heading links to (the root of the supplied index), or <c>null</c> for a plain
    /// heading. Core drops the link when that root IS the page being read — a link to the page you
    /// are on is a dead control.
    /// </summary>
    public string? TitlePath { get; init; }
}

/// <summary>
/// One line of a supplied navigation. <paramref name="IsCurrent"/> marks where the reader is —
/// core renders that entry with a glyph AND an accent, and as text rather than a link, because a
/// link to the page you are on is a dead control that teaches the reader their position marker is
/// broken. The glyph carries the marker for a reader who cannot distinguish the accent.
/// </summary>
/// <param name="Label">The text shown for the entry.</param>
/// <param name="Path">The mesh path the entry stands for (and links to).</param>
/// <param name="IsCurrent">Whether this entry is where the reader stands.</param>
/// <param name="Icon">Optional icon for the entry.</param>
public record NodeNavigationEntry(string Label, string Path, bool IsCurrent = false, string? Icon = null)
{
    /// <summary>
    /// Nested entries — core renders an entry that has them as a collapsible sub-group, which is how
    /// a long index stays scannable (a course groups its lessons by module).
    /// </summary>
    public IReadOnlyList<NodeNavigationEntry> Children { get; init; } = [];
}
