---
Title: "From Specification to Implementation"
Abstract: >
  Whilst Notebooks or Interactive Markdown are great for prototyping, they are not
  suitable for production. In production, we need to have a clear separation of
  concerns between data, business logic, and presentation. In this article, we show
  the path from specification to implementation.    
Thumbnail: "images/Specification to Implementation.jpeg"
Published: "2025-02-04"
VideoUrl: "https://www.youtube.com/embed/soujo5VBE00?si=XDLP2Bik8pg1DqBp"
VideoDuration: "00:15:11"
Authors:
  - "Roland Bürgi"
Tags:
  - "Documentation"
  - "Specification"
  - "Layout Area"
  - "Markdown"
---
Whilst Interactive Markdown is a great tool to work 
interacively on specification and make fast progress, 
it is less useful to write production code. In production, 
we need to have a clear separation of concerns between data, 
business logic, and presentation. In this article, 
we show the path from specification to implementation.

In this article, we will show how to transform a specification 
into a production-ready implementation. We will use the example 
of the simple calculator layout area as elaborated in the 
previous article.

Let us start by porting the example to this markdown document. 

```csharp --render Calculator --show-code
using MeshWeaver.Layout;
using MeshWeaver.Mesh;
using static MeshWeaver.Layout.Controls;
record Calculator(double Summand1, double Summand2);
static object CalculatorSum(Calculator c) => Markdown($"**Sum**: {c.Summand1 + c.Summand2}");
Mesh.Edit(new Calculator(1,2), CalculatorSum)
```

Now we will transform the specification into a production-ready implementation. We recommend to create a single static class for every layout area. This class should contain the data structure, the business logic, and the presentation logic. The data structure can be shared with other components, if they are global data models, or they can just be local view models private to the layout area. The business logic should be properly factored out, however, in this example, it is simple enough to just be included in the static class as well. 

The resulting code looks like this:
```csharp
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
```

We can link the calculator to the main documentation hub:

```csharp
/// <summary>
/// Main entry point to the Document module View Models.
/// By adding a mesh node or a hosted hub,
/// you can install the views from the Documentation module.
/// </summary>
public static class DocumentationViewModels
{
    /// <summary>
    /// This method adds the views defined in the documentation module to
    /// the Documentation hub.
    /// </summary>
    /// <param name="config"></param>
    /// <returns></returns>
    public static MessageHubConfiguration AddDocumentation(MessageHubConfiguration config)
        => config.AddLayout(layout => layout
            // ...
            .AddCalculator()
            // ...
        );

}
```

The calculator will be automatically added to the [Layout Area Catalog](/app/Documentation/LayoutAreas). Eventually, we can include the calculator as a normal layout area:
```csharp --render Calculator-Area --show-code
LayoutArea(new ApplicationAddress("Documentation"), "Calculator")
```