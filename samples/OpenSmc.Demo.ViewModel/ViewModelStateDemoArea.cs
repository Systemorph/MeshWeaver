using OpenSmc.Application.Styles;
using OpenSmc.Layout;
using OpenSmc.Layout.Composition;
using OpenSmc.Northwind.ViewModel;

namespace OpenSmc.Demo.ViewModel;

/// <summary>
/// Defines a static class within the OpenSmc.Demo.ViewModel namespace for creating and managing a ViewModel State view.
/// </summary>
public static class ViewModelStateDemoArea
{
    public static LayoutDefinition AddViewModelStateDemo(this LayoutDefinition layout)
        => layout.WithView(nameof(CounterLayoutArea.Counter), CounterLayoutArea.Counter,
                options => options
                    .WithMenu(Controls.NavLink("Raw: ViewModel State", FluentIcons.Box,
                        layout.ToHref(new(nameof(CounterLayoutArea.Counter)))))
        );
}
