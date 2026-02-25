// <meshweaver>
// Id: OrderDetails
// DisplayName: Order Details
// </meshweaver>

using System.ComponentModel.DataAnnotations;
using MeshWeaver.Domain;

/// <summary>
/// Order detail line item loaded from CSV.
/// </summary>
public record OrderDetails
{
    [Key]
    public int Id { get; init; }

    public int OrderId { get; init; }

    [Dimension(typeof(Product))]
    public int ProductId { get; init; }

    public double UnitPrice { get; init; }

    public int Quantity { get; init; }

    public double Discount { get; init; }
}
