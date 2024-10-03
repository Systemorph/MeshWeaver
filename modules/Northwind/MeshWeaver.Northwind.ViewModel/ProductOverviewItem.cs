using System.ComponentModel.DataAnnotations;

namespace MeshWeaver.Northwind.ViewModel;

/// <summary>
/// Gets the unit price formatted as a numeric value with two decimal places.
/// /// Represents an overview item for a product.
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

    /// <summary>
    /// Gets the unit price.
    /// </summary>
    [DisplayFormat(DataFormatString = "N2")]
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

    /// <summary>
    /// Gets the total amount.
    /// </summary>
    [DisplayFormat(DataFormatString = "N2")]
    public double TotalAmount { get; init; }
}
