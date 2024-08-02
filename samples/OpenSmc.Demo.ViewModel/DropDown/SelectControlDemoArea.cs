using OpenSmc.Application.Styles;
using OpenSmc.Layout;
using OpenSmc.Layout.Composition;
using OpenSmc.Layout.Domain;

namespace OpenSmc.Demo.ViewModel.DropDown;

public static class SelectControlDemoArea
{
    public static LayoutDefinition AddSelectControlDemo(this LayoutDefinition layout)
        => layout.WithView(nameof(YearSelectArea.YearSelect), YearSelectArea.YearSelect)
            .WithNavMenu((menu, _) => menu
                .WithNavLink(
                    "Raw: DropDown Control",
                    new LayoutAreaReference(nameof(YearSelectArea.YearSelect)).ToHref(layout.Hub.Address),
                    FluentIcons.CalendarDataBar
                )
            );
}
