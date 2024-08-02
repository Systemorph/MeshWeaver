using OpenSmc.Application.Styles;
using OpenSmc.Layout;
using OpenSmc.Layout.Composition;
using OpenSmc.Layout.Domain;
using OpenSmc.Northwind.ViewModel;

namespace OpenSmc.Demo.ViewModel;

/// <summary>
/// Defines a static class within the OpenSmc.Demo.ViewModel namespace for creating and managing a ViewModel State view.
/// </summary>
public static class ViewModelStateDemoArea
{
    public static LayoutDefinition AddViewModelStateDemo(this LayoutDefinition layout)
        => layout.WithView(nameof(CounterLayoutArea.Counter), CounterLayoutArea.Counter)
            .WithNavMenu((menu, _) => menu
                .WithNavLink(
                    "Raw: ViewModel State",
                    new LayoutAreaReference(nameof(CounterLayoutArea.Counter)).ToHref(layout.Hub.Address),
                    FluentIcons.Box
                )
            );
}
