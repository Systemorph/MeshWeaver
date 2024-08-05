using OpenSmc.Application.Styles;
using OpenSmc.Documentation;
using OpenSmc.Layout;
using OpenSmc.Layout.Composition;
using OpenSmc.Layout.Domain;
using OpenSmc.Messaging;

namespace OpenSmc.Demo.ViewModel;

public static class DemoDocumentationConfiguration
{
    private const string Overview = nameof(Overview);
    private const string ViewModelState = nameof(ViewModelState);
    private const string DropDownControl = nameof(DropDownControl);
    private const string ListboxControl = nameof(ListboxControl);
    private const string CheckBoxControl = nameof(CheckBoxControl);

    public static LayoutDefinition AddDocumentationMenu(this LayoutDefinition layout)
        => layout
            .WithNavMenu((menu, _) =>
                menu
                    .WithNavLink(Overview, $"{layout.Hub.Address}/Doc/{Overview}", nl => nl with { Icon = FluentIcons.Home, })
                    .WithNavLink("ViewModel State", $"{layout.Hub.Address}/Doc/{ViewModelState}", nl => nl with { Icon = FluentIcons.Box, })
                    .WithNavLink("DropDown Control", $"{layout.Hub.Address}/Doc/{DropDownControl}", nl => nl with { Icon = FluentIcons.CalendarDataBar, })
                    .WithNavLink("Listbox Control", $"{layout.Hub.Address}/Doc/{ListboxControl}", nl => nl with { Icon = FluentIcons.Grid, })
                    .WithNavLink("CheckBox Control", $"{layout.Hub.Address}/Doc/{CheckBoxControl}", nl => nl with { Icon = FluentIcons.Box, })
            )
        ;

    public static MessageHubConfiguration AddDemoDocumentation(
        this MessageHubConfiguration configuration
    ) => configuration
        .AddDocumentation()
        .AddLayout(layout => layout.AddDocumentationMenu())
        ;
}
