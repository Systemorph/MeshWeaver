using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using MeshWeaver.Domain;

namespace MeshWeaver.Import.Test.Domain
{
    /// <summary>
    /// Represents a product in the Northwind domain. This record encapsulates all relevant details about a product, including its supplier, category, pricing, and stock levels.
    /// </summary>
    /// <param name="ProductId">The unique identifier for the product. This is marked as the primary key.</param>
    /// <param name="ProductName">The name of the product.</param>
    /// <param name="SupplierId">The identifier of the supplier for this product, linking to a <see cref="Supplier"/>.</param>
    /// <param name="CategoryId">The identifier of the category this product belongs to, linking to a <see cref="Category"/>.</param>
    /// <param name="QuantityPerUnit">The quantity per unit for the product.</param>
    /// <param name="UnitPrice">The price per unit of the product.</param>
    /// <param name="UnitsInStock">The current number of units in stock.</param>
    /// <param name="UnitsOnOrder">The number of units currently on order.</param>
    /// <param name="ReorderLevel">The inventory level at which a reorder is triggered.</param>
    /// <param name="Discontinued">Indicates whether the product is discontinued. Expected to be a boolean value represented as a string.</param>
    /// <remarks>
    /// Implements the <see cref="INamed"/> interface, providing a DisplayName property that returns the ProductName. Decorated with an <see cref="IconAttribute"/> to specify its visual representation in UI components.
    /// </remarks>
    /// <seealso cref="INamed"/>
    /// <seealso cref="IconAttribute"/>
    public record Product(
        [property: Key] int ProductId,
        string ProductName,
        [property:DisplayName("Supplier")][property: Dimension(typeof(Supplier))] int SupplierId,
        [property: DisplayName("Category")][property: Dimension(typeof(Category))] int CategoryId,
        string QuantityPerUnit,
        double UnitPrice,
        short UnitsInStock,
        short UnitsOnOrder,
        short ReorderLevel,
        string Discontinued
    ) : INamed
    {
        string INamed.DisplayName => ProductName;
    }
}
