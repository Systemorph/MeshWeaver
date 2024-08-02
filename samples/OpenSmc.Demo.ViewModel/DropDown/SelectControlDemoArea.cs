using OpenSmc.Application.Styles;
using OpenSmc.Layout;
using OpenSmc.Layout.Composition;

namespace OpenSmc.Demo.ViewModel.DropDown;

public static class SelectControlDemoArea
{
    public static LayoutDefinition AddSelectControlDemo(this LayoutDefinition layout)
        => layout.WithView(nameof(YearSelectArea.YearSelect), YearSelectArea.YearSelect,
                options => options
                    .WithMenu(Controls.NavLink("Raw: DropDown Control", FluentIcons.CalendarDataBar,
                        layout.ToHref(new(nameof(YearSelectArea.YearSelect)))))
        );
}
