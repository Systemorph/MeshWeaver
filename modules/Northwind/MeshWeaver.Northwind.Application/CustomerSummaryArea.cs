﻿using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;

namespace MeshWeaver.Northwind.Application;

/// <summary>
/// Provides a static view for customer summaries within the Northwind application. This class includes methods to register and generate the customer summary view within a specified layout.
/// </summary>
public static class CustomerSummaryArea
{
    /// <summary>
    /// Registers the customer summary view to the provided layout definition.
    /// </summary>
    /// <param name="layout">The layout definition to which the customer summary view will be added.</param>
    /// <returns>The updated layout definition including the customer summary view.</returns>
    /// <remarks> This method enhances the provided layout definition by adding a navigation link to the customer summary view and configuring the view's appearance and behavior.
    /// </remarks>
    public static LayoutDefinition AddCustomerSummary(this LayoutDefinition layout)
        => layout.WithView(nameof(CustomerSummary), CustomerSummary)
        ;

    /// <summary>
    /// Generates the customer summary view for a given layout area and rendering context.
    /// </summary>
    /// <param name="layoutArea">The layout area host where the customer summary view will be displayed.</param>
    /// <param name="ctx">The rendering context for generating the view.</param>
    /// <returns>A layout stack control representing the customer summary.</returns>
    /// <remarks>This method constructs a stack control that includes a pane header titled "Customer Summary". Additional views can be added to the stack to complete the summary display.
    /// </remarks>
    public static UiControl CustomerSummary(
        this LayoutAreaHost layoutArea,
        RenderingContext ctx
    ) =>
        Controls.Markdown("TODO");
}
