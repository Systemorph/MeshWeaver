using MeshWeaver.Application.Styles;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Layout.Domain;

namespace MeshWeaver.Demo.ViewModel.DropDown;

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
