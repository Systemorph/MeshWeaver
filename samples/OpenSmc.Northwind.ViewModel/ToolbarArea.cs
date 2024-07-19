using System.Reactive.Linq;
using OpenSmc.Layout;
using OpenSmc.Layout.Composition;

namespace OpenSmc.Northwind.ViewModel;

/// <summary>
/// Simple toolbar entry capturing the year
/// </summary>
/// <param name="Year"></param>
public record Toolbar(int Year);

/// <summary>
/// Utilities around using the toolbar
/// </summary>
public static class ToolbarArea
{
    /// <summary>
    /// Creates a toolbar and binds the data.
    /// </summary>
    /// <param name="area"></param>
    /// <param name="years"></param>
    /// <returns></returns>
    public static object Toolbar(this LayoutAreaHost area, IObservable<Option<int>[]> years)
        => Controls.Toolbar()
            .WithView(
                (a, _) =>
                    years.Select(y =>
                        a.Bind(
                            new Toolbar(y.Max(x => x.Item)),
                            nameof(ViewModel.Toolbar),
                            tb => Controls.Select(tb.Year).WithOptions(y)
                        )
                    )
            );

}
