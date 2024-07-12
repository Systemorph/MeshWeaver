using OpenSmc.Application.Styles;
using OpenSmc.Domain;

namespace OpenSmc.Northwind.Domain;

/// <summary>
/// Order details describing the product, unit price, quantity, and discount.
/// </summary>
/// <param name="OrderId">Id of the order linking to <see cref="Order"/></param>
/// <param name="ProductId">Id of the product linking to <see cref="Product"/></param>
/// <param name="UnitPrice">Price per unit</param>
/// <param name="Quantity">Quantity</param>
/// <param name="Discount">Discount</param>
[Icon(FluentIcons.Provider, "Album")]
public record OrderDetails(
    int OrderId,
    [property: Dimension(typeof(Product))] int ProductId,
    double UnitPrice,
    int Quantity,
    double Discount
)
{
    /// <summary>
    /// Ids should be generated depending on data storage (e.g. auto-numbering long), string, Guid, etc. No semantic meaning can be given to the ID.
    /// </summary>
    [NotVisible]
    public Guid Id { get; init; } = Guid.NewGuid();

}
