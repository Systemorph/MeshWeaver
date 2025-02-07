using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using static MeshWeaver.Layout.Controls;

namespace MeshWeaver.Documentation.LayoutAreas;

/// <summary>
/// This is the layout area as demonstrated in the article
/// "From Specification to Implementation". It is an example how
/// we can move from interactive notebooks to production code.
/// </summary>
public static class CalculatorLayoutArea
{
    /// <summary>
    /// This method helps us to add the calculator to a layout.
    /// </summary>
    /// <param name="layout">The layout to add the calculator to.</param>
    /// <returns></returns>
    public static LayoutDefinition AddCalculator(this LayoutDefinition layout)
            => layout.WithView(nameof(Calculator), CalculatorArea);

    /// <summary>
    /// This is the main data type. In our case, we just keep it private, as
    /// it is not used outside.
    /// </summary>
    private record Calculator
    {
        /// <summary>First summand</summary>
        public double Summand1 { get; init; }

        /// <summary>Second summand</summary>
        public double Summand2 { get; init; }

    }

    /// <summary>
    /// A simple calculator to sum up two numbers. 
    /// </summary>
    /// <param name="host">The layout area host</param>
    /// <param name="ctx">The rendering context.</param>
    public static object CalculatorArea(LayoutAreaHost host, RenderingContext ctx) 
        => host.Edit(new Calculator{Summand1 = 1, Summand2 = 2}, CalculatorSum);

    /// <summary>
    /// The business logic for how to compute the sum.
    /// </summary>
    /// <param name="calculator">The calculator instance</param>
    /// <returns>A Markdown Control with the sum.</returns>
    static object CalculatorSum(Calculator calculator) => Markdown($"**Sum**: {calculator.Summand1 + calculator.Summand2}");
}
