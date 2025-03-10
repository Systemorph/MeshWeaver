using System.Reactive.Linq;
using MeshWeaver.Data;
using MeshWeaver.DataCubes;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Northwind.Domain;

namespace MeshWeaver.Northwind.Application;

/// <summary>
/// Represents a toolbar for product overview with a specific category.
/// </summary>
/// <param name="Category">The category ID.</param>
public record ProductOverviewToolbar(int Category);

/// <summary>
/// Provides extension methods for creating product overview toolbars.
/// </summary>
public static class ProductOverviewToolbarArea
{
    /// <summary>
    /// Creates a product overview toolbar for the specified layout area and rendering context.
    /// </summary>
    /// <param name="layoutArea">The layout area host.</param>
    /// <param name="context">The rendering context.</param>
    /// <returns>An object representing the product overview toolbar.</returns>
    public static object ProductOverviewToolbar(this LayoutAreaHost layoutArea, RenderingContext context)
        => Controls.Toolbar.WithView((_, _) =>
            layoutArea.GetProductCategories()
                .Select(categories =>
                    Template.Bind(new ProductOverviewToolbar(0),
                        tb =>
                            Controls.Select(
                                tb.Category, 
                                Enumerable.Select<Category, Option<int>>(categories, c => new Option<int>(c.CategoryId, c.CategoryName))
                                    .Prepend(new Option<int>(0, "All Categories"))
                                    .Cast<Option>()
                                    .ToArray()
                                ),
                        nameof(ProductOverviewToolbar))
                )
        )
    ;

    private static IObservable<IEnumerable<Category>> GetProductCategories(this LayoutAreaHost layoutArea)
        => layoutArea.Workspace.GetStream(typeof(Category))
            .DistinctUntilChanged()
            .CombineLatest(layoutArea.YearlyNorthwindData()
                    .Select(data => data.ToDataCube().GetSlices(nameof(Category))
                        .SelectMany(d => d.Tuple)
                        .Select(tuple => tuple.Value)
                        .Distinct()
                    ),
                (changeItem, values) => (changeItem, values))
            .Select(tp => tp.changeItem.Value.GetData<Category>()
                .Where(c => tp.values.Contains(c.CategoryId))
                .OrderBy(c => c.CategoryName)
            );
}
