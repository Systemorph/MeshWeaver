// <meshweaver>
// Id: LineOfBusinessViews
// DisplayName: Line of Business Views
// </meshweaver>

using System.ComponentModel.DataAnnotations;
using System.Reactive.Linq;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;

/// <summary>
/// Views for displaying line of business details.
/// </summary>
public static class LineOfBusinessViews
{
    public static LayoutDefinition AddLineOfBusinessViews(this LayoutDefinition layout) =>
        layout
            .WithView(nameof(Overview), Overview);

    /// <summary>
    /// Detail view showing line of business information including description and product examples.
    /// </summary>
    [Display(GroupName = "Overview", Order = 1)]
    public static IObservable<UiControl?> Overview(this LayoutAreaHost host, RenderingContext _)
    {
        return host.GetDataStream<LineOfBusiness>()
            .Select(lob =>
            {
                if (lob == null)
                    return null;

                var stack = Controls.Stack
                    .WithView(Controls.H2(lob.DisplayName))
                    .WithView(Controls.Markdown(lob.Description ?? ""));

                if (!string.IsNullOrEmpty(lob.ProductExamples))
                {
                    stack = stack
                        .WithView(Controls.H3("Insurance Products"))
                        .WithView(Controls.Markdown(string.Join("\n",
                            lob.ProductExamples.Split(',', StringSplitOptions.TrimEntries)
                                .Select(p => $"- {p}"))));
                }

                return (UiControl?)stack;
            });
    }
}
