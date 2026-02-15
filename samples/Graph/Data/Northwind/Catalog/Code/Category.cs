// <meshweaver>
// Id: Category
// DisplayName: Product Category
// </meshweaver>

using System.ComponentModel.DataAnnotations;
using MeshWeaver.Domain;

/// <summary>
/// Product category reference data.
/// </summary>
public record Category : INamed
{
    [Key]
    public int CategoryId { get; init; }

    [Required]
    public string CategoryName { get; init; } = string.Empty;

    public string Description { get; init; } = string.Empty;

    string INamed.DisplayName => CategoryName;

    public static readonly Category[] All =
    [
        new() { CategoryId = 1, CategoryName = "Beverages", Description = "Soft drinks, coffees, teas, beers, and ales" },
        new() { CategoryId = 2, CategoryName = "Condiments", Description = "Sweet and savory sauces, relishes, spreads, and seasonings" },
        new() { CategoryId = 3, CategoryName = "Confections", Description = "Desserts, candies, and sweet breads" },
        new() { CategoryId = 4, CategoryName = "Dairy Products", Description = "Cheeses" },
        new() { CategoryId = 5, CategoryName = "Grains/Cereals", Description = "Breads, crackers, pasta, and cereal" },
        new() { CategoryId = 6, CategoryName = "Meat/Poultry", Description = "Prepared meats" },
        new() { CategoryId = 7, CategoryName = "Produce", Description = "Dried fruit and bean curd" },
        new() { CategoryId = 8, CategoryName = "Seafood", Description = "Seaweed and fish" },
    ];
}
