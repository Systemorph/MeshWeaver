using OpenSmc.Application.Styles;
using OpenSmc.Layout;
using OpenSmc.Layout.Composition;

namespace OpenSmc.Northwind.ViewModel
{
    /// <summary>
    /// Todo need to redo
    /// </summary>
    public static class ProductSummaryArea
    {

        /// <summary>
        /// Add the product summary to layout
        /// </summary>
        /// <param name="layout"></param>
        /// <returns></returns>
        public static LayoutDefinition AddProductsSummary(this LayoutDefinition layout)
            => layout.WithView(nameof(ProductSummary), ProductSummary,
                options => options
                    .WithSourcesForTypes(typeof(ProductSummaryArea))
                    .WithMenu(Controls.NavLink("Product Summary", FluentIcons.Box,
                        layout.ToHref(new(nameof(ProductSummary)))))
            );


        /// <summary>
        /// Definition of product summary view.
        /// </summary>
        /// <param name="layoutArea"></param>
        /// <param name="ctx"></param>
        /// <returns></returns>
        public static LayoutStackControl ProductSummary(
            this LayoutAreaHost layoutArea,
            RenderingContext ctx
        ) =>
            Controls.Stack()
                .WithView(Controls.PaneHeader("Product Summary"))
                .WithView(CounterLayoutArea.Counter, o => o.WithSourcesForTypes(typeof(CounterLayoutArea)));

    }
}
