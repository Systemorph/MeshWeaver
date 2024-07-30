using OpenSmc.Layout.Composition;
using OpenSmc.Layout;

namespace OpenSmc.Demo.ViewModel.Listbox;

public static class BaseColorListArea
{
    public static object BaseColorList(LayoutAreaHost area, RenderingContext context)
        => Controls
            .Stack()
            .WithView(
                "BaseColorSelect",
                Controls.Listbox("Y")
                    .WithOptions([
                        new Option<string>("R", "Red"),
                        new Option<string>("G", "Green"),
                        new Option<string>("B", "Blue"),
                        new Option<string>("Y", "Yellow"),
                        new Option<string>("M", "Magenta"),
                        new Option<string>("C", "Cyan"),
                    ])
            )
        ;
}
