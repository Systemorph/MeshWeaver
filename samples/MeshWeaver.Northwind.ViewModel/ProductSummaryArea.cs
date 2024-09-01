using MeshWeaver.Application.Styles;
using MeshWeaver.Demo.ViewModel;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Layout.Domain;

namespace MeshWeaver.Northwind.ViewModel;

/// <summary>
/// Defines a static class within the MeshWeaver.Northwind.ViewModel namespace for creating and managing a Product Summary view. This view provides a comprehensive overview of products, including details such as name, category, and stock levels.
/// </summary>
public static class ProductSummaryArea
{

    /// <summary>
    /// Registers the Product Summary view to the specified layout definition.
    /// </summary>
    /// <param name="layout">The layout definition to which the Product Summary view will be added.</param>
    /// <returns>The updated layout definition including the Product Summary view.</returns><remarks>
    /// This method enhances the provided layout definition by adding a navigation link to the Product Summary view, using the FluentIcons.
    /// Box icon for the menu. It configures the Product Summary view's appearance and behavior within the application's navigation structure.
    /// </remarks>
    public static LayoutDefinition AddProductsSummary(this LayoutDefinition layout)
        => layout.WithView(nameof(ProductSummary), ProductSummary)
            .WithNavMenu((menu, _, _) => menu.WithNavLink("Product Summary",
                new LayoutAreaReference(nameof(ProductSummary)).ToAppHref(layout.Hub.Address), FluentIcons.Box)
            );


    /// <summary>
    /// Generates the Product Summary view for a given layout area and rendering context.
    /// </summary>
    /// <param name="layoutArea">The layout area host where the Product Summary view will be displayed.</param>
    /// <param name="ctx">The rendering context for generating the view.</param>
    /// <returns>A LayoutStackControl object representing the Product Summary view.</returns>
    /// <remarks>
    /// This method constructs the Product Summary view, incorporating various UI components to display detailed product information. The specific contents and layout of the view are determined at runtime based on the rendering context.
    /// </remarks>
    public static LayoutStackControl ProductSummary(
        this LayoutAreaHost layoutArea,
        RenderingContext ctx
    ) =>
        Controls.Stack
            .WithView(Controls.PaneHeader("Product Summary"))
            .WithView(CounterLayoutArea.Counter);

}
