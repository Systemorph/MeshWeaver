using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Threading.Tasks;
using FluentAssertions;
using MeshWeaver.Domain;
using MeshWeaver.Fixture;
using MeshWeaver.Messaging;
using Xunit;

namespace MeshWeaver.Data.Test;

/// <summary>
/// Test data records for virtual data source testing
/// </summary>
public record Product(
    [property: Key] int Id,
    string Name,
    int CategoryId,
    decimal Price
);

public record Category(
    [property: Key] int Id,
    string Name
) : INamed
{
    string INamed.DisplayName => Name;
}

/// <summary>
/// Virtual data cube that combines Product and Category
/// </summary>
public record ProductSummary(
    [property: Key] int ProductId,
    string ProductName,
    int CategoryId,
    string CategoryName,
    decimal Price
);

/// <summary>
/// Tests for VirtualDataSource functionality
/// </summary>
public class VirtualDataSourceTest(ITestOutputHelper output) : HubTestBase(output)
{
    private readonly BehaviorSubject<IEnumerable<Product>> productsSubject = new(
        [
            new Product(1, "Product A", 1, 100m),
            new Product(2, "Product B", 2, 200m),
            new Product(3, "Product C", 1, 150m)
        ]
    );

    private readonly BehaviorSubject<IEnumerable<Category>> categoriesSubject = new(
        [
            new Category(1, "Category 1"),
            new Category(2, "Category 2")
        ]
    );

    /// <summary>
    /// Configures the host message hub with source data and virtual data source
    /// </summary>
    protected override MessageHubConfiguration ConfigureHost(MessageHubConfiguration configuration)
    {
        return base.ConfigureHost(configuration)
            .AddData(data =>
                data
                    // Add source data
                    .AddSource(ds => ds.WithType<Product>(ts => ts.WithInitialData(() =>
                    [
                        new Product(1, "Product A", 1, 100m),
                        new Product(2, "Product B", 2, 200m),
                        new Product(3, "Product C", 1, 150m)
                    ])))
                    .AddSource(ds => ds.WithType<Category>(ts => ts.WithInitialData(() =>
                    [
                        new Category(1, "Category 1"),
                        new Category(2, "Category 2")
                    ])))
                    // Add virtual data source
                    .WithVirtualDataSource("ProductSummary", vds =>
                        vds.WithVirtualType<ProductSummary>(workspace =>
                        {
                            var productsStream = workspace.GetStream(typeof(Product));
                            var categoriesStream = workspace.GetStream(typeof(Category));

                            return Observable.CombineLatest(
                                productsStream,
                                categoriesStream,
                                (products, categories) =>
                                {
                                    var categoryLookup = categories.Value!.GetData<Category>()
                                        .ToDictionary(c => c.Id, c => c.Name);

                                    return products.Value!.GetData<Product>()
                                        .Select(p => new ProductSummary(
                                            p.Id,
                                            p.Name,
                                            p.CategoryId,
                                            categoryLookup.TryGetValue(p.CategoryId, out var catName) ? catName : "Unknown",
                                            p.Price
                                        ))
                                        .AsEnumerable();
                                }
                            );
                        })
                    )
            );
    }

    /// <summary>
    /// Tests that virtual data source correctly initializes with combined data
    /// </summary>
    [Fact]
    public async Task VirtualDataSource_Initializes_WithCombinedData()
    {
        // Arrange
        var workspace = GetHost().GetWorkspace();

        // Act
        var stream = workspace.GetStream(typeof(ProductSummary));
        var data = await stream.Take(1).Timeout(TimeSpan.FromSeconds(5));

        // Assert
        var summaries = data.Value!.GetData<ProductSummary>().ToArray();
        summaries.Should().HaveCount(3);
        summaries.Should().Contain(s => s.ProductId == 1 && s.ProductName == "Product A" && s.CategoryName == "Category 1");
        summaries.Should().Contain(s => s.ProductId == 2 && s.ProductName == "Product B" && s.CategoryName == "Category 2");
        summaries.Should().Contain(s => s.ProductId == 3 && s.ProductName == "Product C" && s.CategoryName == "Category 1");
    }

    /// <summary>
    /// Tests that virtual data source can be queried from workspace
    /// </summary>
    [Fact]
    public async Task VirtualDataSource_CanBeQueried_FromWorkspace()
    {
        // Arrange
        var workspace = GetHost().GetWorkspace();

        // Act
        var stream = workspace.GetStream(typeof(ProductSummary));
        var data = await stream.Take(1).Timeout(TimeSpan.FromSeconds(5));

        // Assert
        var summary = data.Value!.GetData<ProductSummary>().First(s => s.ProductId == 1);
        summary.ProductName.Should().Be("Product A");
        summary.CategoryName.Should().Be("Category 1");
        summary.Price.Should().Be(100m);
    }

    /// <summary>
    /// Tests that virtual data combines data from multiple sources correctly
    /// </summary>
    [Fact]
    public async Task VirtualDataSource_CombinesData_FromMultipleSources()
    {
        // Arrange
        var workspace = GetHost().GetWorkspace();

        // Act
        var stream = workspace.GetStream(typeof(ProductSummary));
        var data = await stream.Take(1).Timeout(TimeSpan.FromSeconds(5));

        // Assert
        var summaries = data.Value!.GetData<ProductSummary>().ToArray();

        // Verify that Category 1 has 2 products
        summaries.Where(s => s.CategoryId == 1).Should().HaveCount(2);

        // Verify that Category 2 has 1 product
        summaries.Where(s => s.CategoryId == 2).Should().HaveCount(1);

        // Verify all products have their category names populated
        summaries.Should().AllSatisfy(s => s.CategoryName.Should().NotBeNullOrEmpty());
        summaries.Should().AllSatisfy(s => s.CategoryName.Should().NotBe("Unknown"));
    }

    /// <summary>
    /// Tests that virtual data source respects keys from source data
    /// </summary>
    [Fact]
    public async Task VirtualDataSource_RespectsKeys_FromSourceData()
    {
        // Arrange
        var workspace = GetHost().GetWorkspace();

        // Act
        var stream = workspace.GetStream(typeof(ProductSummary));
        var data = await stream.Take(1).Timeout(TimeSpan.FromSeconds(5));

        // Assert
        var summaries = data.Value!.GetData<ProductSummary>().ToArray();

        // Each product should have a unique ProductId
        summaries.Select(s => s.ProductId).Should().OnlyHaveUniqueItems();

        // Product IDs should match the original products
        summaries.Select(s => s.ProductId).Should().BeEquivalentTo([1, 2, 3]);
    }
}
