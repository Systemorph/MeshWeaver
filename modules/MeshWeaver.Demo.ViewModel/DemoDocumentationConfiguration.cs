using MeshWeaver.Application.Styles;
using MeshWeaver.Demo.ViewModel.CkeckBox;
using MeshWeaver.Demo.ViewModel.DropDown;
using MeshWeaver.Demo.ViewModel.ItemTemplate;
using MeshWeaver.Demo.ViewModel.Listbox;
using MeshWeaver.Domain.Layout.Documentation;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Layout.Domain;
using MeshWeaver.Messaging;

namespace MeshWeaver.Demo.ViewModel;

public static class DemoDocumentationConfiguration
{
    private const string Overview = nameof(Overview);

    public static LayoutDefinition AddDocumentationMenu(this LayoutDefinition layout)
        => layout
            .WithNavMenu((menu, _, _) =>
                menu
                    .WithNavLink(Overview, layout.DocumentationPath(typeof(CounterLayoutArea).Assembly, "Overview.md"), FluentIcons.Home)
                    .WithNavLink("ViewModel State", layout.DocumentationPath(typeof(CounterLayoutArea).Assembly, "Counter.md"), FluentIcons.Box)
                    .WithNavLink("DropDown Control", layout.DocumentationPath(typeof(YearSelectArea).Assembly, "DropDown.md"), FluentIcons.CalendarDataBar)
                    .WithNavLink("Listbox Control", layout.DocumentationPath(typeof(BaseColorListArea).Assembly, "Listbox.md"), FluentIcons.Grid)
                    .WithNavLink("CheckBox Control", layout.DocumentationPath(typeof(TermsAgreementTickArea).Assembly, "CheckBox.md"), FluentIcons.Box)
                    .WithNavLink("Item Template", layout.DocumentationPath(typeof(ItemTemplateDemoArea).Assembly, "ItemTemplate.md"), FluentIcons.Grid)
            )
        ;

    public static MessageHubConfiguration AddDemoDocumentation(
        this MessageHubConfiguration configuration
    ) => configuration
        .AddDocumentation()
        .AddLayout(layout => layout.AddDocumentationMenu())
        ;
}
