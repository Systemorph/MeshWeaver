using System.Reactive.Linq;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;

namespace MeshWeaver.Demo.ViewModel.DropDown;

public static class YearSelectArea
{
    public static object YearSelect(LayoutAreaHost area, RenderingContext context)
        => Controls
            .Stack
            .WithVerticalGap(16)
            .WithView(
                (_, _) =>
                    Template.Bind(
                        new ChosenYear(2022),
                        nameof(ChosenYear),
                        sy => Controls.Select(sy.Year)
                            .WithOptions(new Option[]
                            {
                                new Option<int>(2023, "2023"),
                                new Option<int>(2022, "2022"),
                                new Option<int>(2021, "2021"),
                            })
                    )
            )
            .WithView((a, _) => a
                .GetDataStream<ChosenYear>(nameof(ChosenYear))
                .Select(x => ShowSelectedYear(x.Year)), nameof(ShowSelectedYear))
        ;

    private static object ShowSelectedYear(int year) => Controls.Html($"Year selected: {year}");
}

internal record ChosenYear(int Year);
