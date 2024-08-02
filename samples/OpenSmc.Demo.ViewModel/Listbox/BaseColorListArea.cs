using System.Reactive.Linq;
using OpenSmc.Layout.Composition;
using OpenSmc.Layout;

namespace OpenSmc.Demo.ViewModel.Listbox;

public static class BaseColorListArea
{
    public static object BaseColorList(LayoutAreaHost area, RenderingContext context)
        => Controls
            .Stack
            .WithVerticalGap(16)
            .WithView(
                (_, _) =>
                    Template.Bind(
                        new ChosenColor("yellow"),
                        nameof(ChosenColor),
                        cc => Controls.Listbox(cc.Color)
                            .WithOptions(new[]
                            {
                                new Option("red", "Red"),
                                new Option("green", "Green"),
                                new Option("blue", "Blue"),
                                new Option("yellow", "Yellow"),
                                new Option("magenta", "Magenta"),
                                new Option("cyan", "Cyan"),
                            })
                    )
            )
            .WithView(
                nameof(ShowSelectedColor),
                (a, _) => a
                    .GetDataStream<ChosenColor>(nameof(ChosenColor))
                    .Select(cc => ShowSelectedColor(cc.Color))
            )
        ;

    private static object ShowSelectedColor(string color)
        => Controls.Html($"<div style=\"width: 50px; height: 50px; background-color: {color}\"></div>");
}

internal record ChosenColor(string Color);
