using MeshWeaver.Application.Styles;
using MeshWeaver.Data;
using MeshWeaver.Domain.Layout;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Layout.Domain;

namespace MeshWeaver.Demo.ViewModel;

/// <summary>
/// Defines a static class within the MeshWeaver.Demo.ViewModel namespace for creating and managing a ViewModel State view.
/// </summary>
public static class ViewModelStateDemoArea
{
    public static LayoutDefinition AddViewModelStateDemo(this LayoutDefinition layout)
        => layout.WithView(nameof(CounterLayoutArea.Counter), CounterLayoutArea.Counter)
            .WithNavMenu((menu, _, _) => menu
                .WithNavLink(
                    "Raw: ViewModel State",
                    new LayoutAreaReference(nameof(CounterLayoutArea.Counter)).ToAppHref(layout.Hub.Address),
                    FluentIcons.Box
                )
            );
}
