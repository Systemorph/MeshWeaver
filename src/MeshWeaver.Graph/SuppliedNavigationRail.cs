using System;
using System.Collections.Generic;
using System.Linq;
using MeshWeaver.Layout;

namespace MeshWeaver.Graph;

/// <summary>
/// Turns a module-supplied <see cref="NodeNavigation"/> into the left-hand rail — as a PURE PLAN
/// first (<see cref="Plan"/>), then a control tree (<see cref="Render"/>).
///
/// <para><b>Why the plan exists.</b> A <see cref="ContainerControl{TControl}"/>'s child views are
/// protected, so a rail built straight into controls cannot be asserted on: a test can count areas
/// and read a skin, and nothing else. Every defect this class was written to fix was invisible to
/// exactly that kind of test. The plan is an ordinary record, so what the rail CONTAINS — which
/// entry is current, which group is open, what each line links to, and in WHAT ORDER — is pinned by
/// unit tests instead of by opening a page and looking.</para>
///
/// <para><b>The shape, and the four defects that chose it</b> (the first three reported on a live
/// course, 2026-08-19; the fourth on 2026-09-06 — the same rail is rendered by every page whose
/// module supplies one):</para>
/// <list type="number">
///   <item><b>The title is a LINK, never the collapsible root.</b> Nesting the whole index under one
///   <see cref="NavGroupControl"/> made its heading both a link and a toggle, so clicking the
///   course name collapsed the entire index. Groups toggle, links navigate — never both on one
///   control.</item>
///   <item><b>An entry with children is a group AND its own first link.</b> The heading expands; the
///   link beneath it opens the page and is what carries the position marker, because a group
///   heading has no active state to carry one.</item>
///   <item><b>The current entry stays IN the tree, as an active link.</b> It used to be swapped for
///   a bare <c>Controls.Body</c> — no icon, no indentation — so the line the reader was standing on
///   jumped to the far-left margin and read as belonging to nothing.
///   <see cref="NavLinkControl.IsActive"/> gives it the accent bar, background and weight the
///   nav menu already styles, none of which is colour-only.</item>
///   <item><b>The SUPPLIER'S ORDER is the rail's order — having children is not a sort key.</b> The
///   plan used to hold two buckets, <c>Pages</c> (entries with no children) and <c>Groups</c>
///   (entries with children), and render every page before every group. The supplied order survived
///   inside each bucket and was lost between them, so an entry could never precede one that had
///   children — a course's four lessons, ordered 1–4, rendered tenth to thirteenth behind every leaf
///   page, and no <c>Order</c> value could fix it because the numbers were already right (#3406).
///   The plan now carries ONE ordered sequence, <see cref="Rail.Items"/>, whose element is a
///   <see cref="RailLink"/> or a <see cref="RailGroup"/>, and <see cref="Render"/> walks it once.
///   Both container renderers emit a container's areas in declaration order, so preserving the order
///   here is the whole fix.</item>
/// </list>
///
/// <para>Only the group the reader is inside is expanded, so a long index stays a scannable list of
/// chevrons — and every other group visibly OFFERS its expander.</para>
/// </summary>
public static class SuppliedNavigationRail
{
    /// <summary>
    /// One item of the rail, in the order its supplier declared it: a <see cref="RailLink"/> or a
    /// <see cref="RailGroup"/>, and nothing else — the constructor is <c>private protected</c>, so
    /// the two cases below are the whole hierarchy and <see cref="Render"/>'s walk over them is
    /// exhaustive.
    /// </summary>
    public abstract record RailItem
    {
        private protected RailItem() { }
    }

    /// <summary>One line of the rail.</summary>
    /// <param name="Label">The text shown.</param>
    /// <param name="Path">The node this line stands for.</param>
    /// <param name="Href">Where it navigates, or null when the node is not there to link to.</param>
    /// <param name="IsCurrent">Whether this is where the reader stands (exactly one line, at most).</param>
    /// <param name="Icon">The node's icon, if it has one.</param>
    public sealed record RailLink(string Label, string Path, string? Href, bool IsCurrent, string? Icon) : RailItem;

    /// <summary>A collapsible group of the rail — one supplied entry that has children.</summary>
    /// <param name="Label">The group heading (never a link — it toggles).</param>
    /// <param name="Path">The node the group stands for.</param>
    /// <param name="Expanded">Open only when the reader is at or below <paramref name="Path"/>.</param>
    /// <param name="Links">The entry's own link first, then its children.</param>
    /// <param name="Icon">The entry's icon, if it has one — the heading shows it, so a group reads
    /// like the links beside it instead of losing its icon by having children.</param>
    public sealed record RailGroup(string Label, string Path, bool Expanded, IReadOnlyList<RailLink> Links, string? Icon = null) : RailItem;

