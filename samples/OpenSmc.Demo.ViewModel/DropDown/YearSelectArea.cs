using OpenSmc.Layout;
using OpenSmc.Layout.Composition;

namespace OpenSmc.Demo.ViewModel.DropDown;

public static class YearSelectArea
{
    public static object YearSelect(LayoutAreaHost area, RenderingContext context)
        => Controls
            .Stack()
            .WithView(
                "SelectYear",
                Controls.Select(2022).WithOptions([new Option<int>(2023, "2023"), new Option<int>(2022, "2022"), new Option<int>(2021, "2021"),])
            )
        ;
}
