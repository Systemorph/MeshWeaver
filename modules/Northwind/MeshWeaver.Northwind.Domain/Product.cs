using System.ComponentModel.DataAnnotations;
using MeshWeaver.Application.Styles;
using MeshWeaver.Domain;

namespace MeshWeaver.Northwind.Domain
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
    [Icon(FluentIcons.Provider, "Album")]
    public record Product(
        [property: Key] int ProductId,
        string ProductName,
        int SupplierId,
        int CategoryId,
        string QuantityPerUnit,
        decimal UnitPrice,
        short UnitsInStock,
        short UnitsOnOrder,
        short ReorderLevel,
        string Discontinued
    ) : INamed
    {
        string INamed.DisplayName => ProductName;
    }
}