    /// <summary>The whole rail: the heading link, then the supplied entries in the supplied order.</summary>
    /// <param name="Home">The index root — a link, not a collapsible heading.</param>
    /// <param name="Items">The supplied entries, IN THE ORDER SUPPLIED — a childless entry as a
    /// <see cref="RailLink"/>, one with children as a <see cref="RailGroup"/>. One sequence, not a
    /// bucket per kind: see the class remarks, defect 4.</param>
    public sealed record Rail(RailLink Home, IReadOnlyList<RailItem> Items);

    /// <summary>
    /// The rail a page shows, from the navigation its module supplied. Pure — see the class remarks
    /// for the shape and why each part of it is the way it is.
    /// </summary>
    /// <param name="supplied">What an <see cref="INodeNavigationProvider"/> claimed the page with.</param>
    /// <param name="currentPath">The page being read.</param>
    public static Rail Plan(NodeNavigation supplied, string currentPath)
    {
        ArgumentNullException.ThrowIfNull(supplied);

        var root = supplied.TitlePath;
        var home = new RailLink(
            supplied.Title,
            root ?? string.Empty,
            // A supplied index whose ROOT NODE is not there (a copy whose root was never created)
            // must not be linked: path resolution matches the longest existing prefix and reads the
            // trailing segment as an AREA, so the reader gets "no renderer is registered for area
            // {Root}" — a rendering error for what is really a missing node.
            root is null ? null : "/" + root,
            root is not null && string.Equals(root, currentPath, StringComparison.Ordinal),
            supplied.Icon);

        // One projection, entry by entry, in the supplied order. Whether an entry has children
        // decides WHAT it becomes, never WHERE it goes — the ordering the supplier computed (Order
        // then Name, for a course) is the reading order, and re-grouping by kind silently discarded
        // it (#3406).
        var items = supplied.Entries
            .Select(entry => entry.Children.Count == 0
                ? (RailItem)Link(entry)
                : new RailGroup(
                    entry.Label,
                    entry.Path,
                    IsAtOrBelow(currentPath, entry.Path),
                    // The entry's own link first, then its children.
                    [Link(entry), .. entry.Children.Select(Link)],
                    entry.Icon))
            .ToList();

        return new Rail(home, items);
    }

    /// <summary>
    /// Renders a plan as the nav menu: the heading link, then every supplied entry in the order the
    /// plan holds it — a link, or a collapsible group when the entry has children.
    /// </summary>
    /// <param name="rail">The plan to render.</param>
    /// <param name="width">Menu width in pixels.</param>
    /// <param name="collapsible">Whether the menu offers the collapse toggle.</param>
    public static NavMenuControl Render(Rail rail, int width = 240, bool collapsible = true)
    {
        ArgumentNullException.ThrowIfNull(rail);

        var menu = Controls.NavMenu
            .WithSkin(s => s.WithWidth(width).WithCollapsible(collapsible))
            .WithNavLink(RenderLink(rail.Home));

        // ONE walk, in plan order. WithNavLink and WithNavGroup both append to the container's
        // single ordered area list, and both renderers (Blazor's NavMenuView, the React NavMenu
        // skin) emit those areas in that order — so this loop is where the supplier's order either
        // survives or dies. Two loops, one per kind, is what #3406 was.
        foreach (var item in rail.Items)
            menu = item switch
            {
                // NO url on the heading: it toggles, and a control that both navigates and toggles
                // is how clicking the index title used to collapse the whole index. The group's own
                // page is the first LINK inside it.
                RailGroup group => menu.WithNavGroup(RenderGroup(group)),
                RailLink link => menu.WithNavLink(RenderLink(link)),
                // Unreachable: RailItem's constructor is private protected, so RailLink and
                // RailGroup are the whole hierarchy. Loud rather than silent if that ever changes —
                // a dropped item is a line missing from the index with nothing to show for it.
                _ => throw new NotSupportedException(
                    $"Unknown rail item '{item.GetType().Name}' — extend {nameof(Render)} when adding a {nameof(RailItem)} case."),
            };

        return menu;
    }

    private static NavGroupControl RenderGroup(RailGroup group)
    {
        var rendered = new NavGroupControl(group.Label).WithSkin(s => s.WithExpanded(group.Expanded));
        if (group.Icon is not null)
            rendered = rendered.WithIcon(group.Icon);
        foreach (var link in group.Links)
            rendered = rendered.WithView(RenderLink(link));
        return rendered;
    }

    private static RailLink Link(NodeNavigationEntry entry)
        => new(entry.Label, entry.Path, "/" + entry.Path, entry.IsCurrent, entry.Icon);

    private static NavLinkControl RenderLink(RailLink link)
        => new NavLinkControl(link.Label, link.Icon, link.Href).WithIsActive(link.IsCurrent);

    /// <summary>Whether <paramref name="currentPath"/> IS <paramref name="path"/> or sits below it.</summary>
    /// <param name="currentPath">The page being read.</param>
    /// <param name="path">The candidate ancestor.</param>
    public static bool IsAtOrBelow(string currentPath, string path)
        => string.Equals(currentPath, path, StringComparison.Ordinal)
           || currentPath.StartsWith(path + "/", StringComparison.Ordinal);
}
