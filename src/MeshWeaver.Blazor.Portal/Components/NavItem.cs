using Microsoft.FluentUI.AspNetCore.Components;

namespace MeshWeaver.Blazor.Portal.Components;

/// <summary>
/// A single entry in the portal navigation menu: a display label (<paramref name="Title"/>),
/// the route it links to (<paramref name="Href"/>), and the icon shown beside it
/// (<paramref name="Icon"/>). The positional parameters back the record's
/// <c>Title</c>, <c>Href</c>, and <c>Icon</c> properties.
/// </summary>
/// <param name="Title">The label rendered for the navigation entry.</param>
/// <param name="Href">The route the entry navigates to when activated.</param>
/// <param name="Icon">The icon displayed alongside the label.</param>
public record NavItem(string Title, string Href, Icon Icon);
