using OpenSmc.Data;
using OpenSmc.Layout.Composition;

namespace OpenSmc.Layout.Domain
{
    public record DomainMenuBuilder(string Area, LayoutDefinition Layout)
    {
        internal NavMenuControl Menu { get; init; } = new();

        public DomainMenuBuilder WithNavLink(object title, object href, Func<NavLinkControl, NavLinkControl> config)
            => this with { Menu = Menu.WithNavLink(title, href, config) };
        public DomainMenuBuilder WithNavLink(object title, object href)
            => this with { Menu = Menu.WithNavLink(title, href, x => x) };
        public DomainMenuBuilder WithNavGroup(object title, Func<NavGroupControl, NavGroupControl> config)
            => this with { Menu = Menu.WithNavGroup(title, config) };
        public DomainMenuBuilder WithNavGroup(object title)
            => this with { Menu = Menu.WithNavGroup(title, x => x) };

        public DomainMenuBuilder WithTypesCatalog()
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
                                x => CreateGroup(types, x)))
                )
            };

        private NavGroupControl CreateGroup(IGrouping<string, ITypeSource> types, NavGroupControl x)
        {
            return types.Aggregate(x,
                (g, t) => g.WithLink(t.DisplayName,
                    $"/{Layout.Hub.Address}/Catalog/{t.CollectionName}", 
                    opt => opt.WithIcon(t.Icon)));
        }


        public LayoutDefinition Build() => Layout.WithView(Area, Menu);
    }
}
