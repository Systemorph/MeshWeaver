using System.Collections.Generic;
using MeshWeaver.Mesh;

namespace MeshWeaver.Graph;

/// <summary>
/// Supplies the left-hand navigation for pages a module OWNS, in place of core's default
/// "the node's own children" list.
///
/// <para><b>Why this is a seam and not a widening.</b> Core's default lists the current node's
/// children, which is right for a doc or a space and wrong for a course: a reader standing in
/// lesson 2 sees lesson 2's sub-nodes and nothing else, so they cannot tell how long the course is,
/// what is coming, or that they are nearly done. But core must not learn what a lesson is to fix
/// that. The course semantics — the <c>module*100 + difficulty</c> Order convention, what counts as
/// a chapter, which satellites to hide — already live in the Edu module, and re-deriving them here
/// would put a copy in the wrong repo.</para>
///
/// <para><b>The default stands.</b> A provider that does not claim a node returns null and core
/// renders exactly what it renders today, so docs and spaces are untouched and a deployment without
/// the module simply loses the richer menu rather than breaking.</para>
/// </summary>
public interface INodeNavigationProvider
{
    /// <summary>
    /// The entries to show for <paramref name="node"/>, or <c>null</c> to decline — core then falls
    /// back to its default child list. Declining is the normal answer for nodes this module does
    /// not own; it must be cheap and must never throw.
    /// </summary>
    /// <param name="node">The page being rendered.</param>
    /// <param name="children">The children core would have listed, so a provider that only wants to
    /// re-order or re-label them need not query again.</param>
    IReadOnlyList<NodeNavigationEntry>? GetNavigation(MeshNode? node, IReadOnlyList<MeshNode> children);
}

/// <summary>
/// One line of a supplied navigation. <paramref name="IsCurrent"/> marks where the reader is —
/// core renders that entry as text rather than a link, because a link to the page you are on is a
/// dead control that teaches the reader their position marker is broken.
/// </summary>
public record NodeNavigationEntry(string Label, string Path, bool IsCurrent = false, string? Icon = null);
