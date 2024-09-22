using System.ComponentModel.DataAnnotations;

namespace MeshWeaver.Northwind.ViewModel;

/// <summary>
/// Represents an overview item for a product.
/// </summary>
public record ProductOverviewItem
{
    /// <summary>
    /// Gets the product ID.
    /// </summary>
    public int ProductId { get; init; }
    
    /// <summary>
    /// Gets the product name.
    /// </summary>
    public string ProductName { get; init; }
    
    /// <summary>
    /// Gets the category name.
    /// </summary>
    public string CategoryName { get; init; }

    [DisplayFormat(DataFormatString = "N2")]
    /// <summary>
    /// Gets the unit price.
    /// </summary>
    public double UnitPrice { get; init; }

    /// <summary>
    /// Gets the number of units sold.
    /// </summary>
    public int UnitsSold { get; init; }

    /// <summary>
    /// Gets the discount given.
    /// </summary>
    [DisplayFormat(DataFormatString = "N2")]
    public double DiscountGiven { get; init; }

    [DisplayFormat(DataFormatString = "N2")]
    public double TotalAmount { get; init; }
}
