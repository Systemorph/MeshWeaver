using OpenSmc.Data;
using OpenSmc.Layout.Composition;

namespace OpenSmc.Layout.Domain
{
    public record ApplicationMenuBuilder(string Area, LayoutDefinition Layout, LayoutAreaReference Reference)
    {
        internal NavMenuControl Menu { get; init; } = new();

        public ApplicationMenuBuilder WithNavLink(object title, object href, Func<NavLinkControl, NavLinkControl> config)
            => this with { Menu = Menu.WithNavLink(title, href, config) };
        public ApplicationMenuBuilder WithNavLink(object title, object href)
            => this with { Menu = Menu.WithNavLink(title, href, x => x) };
        public ApplicationMenuBuilder WithNavGroup(object title, Func<NavGroupControl, NavGroupControl> config)
            => this with { Menu = Menu.WithNavGroup(title, config) };
        public ApplicationMenuBuilder WithNavGroup(object title)
            => this with { Menu = Menu.WithNavGroup(title, x => x) };

        public ApplicationMenuBuilder AddTypesCatalogs()
            => this with
            {
                Layout = Layout.WithView(Area,
                    Layout
                        .Workspace
                        .DataContext
                        .TypeSources
                        .Values

                        .OrderBy(x => x.Order ?? int.MaxValue)
                        .GroupBy(x => x.GroupName)

                        .Aggregate(
                            Menu,
                            (menu, types) => menu.WithNavGroup(types.Key ?? "Domain Types",
                                x => CreateGroup(types, x))),
                    options => options
                )
            };

        private NavGroupControl CreateGroup(IGrouping<string, ITypeSource> types, NavGroupControl x)
        {
            return types.Aggregate(x,
                (g, t) => g.WithLink(t.DisplayName,
                    $"/{Layout.Hub.Address}/Catalog?Type={t.CollectionName}", 
                    opt => opt.WithIcon(t.Icon)));
        }

        public ApplicationMenuBuilder AddRegisteredViews()
            => this with
            {
                Menu = Layout
                    .ViewGenerators
                    .Select(g => new { g.ViewElement.Area, g.ViewElement.Options })
                    .OrderBy(x => x.Options.MenuOrder)
                    .SelectMany(x => x.Options.MenuControls)
                    .Aggregate(Menu,
                        (menu, navLink) => menu.WithNavLink(navLink))
            };

        public NavMenuControl Build() => Menu;
    }
}
