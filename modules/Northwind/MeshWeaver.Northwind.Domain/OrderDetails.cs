using System.ComponentModel.DataAnnotations;
using MeshWeaver.Application.Styles;
using MeshWeaver.Domain;

namespace MeshWeaver.Northwind.Domain;

/// <summary>
/// Represents the detailed line items for an order within the Northwind domain, including product information, pricing, and quantity.
/// </summary>
/// <param name="Id">The Primary Key. This is not originally part of Northwind,
/// however we believe that it belongs to this form of normalized model.
/// In modern data modelling, this would be an inline property of Order.</param>
/// <param name="OrderId">The identifier of the order this detail belongs to, linking to an <see cref="Order"/>.</param>
/// <param name="ProductId">The identifier of the product for this order detail, linking to a <see cref="Product"/>.</param>
/// <param name="UnitPrice">The price per unit of the product.</param>
/// <param name="Quantity">The quantity of the product ordered.</param>
/// <param name="Discount">The discount applied to this order detail, if any.</param>
/// <remarks>
/// This record is decorated with an <see cref="IconAttribute"/> to specify its visual representation. Additionally, it includes a unique identifier <see cref="Id"/> for internal use, which is automatically generated and has no semantic meaning.
/// </remarks>
/// <seealso cref="IconAttribute"/>
/// <seealso cref="NotVisibleAttribute"/>
[Icon(FluentIcons.Provider, "Album")]
public record OrderDetails(
    [property: Key] int Id,
    int OrderId,
    [property: Dimension(typeof(Product))] int ProductId,
    double UnitPrice,
    int Quantity,
    double Discount
);
