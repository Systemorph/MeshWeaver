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
                Controls.Listbox("yellow")
                    .WithOptions([
                        new Option<string>("red", "Red"),
                        new Option<string>("green", "Green"),
                        new Option<string>("blue", "Blue"),
                        new Option<string>("yellow", "Yellow"),
                        new Option<string>("magenta", "Magenta"),
                        new Option<string>("cyan", "Cyan"),
                    ])
            )
        ;
}
