using System.Reactive.Linq;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Layout;

namespace MeshWeaver.Demo.ViewModel.Listbox;

public static class BaseColorListArea
{
    public static object BaseColorList(LayoutAreaHost area, RenderingContext context)
        => Controls
            .Stack
            .WithOrientation(Orientation.Horizontal)
            .WithHorizontalGap(16)
            .WithView(
                (_, _) =>
                    Template.Bind(
                        new ChosenColor("yellow"),
                        nameof(ChosenColor),
                        cc => Controls.Listbox(cc.Color)
                            .WithOptions(new Option[]
                            {
                                new Option<string>("red", "Red"),
                                new Option<string>("green", "Green"),
                                new Option<string>("blue", "Blue"),
                                new Option<string>("yellow", "Yellow"),
                                new Option<string>("magenta", "Magenta"),
                                new Option<string>("cyan", "Cyan"),
                            })
                    )
            )
            .WithView((a, _) => a
                .GetDataStream<ChosenColor>(nameof(ChosenColor))
                .Select(cc => ShowSelectedColor(cc.Color)), nameof(ShowSelectedColor))
        ;

    private static object ShowSelectedColor(string color)
        => Controls.Html($"<div style=\"width: 50px; height: 50px; background-color: {color}\"></div>");
}

internal record ChosenColor(string Color);
