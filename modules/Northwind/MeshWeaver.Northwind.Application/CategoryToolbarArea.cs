using System.Reactive.Linq;
using MeshWeaver.Data;
using MeshWeaver.DataCubes;
using MeshWeaver.Domain;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Northwind.Domain;

namespace MeshWeaver.Northwind.Application;

/// <summary>
/// Represents a toolbar for category selection with a specific category.
/// </summary>
/// <param name="Category">The category ID.</param>
public record CategoryToolbar([property: Dimension<Category>] int Category);

/// <summary>
/// Provides extension methods for creating category selection toolbars.
/// </summary>
public static class CategoryToolbarArea
{
    /// <summary>
    /// Creates a category selection toolbar for the specified layout area and rendering context.
    /// </summary>
    /// <param name="layoutArea">The layout area host.</param>
    /// <param name="context">The rendering context.</param>
    /// <returns>An object representing the category selection toolbar.</returns>
    public static UiControl CategoryToolbar(this LayoutAreaHost layoutArea, RenderingContext context)
    {
        var _ = context;
        return Controls.Toolbar.WithView((_, _) =>
            layoutArea.GetAvailableCategories()
                .Select(categories =>
                    Template.Bind(new CategoryToolbar(0),
                        tb =>
                            Controls.Select(
                                tb.Category,
                                categories.Select<Category, Option<int>>(c =>
                                        new Option<int>(c.CategoryId, c.CategoryName))
                                    .Prepend(new Option<int>(0, "All Categories"))
                                    .Cast<Option>()
                                    .ToArray()
                            ),
                        nameof(CategoryToolbar))
                )
        );
    }

    private static IObservable<IEnumerable<Category>> GetAvailableCategories(this LayoutAreaHost layoutArea)
        => layoutArea.Workspace.GetStream(typeof(Category))
            .DistinctUntilChanged()
            .CombineLatest(layoutArea.YearlyNorthwindData()
                    .Select(data => data.ToDataCube().GetSlices(nameof(Category))
                        .SelectMany(d => d.Tuple)
                        .Select(tuple => tuple.Value)
                        .Distinct()
                    ),
                (changeItem, values) => (changeItem, values))
            .Select(tp => tp.changeItem.Value!.GetData<Category>()
                .Where(c => tp.values.Contains(c.CategoryId))
                .OrderBy(c => c.CategoryName)
            );
}
