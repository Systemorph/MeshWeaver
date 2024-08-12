using MeshWeaver.Application.Styles;
using MeshWeaver.Demo.ViewModel.CkeckBox;
using MeshWeaver.Demo.ViewModel.DropDown;
using MeshWeaver.Demo.ViewModel.ItemTemplate;
using MeshWeaver.Demo.ViewModel.Listbox;
using MeshWeaver.Documentation;
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
                    .WithNavLink(Overview, layout.DocumentationPath(typeof(CounterLayoutArea).Assembly, "Overview.md"), nl => nl with { Icon = FluentIcons.Home, })
                    .WithNavLink("ViewModel State", layout.DocumentationPath(typeof(CounterLayoutArea).Assembly, "Counter.md"), nl => nl with { Icon = FluentIcons.Box, })
                    .WithNavLink("DropDown Control", layout.DocumentationPath(typeof(YearSelectArea).Assembly, "DropDown.md"), nl => nl with { Icon = FluentIcons.CalendarDataBar, })
                    .WithNavLink("Listbox Control", layout.DocumentationPath(typeof(BaseColorListArea).Assembly, "Listbox.md"), nl => nl with { Icon = FluentIcons.Grid, })
                    .WithNavLink("CheckBox Control", layout.DocumentationPath(typeof(TermsAgreementTickArea).Assembly, "CheckBox.md"), nl => nl with { Icon = FluentIcons.Box, })
                    .WithNavLink("Item Template", layout.DocumentationPath(typeof(ItemTemplateDemoArea).Assembly, "ItemTemplate.md"), nl => nl with { Icon = FluentIcons.Grid, })
            )
        ;

    public static MessageHubConfiguration AddDemoDocumentation(
        this MessageHubConfiguration configuration
    ) => configuration
        .AddDocumentation()
        .AddLayout(layout => layout.AddDocumentationMenu())
        ;
}
