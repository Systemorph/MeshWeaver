using System.Reactive.Linq;
using MeshWeaver.Domain;

using MeshWeaver.Layout.Composition;
using MeshWeaver.Layout.Domain;

namespace MeshWeaver.Layout;

/// <summary>
/// Fluent extension methods for registering navigation menus and bulk view collections
/// on a <see cref="LayoutDefinition"/>.
/// </summary>
public static class NavMenuExtensions
{
    /// <summary>
    /// The reserved area name used to identify the navigation menu slot in the layout.
    /// </summary>
    public const string NavMenu = "$" + nameof(NavMenu);

    /// <summary>
    /// Registers a navigation menu renderer on <paramref name="layout"/> that is built by
    /// <paramref name="config"/> on each render pass.
    /// </summary>
    /// <param name="layout">The layout definition to extend.</param>
    /// <param name="config">A delegate that receives the current menu control, host, and context and returns the configured menu.</param>
    /// <returns>A new <see cref="LayoutDefinition"/> with the nav-menu renderer registered.</returns>
    public static LayoutDefinition WithNavMenu(this LayoutDefinition layout,
        Func<NavMenuControl, LayoutAreaHost, RenderingContext, NavMenuControl> config)
        => layout.WithRenderer(a => a.Area == NavMenu,
            (h, c, store) =>
                h.ConfigBasedRenderer(
                    c,
                    store,
                    NavMenu,
                    () => new(),
                    config)
        );
    /// <summary>
    /// Adds a single navigation link with <paramref name="title"/>, <paramref name="href"/>,
    /// and optional <paramref name="icon"/> to the layout's navigation menu.
    /// </summary>
    /// <param name="layout">The layout definition to extend.</param>
    /// <param name="title">The link label displayed to the user.</param>
    /// <param name="href">The URL the link navigates to.</param>
    /// <param name="icon">Optional icon to display alongside the label.</param>
    /// <returns>A new <see cref="LayoutDefinition"/> with the nav link appended.</returns>
    public static LayoutDefinition WithNavMenu(this LayoutDefinition layout,
        object title, string href, Icon? icon = null)
        => layout.WithNavMenu((menu, _, _) => menu.WithNavLink(title, href, icon!));

    /// <summary>
    /// Appends each control in <paramref name="views"/> to <paramref name="container"/> via
    /// successive <c>WithView</c> calls.
    /// </summary>
    /// <typeparam name="TContainer">The container control type.</typeparam>
    /// <param name="container">The container to add views to.</param>
    /// <param name="views">The UI controls to append.</param>
    /// <returns>A new container with all <paramref name="views"/> added.</returns>
    public static TContainer WithViews<TContainer>(this TContainer container, params IEnumerable<UiControl> views)
    where TContainer : ContainerControl<TContainer> =>
        views.Aggregate(container, (c, v) => c.WithView(v));



}
