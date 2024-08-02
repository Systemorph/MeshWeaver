using MeshWeaver.Application.Styles;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Layout.Domain;

namespace MeshWeaver.Demo.ViewModel.ItemTemplate;

public static class ItemTemplateDemoArea
{
    public static LayoutDefinition AddCurrenciesItemTemplateDemo(this LayoutDefinition layout)
        => layout.WithView(nameof(Currencies.CurrenciesFilter), Currencies.CurrenciesFilter)
            .WithNavMenu((menu, _, _) => menu
                .WithNavLink(
                    "Raw: ItemTemplate (currencies)",
                    new LayoutAreaReference(nameof(Currencies.CurrenciesFilter)).ToHref(layout.Hub.Address),
                    FluentIcons.Person
                )
            );
}
