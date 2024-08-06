using OpenSmc.Application.Styles;
using OpenSmc.Demo.ViewModel.CkeckBox;
using OpenSmc.Demo.ViewModel.DropDown;
using OpenSmc.Demo.ViewModel.Listbox;
using OpenSmc.Documentation;
using OpenSmc.Layout;
using OpenSmc.Layout.Composition;
using OpenSmc.Layout.Domain;
using OpenSmc.Messaging;

namespace OpenSmc.Demo.ViewModel;

public static class DemoDocumentationConfiguration
{
    private const string Overview = nameof(Overview);

    public static LayoutDefinition AddDocumentationMenu(this LayoutDefinition layout)
        => layout
            .WithNavMenu((menu, _) =>
                menu
                    .WithNavLink(Overview, layout.DocumentationPath(typeof(CounterLayoutArea).Assembly, "Overview.md"), nl => nl with { Icon = FluentIcons.Home, })
                    .WithNavLink("ViewModel State", layout.DocumentationPath(typeof(CounterLayoutArea).Assembly, "Counter.md"), nl => nl with { Icon = FluentIcons.Box, })
                    .WithNavLink("DropDown Control", layout.DocumentationPath(typeof(YearSelectArea).Assembly, "DropDown.md"), nl => nl with { Icon = FluentIcons.CalendarDataBar, })
                    .WithNavLink("Listbox Control", layout.DocumentationPath(typeof(BaseColorListArea).Assembly, "Listbox.md"), nl => nl with { Icon = FluentIcons.Grid, })
                    .WithNavLink("CheckBox Control", layout.DocumentationPath(typeof(TermsAgreementTickArea).Assembly, "CheckBox.md"), nl => nl with { Icon = FluentIcons.Box, })
            )
        ;

    public static MessageHubConfiguration AddDemoDocumentation(
        this MessageHubConfiguration configuration
    ) => configuration
        .AddDocumentation()
        .AddLayout(layout => layout.AddDocumentationMenu())
        ;
}
