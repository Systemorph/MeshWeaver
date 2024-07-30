using OpenSmc.Application.Styles;
using OpenSmc.Documentation;
using OpenSmc.Layout.Domain;
using OpenSmc.Messaging;

namespace OpenSmc.Demo.ViewModel;

public static class DemoDocumentationConfiguration
{
    private const string Overview = nameof(Overview);
    private const string ViewModelState = nameof(ViewModelState);
    private const string DropDownControl = nameof(DropDownControl);

    public static ApplicationMenuBuilder AddDocumentationMenu(this ApplicationMenuBuilder builder)
        => builder
            .WithNavLink(Overview, $"{builder.Layout.Hub.Address}/Doc/{Overview}", nl => nl with { Icon = FluentIcons.Home, })
            .WithNavLink("ViewModel State", $"{builder.Layout.Hub.Address}/Doc/{ViewModelState}", nl => nl with { Icon = FluentIcons.Box, })
            .WithNavLink("DropDown Control", $"{builder.Layout.Hub.Address}/Doc/{DropDownControl}", nl => nl with { Icon = FluentIcons.CalendarDataBar, })
        ;

    public static MessageHubConfiguration AddDemoDocumentation(
        this MessageHubConfiguration configuration
    ) => configuration
        .AddDocumentation(doc =>
            doc.WithEmbeddedResourcesFrom(typeof(ViewModelStateDemoArea).Assembly,
                source => source
                    .WithDocument(Overview,
                        $"{typeof(ViewModelStateDemoArea).Assembly.GetName().Name}.Markdown.Overview.md")
                    .WithDocument(ViewModelState,
                        $"{typeof(ViewModelStateDemoArea).Assembly.GetName().Name}.Markdown.Counter.md")
                    .WithDocument(DropDownControl,
                        $"{typeof(ViewModelStateDemoArea).Assembly.GetName().Name}.Markdown.DropDown.md")
            )
            );
}
